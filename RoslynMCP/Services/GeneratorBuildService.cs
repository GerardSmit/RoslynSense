using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Language.Xml;

namespace RoslynMCP.Services;

/// <summary>
/// Builds project-referenced source generators whose output DLL does not exist yet, before the
/// workspace loads the projects that consume them.
/// </summary>
/// <remarks>
/// <para>
/// A <c>ProjectReference</c> with <c>OutputItemType="Analyzer"</c> is how a solution ships its own
/// source generator, and <c>MSBuildWorkspace</c>'s design-time evaluation resolves it to the
/// generator's <em>built</em> DLL without ever building it. On a fresh clone that DLL does not
/// exist, the generator silently never runs, and every partial method or type the solution expects
/// it to complete reads as a compile error — "No defining declaration found for implementing
/// declaration of partial method", on code that builds fine from the command line. DNN Platform's
/// <c>[DnnDeprecated]</c> generator is the canonical case.
/// </para>
/// <para>
/// This is the same shape of problem <see cref="RestoreService"/> exists for — a subprocess the
/// workspace itself will never run, without which the evaluation is quietly wrong — and it gets
/// the same treatment: run before the load, single-flighted per target, and a failure degrades the
/// load rather than refusing it. The scan reads project XML rather than an evaluation because it
/// runs before any project has been loaded; that is the point of it.
/// </para>
/// <para>
/// The "is it built" check is a <c>bin/**/&lt;AssemblyName&gt;.dll</c> probe, not an output-path
/// computation, so a generator built to a custom <c>OutputPath</c> would be re-built once per
/// process (see <see cref="s_builtThisSession"/>) — a few wasted seconds, against the alternative
/// of evaluating the generator project just to ask where its output goes.
/// </para>
/// </remarks>
internal static class GeneratorBuildService
{
    /// <summary>Generator project path → the build currently in flight for it.</summary>
    private static readonly SingleFlight s_inflight = new();

    /// <summary>
    /// Generator projects built successfully this session. Only consulted when the output probe
    /// says "not built": a generator with a custom <c>OutputPath</c> produces a DLL the probe
    /// cannot see, and without this memo every load would build it again. A <em>failed</em> build
    /// is deliberately not memoized — disk is the truth, and the next load retrying it is how a
    /// generator the user just fixed starts working without a server restart.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> s_builtThisSession =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures every source generator the <paramref name="projectPath"/> closure references via
    /// <c>OutputItemType="Analyzer"</c> has been built, building the missing ones. Returns without
    /// doing anything on the common path — every generator already has output on disk.
    /// </summary>
    /// <remarks>
    /// Callers must invoke this <em>before</em> taking any workspace load gate, for the same
    /// reason as <see cref="RestoreService.EnsureRestoredAsync"/>: it is a subprocess, and holding
    /// a gate across it makes one project's cold build everybody else's latency.
    /// </remarks>
    public static async Task EnsureGeneratorsBuiltAsync(
        string projectPath, CancellationToken cancellationToken, Action<string>? report = null)
    {
        List<string> unbuilt;
        try
        {
            unbuilt = FindUnbuiltGeneratorProjects(projectPath);
        }
        catch (Exception ex)
        {
            // The scan must never be the reason a project fails to open.
            Console.Error.WriteLine(
                $"[GeneratorBuild] Generator scan of '{Path.GetFileName(projectPath)}' failed: {ex.Message}");
            return;
        }

        foreach (string generator in unbuilt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (s_builtThisSession.ContainsKey(generator))
                continue;

            // The bin\ probe missed, but that only means "no output under bin\" — a generator
            // built to a custom OutputPath or an artifacts/ layout is built, just elsewhere.
            // Before paying a build subprocess (and the DLL write it produces, which is what used
            // to churn every loaded workspace), ask MSBuild where the output actually goes and
            // trust the file on disk. The evaluation is cached against the project file, so a
            // warm session answers this from a stat.
            if (await HasOutputAtMsBuildTargetPathAsync(generator, cancellationToken))
                continue;

            report?.Invoke($"Building source generator {Path.GetFileNameWithoutExtension(generator)}");

            var run = s_inflight.Start(generator, RunBuildAsync);
            try
            {
                await run.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Reported by RunBuildAsync and then let go: the consumer loads without its
                // generated code, which is a degraded project rather than a failed request.
            }
        }
    }

    /// <summary>
    /// Walks the <c>ProjectReference</c> closure of <paramref name="entryProjectPath"/> through
    /// the project XML and returns every analyzer-referenced project with no output DLL on disk,
    /// in discovery order. The whole closure is walked — not just direct references — because
    /// Roslyn loads transitive <c>ProjectReference</c>s into the same workspace, and each project
    /// in it applies its own analyzer references.
    /// </summary>
    internal static List<string> FindUnbuiltGeneratorProjects(string entryProjectPath)
    {
        var unbuilt = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        string entry = Path.GetFullPath(entryProjectPath);
        visited.Add(entry);
        pending.Enqueue(entry);

        while (pending.Count > 0)
        {
            string project = pending.Dequeue();
            foreach (var reference in ReadProjectReferences(project))
            {
                if (reference.IsAnalyzer
                    && File.Exists(reference.ProjectPath)
                    && !HasBuiltOutput(reference.ProjectPath)
                    && !unbuilt.Contains(reference.ProjectPath, StringComparer.OrdinalIgnoreCase))
                {
                    unbuilt.Add(reference.ProjectPath);
                }

                if (visited.Add(reference.ProjectPath) && File.Exists(reference.ProjectPath))
                    pending.Enqueue(reference.ProjectPath);
            }
        }

        return unbuilt;
    }

    /// <summary>One <c>ProjectReference</c> item as declared in a project file.</summary>
    internal readonly record struct ProjectReferenceItem(string ProjectPath, bool IsAnalyzer);

    /// <summary>
    /// The <c>ProjectReference</c> items of a project file, from its XML, cached on the file's
    /// identity. Namespace-agnostic (legacy project files carry the MSBuild namespace, SDK ones do
    /// not) and metadata is read from either shape — attribute or child element. An
    /// <c>Include</c> containing an MSBuild expression is skipped: resolving it needs the
    /// evaluation this scan exists to run before.
    /// </summary>
    private static IReadOnlyList<ProjectReferenceItem> ReadProjectReferences(string projectPath) =>
        PathHelper.FileDerived<IReadOnlyList<ProjectReferenceItem>>.Get(projectPath, static path =>
        {
            try
            {
                var document = Parser.ParseText(File.ReadAllText(path));
                var items = new List<ProjectReferenceItem>();

                foreach (var element in document.DescendantsByLocalName("ProjectReference"))
                {
                    string? include = element.GetAttributeValue("Include");
                    if (string.IsNullOrWhiteSpace(include) || include.Contains('$'))
                        continue;

                    string referencedPath;
                    try
                    {
                        referencedPath = Path.GetFullPath(Path.Combine(
                            Path.GetDirectoryName(path)!,
                            include.Replace('\\', Path.DirectorySeparatorChar)));
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    string? outputItemType =
                        element.GetAttributeValue("OutputItemType")
                        ?? element.GetElementByLocalName("OutputItemType")?.Value;

                    items.Add(new ProjectReferenceItem(
                        referencedPath,
                        string.Equals(outputItemType?.Trim(), "Analyzer", StringComparison.OrdinalIgnoreCase)));
                }

                return items;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A project file that cannot be read contributes nothing to the scan; whatever is
                // wrong with it will be reported by the load itself, with better context. One that
                // is merely malformed needs no catch: the parse is error-tolerant.
                return [];
            }
        });

    /// <summary>
    /// Whether the generator project has an output DLL anywhere under its <c>bin/</c>. Any
    /// configuration or target framework counts: the design-time evaluation picks one, but "some
    /// build has happened" is the question, and a stale Release build is still a generator that
    /// runs — the rebuild watcher handles staleness, absence is what breaks everything.
    /// </summary>
    private static bool HasBuiltOutput(string generatorProjectPath)
    {
        string? projectDir = Path.GetDirectoryName(generatorProjectPath);
        if (projectDir is null)
            return true;

        string binDir = Path.Combine(projectDir, "bin");
        if (!Directory.Exists(binDir))
            return false;

        try
        {
            string assemblyName = ReadAssemblyName(generatorProjectPath);
            return Directory.EnumerateFiles(binDir, assemblyName + ".dll", SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            // An unreadable bin/ must not be mistaken for an unbuilt generator: the failure mode
            // of a false "unbuilt" is a build subprocess on every load.
            return true;
        }
    }

    /// <summary>
    /// The project's <c>&lt;AssemblyName&gt;</c> when it declares a literal one, else the project
    /// file name — MSBuild's own default.
    /// </summary>
    private static string ReadAssemblyName(string projectPath) =>
        PathHelper.FileDerived<string>.Get(projectPath, static path =>
        {
            string fallback = Path.GetFileNameWithoutExtension(path);
            try
            {
                string? declared = Parser.ParseText(File.ReadAllText(path))
                    .DescendantsByLocalName("AssemblyName")
                    .FirstOrDefault()?.Value.Trim();
                return string.IsNullOrEmpty(declared) || declared.Contains('$') ? fallback : declared;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return fallback;
            }
        });

    /// <summary>Project file path → (stamp it was read at, the TargetPath MSBuild computed, or
    /// null when the evaluation failed). Caching on the project file's own stamp means a warm
    /// session answers <see cref="HasOutputAtMsBuildTargetPathAsync"/> with a stat.</summary>
    private static readonly ConcurrentDictionary<string, (long Ticks, string? TargetPath)>
        s_targetPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the generator's real output — where MSBuild says it goes, not where the bin probe
    /// guessed — exists on disk. False when the evaluation fails or the file is absent, in which
    /// case the caller builds, exactly as before this check existed.
    /// </summary>
    private static async Task<bool> HasOutputAtMsBuildTargetPathAsync(
        string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            long ticks = new FileInfo(projectPath).LastWriteTimeUtc.Ticks;
            if (!s_targetPaths.TryGetValue(projectPath, out var cached) || cached.Ticks != ticks)
            {
                cached = (ticks, await QueryTargetPathAsync(projectPath, cancellationToken));
                s_targetPaths[projectPath] = cached;
            }

            return cached.TargetPath is { Length: > 0 } target && File.Exists(target);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asks MSBuild for the project's <c>TargetPath</c> — an evaluation, not a build. Returns
    /// null when it cannot be determined; ~a second on first ask, then cached by the caller.
    /// </summary>
    private static async Task<string?> QueryTargetPathAsync(
        string projectPath, CancellationToken cancellationToken)
    {
        bool legacy = PathHelper.RequiresMsBuild(projectPath);
        if (legacy && WorkspaceService.LegacyMsBuildDirectory is not { Length: > 0 })
            return null;

        var (fileName, arguments) = legacy
            ? (Path.Combine(WorkspaceService.LegacyMsBuildDirectory!, "MSBuild.exe"),
                $"\"{projectPath}\" -getProperty:TargetPath -nologo")
            : ("dotnet", $"msbuild \"{projectPath}\" -getProperty:TargetPath -nologo");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            }
        };

        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        try
        {
            BuildProcessHelper.StartWithClosedInput(process);

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

            // Bounded: this is an optimization on the load path, and a wedged MSBuild here must
            // degrade to "build it, as before", not hold the open hostage.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
                return null;

            string answer = (await stdout).Trim();
            return answer.Length > 0 ? Path.GetFullPath(answer) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The build itself. Uncancellable by design, exactly like a restore: it is shared by every
    /// caller waiting on the same generator, so the first one to give up must not take the others'
    /// build down with it. Failures are reported here and not thrown.
    /// </summary>
    private static async Task RunBuildAsync(string generatorProjectPath)
    {
        // Off the caller's stack: GetOrAdd runs its factory inline.
        await Task.Yield();

        bool legacy = PathHelper.RequiresMsBuild(generatorProjectPath);
        if (legacy && WorkspaceService.LegacyMsBuildDirectory is not { Length: > 0 })
        {
            Console.Error.WriteLine(
                $"[GeneratorBuild] '{Path.GetFileName(generatorProjectPath)}' needs a Visual Studio MSBuild " +
                "to build (non-SDK project) and none is installed; projects consuming this generator " +
                "will be missing their generated code.");
            return;
        }

        var watch = Stopwatch.StartNew();
        Console.Error.WriteLine(
            $"[GeneratorBuild] Building source generator '{Path.GetFileName(generatorProjectPath)}'...");

        // `dotnet build` restores by itself, and -t:Restore;Build gives MSBuild.exe the same
        // behaviour: a generator outside the loaded solution may not have been covered by the
        // solution restore that just ran. -nr:false for the same reason as RestoreService — a
        // lingering worker node holds handles the next git operation trips over.
        var (fileName, arguments) = legacy
            ? (Path.Combine(WorkspaceService.LegacyMsBuildDirectory!, "MSBuild.exe"),
                $"\"{generatorProjectPath}\" -t:Restore;Build -v:quiet -nologo -nr:false")
            : ("dotnet",
                $"build \"{generatorProjectPath}\" --verbosity quiet --nologo -nr:false -tl:false");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(generatorProjectPath)!,
            }
        };

        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        int exitCode;
        string output;
        try
        {
            BuildProcessHelper.StartWithClosedInput(process);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);

            exitCode = process.ExitCode;
            output = $"{(await stdout).Trim()}\n{(await stderr).Trim()}".Trim();
        }
        catch (Exception ex)
        {
            exitCode = -1;
            output = ex.Message;
        }

        if (exitCode == 0)
        {
            s_builtThisSession.TryAdd(generatorProjectPath, 0);
            Console.Error.WriteLine(
                $"[GeneratorBuild] '{Path.GetFileName(generatorProjectPath)}' built in {watch.ElapsedMilliseconds} ms.");
            return;
        }

        // The full output, not the exit code: a generator that fails to build is nearly always a
        // compile error in the generator itself, and that is only diagnosable from the errors.
        Console.Error.WriteLine(
            $"[GeneratorBuild] Build of '{Path.GetFileName(generatorProjectPath)}' failed (exit {exitCode}) " +
            $"after {watch.ElapsedMilliseconds} ms; consuming projects load without its generated code.\n{output}");
    }
}
