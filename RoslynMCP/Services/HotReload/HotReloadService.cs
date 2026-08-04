using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExternalAccess.UnitTesting.Api;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services.HotReload;

/// <summary>
/// Real Edit-and-Continue: Roslyn computes the metadata and IL delta for what changed, and the
/// delta is applied to the already-running process.
/// </summary>
/// <remarks>
/// <para>
/// The plan originally recommended shipping <c>dotnet watch</c> instead, on the grounds that
/// netcoredbg has no EnC support. That reasoning turned out to be aimed at the wrong component:
/// the debugger is not involved on CoreCLR at all. <see cref="MetadataUpdater"/> applies deltas
/// from inside the process — which is what <c>RoslynMCP.HotReloadAgent</c> is for — and on .NET
/// Framework the apply goes through <c>ICorDebugModule2::ApplyChanges</c> in the ICorDebug engine
/// this repo already owns. Neither path needs anything from netcoredbg.
/// </para>
/// <para>
/// What Roslyn supplies is the hard half: deciding whether an edit is even expressible as a delta.
/// <c>UnitTestingHotReloadService</c> is the supported entry point into that machinery — the same
/// engine behind Visual Studio's Apply Code Changes — so rude edits are reported by the compiler
/// that would have to emit them, rather than guessed at here.
/// </para>
/// </remarks>
internal sealed class HotReloadService
{
    /// <summary>
    /// What .NET Framework's EnC accepts. It predates the capability strings entirely, so there is
    /// nothing to ask; this is the classic ICorDebug set, and notably excludes everything generic.
    /// </summary>
    private static readonly string[] FrameworkCapabilities =
    [
        "Baseline",
        "AddMethodToExistingType",
        "AddStaticFieldToExistingType",
        "AddInstanceFieldToExistingType",
        "NewTypeDefinition",
    ];

    /// <summary>Used when nothing is attached yet, so an edit can still be checked for rude edits
    /// before anything is running.</summary>
    private static readonly string[] DefaultCapabilities =
    [
        "Baseline",
        "AddMethodToExistingType",
        "AddStaticFieldToExistingType",
        "AddInstanceFieldToExistingType",
        "NewTypeDefinition",
        "ChangeCustomAttributes",
    ];

    private static readonly ConcurrentDictionary<string, HotReloadService> s_sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly UnitTestingHotReloadService _encService;
    private readonly string _projectPath;

    /// <summary>Per-document file stamps, so an apply re-reads only what actually changed.</summary>
    private readonly Dictionary<DocumentId, (DateTime Written, long Length)> _stamps = [];

    /// <summary>
    /// Every text this session has read from disk, keyed by document.
    /// </summary>
    /// <remarks>
    /// The workspace snapshot never learns about these edits, but the committed EnC baseline
    /// does. Re-applying only what changed since the last apply would hand Roslyn the stale
    /// snapshot text for everything applied earlier — which diffs against the committed baseline
    /// as the user's edit being <em>reverted</em>, and emits a delta undoing it. So every apply
    /// overlays the full set, and the stamps only decide what to re-read.
    /// </remarks>
    private readonly Dictionary<DocumentId, SourceText> _texts = [];

    /// <summary>One apply at a time: the EnC service commits state per emit, and the stamp and
    /// text tables are plain dictionaries.</summary>
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private static readonly SemaphoreSlim s_startGate = new(1, 1);

    private HotReloadService(UnitTestingHotReloadService encService, string projectPath)
    {
        _encService = encService;
        _projectPath = projectPath;
    }

    public string ProjectPath => _projectPath;

    /// <summary>Whether a session is open for a project — an apply without one has no baseline to
    /// compare against and would report the whole project as changed.</summary>
    public static bool IsRunning(string projectPath) => s_sessions.ContainsKey(projectPath);

    public static HotReloadService? Get(string projectPath) => s_sessions.GetValueOrDefault(projectPath);

    /// <summary>Projects with an open edit session, which is what makes an apply incremental.</summary>
    public static IReadOnlyList<string> OpenSessions => [.. s_sessions.Keys];

    /// <summary>
    /// Opens an edit session, capturing the built output as the baseline every later delta is
    /// computed against.
    /// </summary>
    public static async Task<(HotReloadService? Session, string Message)> StartAsync(
        string projectPath, CancellationToken cancellationToken = default)
    {
        // Serialised: two concurrent starts would each open a Roslyn EnC session, and the
        // loser's would be overwritten in the table without ever being ended.
        await s_startGate.WaitAsync(cancellationToken);
        try
        {
            if (s_sessions.TryGetValue(projectPath, out var existing))
                return (existing, "A hot reload session is already open for this project.");

            var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
                projectPath, cancellationToken: cancellationToken);

            if (project.OutputFilePath is not { Length: > 0 } output || !File.Exists(output))
            {
                return (null,
                    "The project has not been built, so there is no baseline to diff against. " +
                    "Build it and start again.");
            }

            var capabilities = ResolveCapabilities(project);

            var service = new UnitTestingHotReloadService(workspace.Services);
            await service.StartSessionAsync(
                project.Solution, [.. capabilities], cancellationToken);

            var session = new HotReloadService(service, projectPath);
            session.RecordStamps(project.Solution);
            s_sessions[projectPath] = session;

            return (session, $"Hot reload session open with {capabilities.Count} runtime capabilities.");
        }
        finally
        {
            s_startGate.Release();
        }
    }

    /// <summary>
    /// Computes the delta for everything edited since the last apply and pushes it into whatever
    /// is running.
    /// </summary>
    /// <remarks>
    /// The updates are committed as they are emitted, which makes the next apply diff against this
    /// one rather than against the original build — otherwise every subsequent edit would resend
    /// the whole accumulated change.
    /// </remarks>
    public async Task<HotReloadOutcome> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            return await ApplyLockedAsync(cancellationToken);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<HotReloadOutcome> ApplyLockedAsync(CancellationToken cancellationToken)
    {
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(
            _projectPath, cancellationToken: cancellationToken);

        // The whole input to a hot reload is "what changed since the build", and the cached
        // snapshot refreshes only the one file a request names — which is no file at all here.
        // Without this, Roslyn diffs the loaded solution against itself and emits nothing.
        var solution = RefreshFromDisk(project.Solution);

        var (updates, diagnostics) = await _encService.EmitSolutionUpdateAsync(
            solution, commitUpdates: true, cancellationToken);

        var reported = diagnostics.Select(Describe).ToList();

        if (updates.IsDefaultOrEmpty)
        {
            // Errors and rude edits both land here: Roslyn emits nothing rather than something
            // wrong, so "no updates" plus diagnostics is a refusal, not a no-op.
            return reported.Any(d => d.Severity == "error")
                ? new HotReloadOutcome(false, "The edit cannot be applied to the running process.", reported, [], [])
                : new HotReloadOutcome(true, "No changes to apply.", reported, [], []);
        }

        var deltas = updates.Select(u => new HotReloadDelta(
            u.ModuleId,
            [.. u.MetadataDelta],
            [.. u.ILDelta],
            [.. u.PdbDelta],
            [.. u.UpdatedTypes])).ToList();

        var (applied, errors) = await HotReloadAgentServer.Instance.ApplyAsync(deltas, cancellationToken);

        var (frameworkApplied, frameworkErrors) = await ApplyToFrameworkSessionAsync(
            solution, deltas, cancellationToken);

        applied = [.. applied, .. frameworkApplied];
        errors = [.. errors, .. frameworkErrors];

        string summary = applied.Count == 0
            ? errors.Count > 0
                ? "The delta was computed but no running target accepted it."
                : "The delta was computed but nothing is running to apply it to."
            : $"Applied {deltas.Count} module update(s) to {string.Join(", ", applied)}.";

        return new HotReloadOutcome(applied.Count > 0, summary, reported, applied, errors);
    }

    /// <summary>
    /// Routes deltas into a live .NET Framework debug session, which is the only way onto the
    /// desktop runtime — there is no in-process updater there.
    /// </summary>
    /// <remarks>
    /// ICorDebug addresses modules by name rather than by MVID, so the ids Roslyn returns are
    /// mapped back through the built output. A module that is not loaded in the debuggee is
    /// skipped rather than reported as a failure: a solution can easily contain projects the
    /// running app never loads.
    /// </remarks>
    private static async Task<(IReadOnlyList<string> Applied, IReadOnlyList<string> Errors)>
        ApplyToFrameworkSessionAsync(
            Solution solution, IReadOnlyList<HotReloadDelta> deltas, CancellationToken cancellationToken)
    {
        var local = DebugSessionManager.GetSession() as Debugging.PublishingDebugBackend;
        var icor = local?.Inner as IcorDebugBackend;

        // The session can live in another process — the editor's F5 runs `--dap`, and an AI
        // session runs in its own MCP client — so a remote apply goes over the same command pipe
        // the debug bridge already uses.
        var remotePids = icor is not null
            ? []
            : Debugging.DebugStateStore.List()
                .Where(e => e.OwnerPid != Environment.ProcessId)
                .Select(e => e.OwnerPid)
                .ToList();

        if (icor is null && remotePids.Count == 0)
            return ([], []);

        var names = ModuleNames(solution);
        var applied = new List<string>();
        var errors = new List<string>();

        foreach (var delta in deltas)
        {
            if (!names.TryGetValue(delta.ModuleId, out string? assemblyName))
                continue;

            if (icor is not null)
            {
                var (ok, error) = await icor.ApplyDeltaAsync(
                    assemblyName, delta.MetadataDelta, delta.IlDelta, delta.PdbDelta, cancellationToken);
                Record(assemblyName, ok, error);
                continue;
            }

            foreach (int pid in remotePids)
            {
                var response = await Debugging.DebugCommandPipeServer.SendAsync(pid, new Debugging.DebugPipeRequest(
                    "apply_delta",
                    AssemblyName: assemblyName,
                    MetadataDelta: Convert.ToBase64String(delta.MetadataDelta),
                    IlDelta: Convert.ToBase64String(delta.IlDelta),
                    PdbDelta: Convert.ToBase64String(delta.PdbDelta)), cancellationToken);

                Record(assemblyName, response.Ok && response.Result?.StartsWith("Error") != true,
                    response.Error ?? response.Result ?? "");
            }
        }

        return (applied, errors);

        void Record(string assemblyName, bool ok, string error)
        {
            if (ok)
                applied.Add($"{assemblyName} (debuggee)");
            else if (!error.Contains("is not loaded", StringComparison.OrdinalIgnoreCase) &&
                     !error.Contains("does not debug .NET Framework", StringComparison.OrdinalIgnoreCase))
            {
                // A CoreCLR session refusing a Framework-only route is a skip, not a failure:
                // the fan-out reaches every published session, related to this edit or not.
                errors.Add($"{assemblyName}: {error}");
            }
        }
    }

    public void Stop()
    {
        s_sessions.TryRemove(_projectPath, out _);
        try { _encService.EndSession(); } catch { }
    }

    public static void StopAll()
    {
        foreach (var session in s_sessions.Values.ToList())
            session.Stop();
    }

    /// <summary>
    /// Asks whatever is running what it will accept, so an edit is judged against the real
    /// runtime rather than a guess.
    /// </summary>
    private static IReadOnlyList<string> ResolveCapabilities(Project project)
    {
        // The runtime is classified before any agent is asked: the agent server is process-wide,
        // so an agent from an unrelated CoreCLR app must never decide what the desktop runtime
        // accepts — a delta computed with CoreCLR capabilities is the documented way to crash
        // ICorDebug's ApplyChanges rather than get an error from it.
        if (project.FilePath is { Length: > 0 } path)
        {
            try
            {
                if (ProjectClassifier.Classify(path).DebugRuntime == DebugRuntime.NetFramework)
                    return FrameworkCapabilities;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var fromAgents = HotReloadAgentServer.Instance.Capabilities();
        return fromAgents.Count > 0 ? fromAgents : DefaultCapabilities;
    }

    /// <summary>Maps every built output in the solution to the simple assembly name ICorDebug
    /// knows it by.</summary>
    private static Dictionary<Guid, string> ModuleNames(Solution solution)
    {
        var names = new Dictionary<Guid, string>();

        foreach (var project in solution.Projects)
        {
            if (project.OutputFilePath is not { Length: > 0 } path || !File.Exists(path))
                continue;
            if (ReadModuleId(path) is not { } moduleId)
                continue;

            names[moduleId] = Path.GetFileNameWithoutExtension(path);
        }

        return names;
    }

    /// <summary>
    /// Pulls edited files back into the snapshot, so the diff is against what the user has now
    /// rather than against what was loaded.
    /// </summary>
    /// <remarks>
    /// Stamps rather than content: re-reading every file in a solution on every save would make
    /// apply-on-save cost proportional to the solution, not to the edit. An open editor buffer is
    /// skipped because the snapshot already carries it and disk says nothing about unsaved text.
    /// </remarks>
    private Solution RefreshFromDisk(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path || OpenDocumentStore.IsOpen(path))
                    continue;

                if (Stamp(path) is { } stamp &&
                    (!_stamps.TryGetValue(document.Id, out var known) || known != stamp) &&
                    Read(path) is { } text)
                {
                    _stamps[document.Id] = stamp;
                    _texts[document.Id] = text;
                }

                // The full overlay, not just this round's re-reads: earlier applies are in the
                // committed baseline but not in the workspace snapshot this fork starts from.
                if (_texts.TryGetValue(document.Id, out var current))
                    solution = solution.WithDocumentText(document.Id, current);
            }
        }

        return solution;
    }

    private void RecordStamps(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { Length: > 0 } path && Stamp(path) is { } stamp)
                    _stamps[document.Id] = stamp;
            }
        }
    }

    private static (DateTime Written, long Length)? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    /// <summary>Reads a document with an explicit encoding: a <see cref="SourceText"/> without one
    /// cannot have debug information emitted for it, and no PDB means no delta.</summary>
    private static SourceText? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return SourceText.From(stream, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Guid? ReadModuleId(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;

            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HotReloadDiagnostic Describe(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new HotReloadDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
            },
            span.Path ?? "",
            span.StartLinePosition.Line + 1);
    }
}
