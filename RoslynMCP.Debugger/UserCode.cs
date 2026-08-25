namespace RoslynMCP.Debugger;

/// <summary>What a module is, as far as "my code" is concerned.</summary>
public enum UserCodeVerdict
{
    /// <summary>No evidence either way. Treated as the user's, because hiding code that turns out
    /// to be theirs is a worse failure than showing a frame they did not want.</summary>
    Unknown,

    /// <summary>Built from a project in the open solution.</summary>
    User,

    /// <summary>The framework, the GAC, or a restored package — somebody else's.</summary>
    External,
}

/// <summary>
/// Which loaded modules hold code the user wrote.
/// </summary>
/// <remarks>
/// <para>
/// The answer everything about stepping depends on, and the reason it is worth building from the
/// solution rather than guessing from paths: a path test can only recognise the places it was
/// told about, so every module anywhere else — a restored package, a self-contained runtime beside
/// the application, a plugin loaded from a content directory — reads as the user's own, and a step
/// into any of them stops in code with no source.
/// </para>
/// <para>
/// Naming the user's assemblies instead gives the one thing a path test cannot: a positive answer.
/// A module the solution builds is the user's wherever it was loaded from, which is what lets the
/// runtime's own Just My Code be turned on at all — it needs somewhere marked as the user's or a
/// filtered step never finds anywhere to stop.
/// </para>
/// <para>
/// What it deliberately does not give is a negative answer. Absence from the solution is not
/// evidence of anything, so it stays <see cref="UserCodeVerdict.Unknown"/> — which every caller
/// reads as "the user's" — and only the places nobody's own code is built into say
/// <see cref="UserCodeVerdict.External"/>.
/// </para>
/// </remarks>
public sealed class UserCodeMap
{
    /// <summary>The map for a session with no solution to consult: everything is Unknown.</summary>
    public static readonly UserCodeMap None = new([]);

    private readonly HashSet<string> _assemblies;

    private UserCodeMap(IEnumerable<string> assemblies) =>
        _assemblies = new HashSet<string>(assemblies, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a map from the output assemblies of the projects in the solution.
    /// </summary>
    /// <remarks>
    /// Reduced to simple assembly names, not kept as paths. The module the runtime loads is very
    /// often not the file the compiler wrote — a web application runs from a shadow copy, a test
    /// host runs from a per-run directory, a deployment copies the whole bin folder somewhere
    /// else — and all of those keep the name while changing every directory above it.
    /// </remarks>
    public static UserCodeMap From(IEnumerable<string> outputAssemblies) =>
        new(outputAssemblies
            .Where(p => p is { Length: > 0 })
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 })
            .Select(n => n!));

    /// <summary>Whether there is a solution behind this at all. False means every answer below is
    /// <see cref="UserCodeVerdict.Unknown"/> or <see cref="UserCodeVerdict.External"/>, never
    /// <see cref="UserCodeVerdict.User"/>, which is what decides whether the runtime's own Just My
    /// Code can be trusted to find anywhere to stop.</summary>
    public bool KnowsTheSolution => _assemblies.Count > 0;

    /// <summary>How many assemblies the solution builds.</summary>
    public int Count => _assemblies.Count;

    public UserCodeVerdict Classify(string modulePath)
    {
        if (modulePath.Length == 0)
            return UserCodeVerdict.Unknown;

        string name;
        try { name = Path.GetFileNameWithoutExtension(modulePath); }
        catch { return UserCodeVerdict.Unknown; }

        if (name.Length > 0 && _assemblies.Contains(name))
            return UserCodeVerdict.User;

        var lower = modulePath.ToLowerInvariant();

        // The page compiler's output, and the shadow copies it takes of the site's own bin. Named
        // unpredictably per compilation, so neither the solution nor a directory can identify them;
        // they hold the user's markup and inline code and have to stay steppable.
        if (lower.Contains("temporary asp.net files"))
            return UserCodeVerdict.Unknown;

        foreach (var directory in NotTheUsers)
        {
            if (lower.Contains(directory))
                return UserCodeVerdict.External;
        }

        // Nothing recognised the module, and an open solution does not change that. It is tempting
        // to read absence as an answer — the solution builds everything the user wrote, so surely
        // a module it does not build is somebody else's — but the solution is not a reliable
        // census of the user's projects. A project whose design-time build failed has no output
        // path to contribute, a workspace opened for one project holds only that project and its
        // references while the process runs its siblings, and a build step that renames or merges
        // an assembly leaves a module no project claims. Ruling those External would tell the
        // runtime the user's own code is not theirs, step back out of their own methods, refuse
        // them as hot reload targets, and — worst of it — silence the diagnostic written to
        // explain why.
        return UserCodeVerdict.Unknown;
    }

    /// <summary>
    /// Whether this module may hold the user's code — <c>false</c> only for a module positively
    /// ruled out.
    /// </summary>
    /// <remarks>
    /// The fail-open reading of <see cref="Classify"/>, and the one every caller that has to pick a
    /// side should use. Being wrong in this direction costs a frame in a call stack; being wrong in
    /// the other means a breakpoint that never binds and a step that never stops, with nothing said
    /// about why.
    /// </remarks>
    public bool CouldBeUserCode(string modulePath) =>
        Classify(modulePath) != UserCodeVerdict.External;

    /// <summary>
    /// Directories nobody's own code is built into.
    /// </summary>
    /// <remarks>
    /// Still needed alongside the solution: they are what makes a session with no workspace — an
    /// attach to a process nobody opened a project for — say anything useful at all, and they are
    /// what keeps a self-contained application's bundled runtime out of the user's code when the
    /// solution does happen to be open.
    /// </remarks>
    private static readonly string[] NotTheUsers =
    [
        @"\microsoft.net\framework",
        @"\windows\assembly\",
        @"\gac_",
        @"\dotnet\shared\",
        @"\.nuget\packages\",
        @"\packages\microsoft.netcore.app.",
        @"\reference assemblies\microsoft\",
    ];
}
