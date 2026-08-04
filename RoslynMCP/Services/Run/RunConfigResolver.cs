using System.Net;
using System.Net.Sockets;
using System.Xml;

namespace RoslynMCP.Services.Run;

/// <summary>How a project should be launched.</summary>
public sealed record RunSpec
{
    public required string ProjectPath { get; init; }
    public required AppKind Kind { get; init; }
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required DebugRuntime DebugRuntime { get; init; }

    /// <summary>The URL the app will serve on, for web projects.</summary>
    public string? Url { get; init; }

    /// <summary>The port to poll for readiness, for web projects.</summary>
    public int? Port { get; init; }

    /// <summary>The launchSettings.json profile this spec came from, when there was one.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Every launchable profile in the project, so a caller can offer the others.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];

    /// <summary>Where to send a browser once the app is up: <see cref="Url"/> with the profile's
    /// <c>launchUrl</c> applied.</summary>
    public string? BrowseUrl { get; init; }

    /// <summary>The profile's <c>launchBrowser</c>. Null when no profile had a say, which leaves
    /// the decision to the client rather than reading as an explicit "no".</summary>
    public bool? LaunchBrowser { get; init; }

    /// <summary>The profile's <c>hotReloadEnabled</c>, when it stated one.</summary>
    public bool? HotReloadEnabled { get; init; }

    /// <summary>Why the project cannot be launched, when it cannot.</summary>
    public string? Error { get; init; }

    public bool CanRun => Error is null;

    public static RunSpec Unsupported(string projectPath, AppKind kind, string error) => new()
    {
        ProjectPath = projectPath,
        Kind = kind,
        Executable = "",
        Arguments = [],
        WorkingDirectory = "",
        Environment = new Dictionary<string, string>(),
        DebugRuntime = DebugRuntime.CoreClr,
        Error = error,
    };
}

/// <summary>
/// Turns a classified project into a concrete launch: which executable, which arguments, which
/// environment, and which URL to expect.
/// </summary>
/// <remarks>
/// Classification itself belongs to <see cref="ProjectClassifier"/>; this only maps a shape to a
/// launch. The IIS Express path follows what Visual Studio does for a legacy web project.
/// </remarks>
public static class RunConfigResolver
{
    public static RunSpec Resolve(
        string projectPath,
        string configuration = "Debug",
        string? launchProfile = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        projectPath = PathHelper.NormalizePath(projectPath);
        var classification = ProjectClassifier.Classify(projectPath);
        var projectDir = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory;

        if (!classification.IsRunnable)
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                classification.IsTestProject
                    ? "This is a test project — use RunTests instead of RunProject."
                    : "This project produces a library, so there is nothing to run.");
        }

        return classification.Kind == AppKind.AspNetClassic
            ? ResolveIisExpress(projectPath, projectDir, classification, extraEnvironment)
            : ResolveExecutable(projectPath, projectDir, classification, configuration, launchProfile, extraEnvironment);
    }

    /// <summary>
    /// A legacy System.Web site runs under IIS Express against the project directory, with the port
    /// and virtual path Visual Studio recorded in the project's WebProjectProperties.
    /// </summary>
    private static RunSpec ResolveIisExpress(
        string projectPath,
        string projectDir,
        ProjectClassification classification,
        IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        var iisExpress = NetFxToolchain.Info.PreferredIisExpress;
        if (iisExpress is null)
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                "IIS Express was not found. Install it (it ships with Visual Studio) to run legacy ASP.NET sites.");
        }

        TryReadWebProjectProperties(projectPath, out var web);
        var port = PickPort(web.Port);

        return new RunSpec
        {
            ProjectPath = projectPath,
            Kind = classification.Kind,
            Executable = iisExpress,
            Arguments =
            [
                $"/path:{projectDir}",
                $"/port:{port}",
                "/clr:v4.0",
                "/systray:false",
                "/trace:error",
            ],
            WorkingDirectory = projectDir,
            Environment = Merge(null, extraEnvironment),
            DebugRuntime = DebugRuntime.NetFramework,
            // IIS Express's /port binding is plain HTTP. An https IISUrl still runs, over http:
            // an SSL binding would need applicationHost.config, which this deliberately never edits.
            Url = $"http://localhost:{port}{web.VirtualPath}",
            Port = port,
            BrowseUrl = $"http://localhost:{port}{web.VirtualPath}",
        };
    }

    private static RunSpec ResolveExecutable(
        string projectPath,
        string projectDir,
        ProjectClassification classification,
        string configuration,
        string? launchProfile,
        IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        var settings = LaunchSettings.Load(projectDir);
        var profile = settings?.Select(launchProfile);
        string[] profileNames = settings is null
            ? []
            : [.. settings.Profiles.Where(p => p.IsLaunchable).Select(p => p.Name)];

        // A name that matches nothing would otherwise fall back to the default profile and run
        // with the wrong environment, which looks like the profile was honoured.
        if (!string.IsNullOrWhiteSpace(launchProfile) && profile is null)
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                $"'{launchProfile}' is not a profile in this project's launchSettings.json." +
                (profileNames.Length == 0
                    ? " It has no launchable profiles."
                    : $" Available: {string.Join(", ", profileNames)}."));
        }

        if (profile is { IsLaunchable: false })
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                $"Profile '{profile.Name}' has commandName '{profile.CommandName}', which this " +
                "server does not launch.");
        }

        var environment = Merge(profile?.EnvironmentVariables, extraEnvironment);
        var profileArguments = SplitArguments(profile?.CommandLineArgs);

        // An "Executable" profile runs something else entirely — the project's own output is not
        // even involved, so neither is its build.
        if (profile is { CommandName: "Executable" })
        {
            return ResolveProfileExecutable(
                projectPath, projectDir, classification, profile, profileNames,
                profileArguments, environment);
        }

        // Running the built binary rather than `dotnet run` gives a stable PID, which the debugger
        // needs and `dotnet run` does not provide (it launches the app as a child of itself).
        var targetPath = MsBuildLocator.GetTargetPath(projectPath, configuration);
        if (targetPath is null || !File.Exists(targetPath))
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                $"Could not locate the built output for '{Path.GetFileName(projectPath)}' " +
                $"({configuration}). Build the project first.");
        }

        var url = profile?.ApplicationUrl;

        // An IISExpress profile carries no URL of its own; the binding lives in iisSettings.
        if (profile is { CommandName: "IISExpress" })
            url ??= settings?.IisExpressApplicationUrl;

        // --urls on the command line beats both, and is what the app will actually bind to.
        url = UrlsFromArguments(profileArguments) ?? url;

        if (classification.Kind == AppKind.AspNetCore)
        {
            // Derived from the project path rather than asked of the OS, so the app keeps the same
            // address across restarts and a bookmark or a running browser tab stays valid.
            url ??= $"http://localhost:{PickPort(StablePort(projectPath))}";

            // ASPNETCORE_URLS is what actually binds the server; the launch profile only suggests it.
            if (!environment.ContainsKey("ASPNETCORE_URLS"))
                environment["ASPNETCORE_URLS"] = url;
        }

        var (executable, hostArguments) = ResolveHost(targetPath, classification);

        return new RunSpec
        {
            ProjectPath = projectPath,
            Kind = classification.Kind,
            Executable = executable,
            Arguments = [.. hostArguments, .. profileArguments],
            WorkingDirectory = ResolveWorkingDirectory(profile?.WorkingDirectory, projectDir)
                ?? Path.GetDirectoryName(targetPath) ?? projectDir,
            Environment = environment,
            DebugRuntime = classification.DebugRuntime,
            Url = url,
            Port = TryParsePort(url),
            ProfileName = profile?.Name,
            Profiles = profileNames,
            BrowseUrl = CombineLaunchUrl(url, profile?.LaunchUrl),
            LaunchBrowser = profile?.LaunchBrowser,
            HotReloadEnabled = profile?.HotReloadEnabled,
        };
    }

    /// <summary>
    /// A <c>commandName: "Executable"</c> profile: the project is only the place the settings live,
    /// and the thing that runs is whatever <c>executablePath</c> names.
    /// </summary>
    private static RunSpec ResolveProfileExecutable(
        string projectPath,
        string projectDir,
        ProjectClassification classification,
        LaunchProfileInfo profile,
        IReadOnlyList<string> profileNames,
        IReadOnlyList<string> arguments,
        Dictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                $"Profile '{profile.Name}' is an Executable profile but sets no executablePath.");
        }

        var executable = Path.IsPathRooted(profile.ExecutablePath)
            ? profile.ExecutablePath
            : Path.GetFullPath(profile.ExecutablePath, projectDir);

        // A bare command name is meant to be found on PATH, so keep it as written when the
        // project-relative interpretation does not exist.
        if (!File.Exists(executable))
            executable = profile.ExecutablePath;

        var url = UrlsFromArguments(arguments) ?? profile.ApplicationUrl;

        return new RunSpec
        {
            ProjectPath = projectPath,
            Kind = classification.Kind,
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = ResolveWorkingDirectory(profile.WorkingDirectory, projectDir) ?? projectDir,
            Environment = environment,
            DebugRuntime = classification.DebugRuntime,
            Url = url,
            Port = TryParsePort(url),
            ProfileName = profile.Name,
            Profiles = profileNames,
            BrowseUrl = CombineLaunchUrl(url, profile.LaunchUrl),
            LaunchBrowser = profile.LaunchBrowser,
            HotReloadEnabled = profile.HotReloadEnabled,
        };
    }

    private static string? ResolveWorkingDirectory(string? configured, string projectDir)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(expanded, projectDir);
    }

    /// <summary>
    /// The <c>--urls</c> value from a profile's command line, which overrides both the profile's
    /// applicationUrl and ASPNETCORE_URLS once the app is running.
    /// </summary>
    internal static string? UrlsFromArguments(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
                return argument["--urls=".Length..];

            if (argument.Equals("--urls", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
                return arguments[i + 1];
        }

        return null;
    }

    /// <summary>
    /// The address to open a browser at: a <c>launchUrl</c> is either absolute or relative to the
    /// first URL the app binds to.
    /// </summary>
    internal static string? CombineLaunchUrl(string? applicationUrl, string? launchUrl)
    {
        var baseUrl = applicationUrl?.Split(';').FirstOrDefault()?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(launchUrl))
            return baseUrl;

        if (Uri.TryCreate(launchUrl, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return baseUrl is null ? null : $"{baseUrl}/{launchUrl.TrimStart('/')}";
    }

    /// <summary>
    /// Splits a profile's <c>commandLineArgs</c> the way a shell would: double quotes group, and a
    /// backslash only escapes when it precedes a quote.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];

        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        bool started = false;
        int backslashes = 0;

        void FlushBackslashes(bool beforeQuote)
        {
            current.Append('\\', beforeQuote ? backslashes / 2 : backslashes);
            backslashes = 0;
        }

        foreach (var c in commandLine)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                bool escaped = backslashes % 2 == 1;
                FlushBackslashes(beforeQuote: true);
                if (escaped)
                    current.Append('"');
                else
                    quoted = !quoted;
                started = true;
                continue;
            }

            FlushBackslashes(beforeQuote: false);

            if (!quoted && char.IsWhiteSpace(c))
            {
                // An explicitly quoted empty argument is still an argument.
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                continue;
            }

            current.Append(c);
            started = true;
        }

        FlushBackslashes(beforeQuote: false);
        if (started || current.Length > 0)
            arguments.Add(current.ToString());

        return arguments;
    }

    /// <summary>
    /// A .NET Framework build produces a directly runnable .exe. A modern .NET build produces a
    /// .dll plus an apphost .exe; prefer the apphost, and fall back to the shared `dotnet` host.
    /// </summary>
    private static (string Executable, IReadOnlyList<string> Arguments) ResolveHost(
        string targetPath, ProjectClassification classification)
    {
        if (classification.Runtime == RuntimeFlavor.NetFramework)
            return (targetPath, []);

        var appHost = Path.ChangeExtension(targetPath, ".exe");
        if (OperatingSystem.IsWindows() && File.Exists(appHost))
            return (appHost, []);

        var extensionless = Path.ChangeExtension(targetPath, null);
        if (!OperatingSystem.IsWindows() && File.Exists(extensionless))
            return (extensionless, []);

        return ("dotnet", [targetPath]);
    }

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in first ?? new Dictionary<string, string>())
            merged[pair.Key] = pair.Value;

        // Caller-supplied values win over the launch profile's.
        foreach (var pair in second ?? new Dictionary<string, string>())
            merged[pair.Key] = pair.Value;

        return merged;
    }

    private static int? TryParsePort(string? url) =>
        Uri.TryCreate(url?.Split(';').FirstOrDefault(), UriKind.Absolute, out var parsed) ? parsed.Port : null;

    // -------------------------------------------------------------------------
    // Legacy web project properties
    // -------------------------------------------------------------------------

    /// <summary>
    /// The WebProjectProperties block a legacy web csproj carries under
    /// <c>ProjectExtensions/VisualStudio/FlavorProperties</c>.
    /// </summary>
    public readonly record struct WebProjectProperties(int Port, string VirtualPath, bool UseSsl);

    /// <summary>
    /// Reads the port and virtual path from a legacy web project. The port comes from
    /// <c>DevelopmentServerPort</c> or the port of <c>IISUrl</c>, defaulting to 8080.
    /// </summary>
    public static bool TryReadWebProjectProperties(string csprojPath, out WebProjectProperties props)
    {
        var port = 8080;
        var vpath = "";
        var useSsl = false;
        props = new WebProjectProperties(port, vpath, useSsl);

        try
        {
            using var reader = XmlReader.Create(
                csprojPath, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });

            var sawRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (!sawRoot)
                {
                    if (reader.LocalName != "Project")
                        continue;

                    // SDK-style projects use launch profiles instead; this block is legacy-only.
                    if (reader.GetAttribute("Sdk") is not null)
                        return false;

                    sawRoot = true;
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "DevelopmentServerPort":
                        if (int.TryParse(reader.ReadElementContentAsString(), out var devPort) && devPort > 0)
                            port = devPort;
                        break;
                    case "DevelopmentServerVPath":
                        vpath = NormalizeVPath(reader.ReadElementContentAsString());
                        break;
                    case "IISUrl":
                        if (Uri.TryCreate(reader.ReadElementContentAsString(), UriKind.Absolute, out var url))
                        {
                            port = url.Port;
                            useSsl = url.Scheme == Uri.UriSchemeHttps;
                            if (vpath.Length == 0)
                                vpath = NormalizeVPath(url.AbsolutePath);
                        }
                        break;
                }
            }

            if (!sawRoot)
                return false;

            props = new WebProjectProperties(port, vpath, useSsl);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a virtual path so it can be appended to <c>http://localhost:{port}</c>: the site
    /// root becomes empty, anything else becomes <c>/segment</c> with no trailing slash.
    /// </summary>
    internal static string NormalizeVPath(string raw)
    {
        var trimmed = raw.Trim().Trim('/');
        return trimmed.Length == 0 ? "" : "/" + trimmed;
    }

    /// <summary>
    /// The preferred port when it is free, otherwise an OS-assigned one. Best effort by nature: the
    /// gap between probing and binding is unavoidable, and a false "busy" only moves the site to a
    /// different port.
    /// </summary>
    /// <summary>
    /// A port derived from the project's path, in the range ASP.NET Core templates use. Stable by
    /// construction: the same project gets the same address on every machine and every run, which
    /// an OS-assigned port cannot promise even once.
    /// </summary>
    internal static int StablePort(string projectPath)
    {
        ulong hash = 14695981039346656037; // FNV-1a
        foreach (var c in PathHelper.NormalizePath(projectPath).ToLowerInvariant())
        {
            hash ^= c;
            hash *= 1099511628211;
        }

        return 5000 + (int)(hash % 1000);
    }

    internal static int PickPort(int preferred)
    {
        if (preferred is > 0 and < 65536 && IsPortFree(preferred))
            return preferred;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

}
