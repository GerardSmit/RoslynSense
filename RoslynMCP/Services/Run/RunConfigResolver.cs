using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
        // Running the built binary rather than `dotnet run` gives a stable PID, which the debugger
        // needs and `dotnet run` does not provide (it launches the app as a child of itself).
        var targetPath = MsBuildLocator.GetTargetPath(projectPath, configuration);
        if (targetPath is null || !File.Exists(targetPath))
        {
            return RunSpec.Unsupported(projectPath, classification.Kind,
                $"Could not locate the built output for '{Path.GetFileName(projectPath)}' " +
                $"({configuration}). Build the project first.");
        }

        var profile = LaunchProfile.Load(projectDir, launchProfile);
        var environment = Merge(profile?.EnvironmentVariables, extraEnvironment);

        var url = profile?.ApplicationUrl;
        if (classification.Kind == AppKind.AspNetCore)
        {
            url ??= $"http://localhost:{PickPort(0)}";

            // ASPNETCORE_URLS is what actually binds the server; the launch profile only suggests it.
            if (!environment.ContainsKey("ASPNETCORE_URLS"))
                environment["ASPNETCORE_URLS"] = url;
        }

        var (executable, arguments) = ResolveHost(targetPath, classification);

        return new RunSpec
        {
            ProjectPath = projectPath,
            Kind = classification.Kind,
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(targetPath) ?? projectDir,
            Environment = environment,
            DebugRuntime = classification.DebugRuntime,
            Url = url,
            Port = TryParsePort(url),
        };
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

    // -------------------------------------------------------------------------
    // launchSettings.json
    // -------------------------------------------------------------------------

    private sealed record LaunchProfile(
        string? ApplicationUrl, Dictionary<string, string> EnvironmentVariables)
    {
        /// <summary>
        /// Loads a profile from <c>Properties/launchSettings.json</c>. Without an explicit name the
        /// first "Project"-command profile wins, which is what the dotnet CLI does.
        /// </summary>
        public static LaunchProfile? Load(string projectDir, string? profileName)
        {
            var path = Path.Combine(projectDir, "Properties", "launchSettings.json");
            if (!File.Exists(path))
                return null;

            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

                if (!document.RootElement.TryGetProperty("profiles", out var profiles))
                    return null;

                foreach (var profile in profiles.EnumerateObject())
                {
                    if (profileName is not null &&
                        !profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (profileName is null &&
                        profile.Value.TryGetProperty("commandName", out var command) &&
                        command.GetString() is not "Project")
                        continue;

                    return Parse(profile.Value);
                }
            }
            catch (Exception)
            {
                // A malformed launchSettings.json falls back to defaults rather than blocking the run.
            }

            return null;
        }

        private static LaunchProfile Parse(JsonElement profile)
        {
            var url = profile.TryGetProperty("applicationUrl", out var applicationUrl)
                ? applicationUrl.GetString()
                : null;

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (profile.TryGetProperty("environmentVariables", out var variables) &&
                variables.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in variables.EnumerateObject())
                {
                    if (variable.Value.ValueKind == JsonValueKind.String)
                        environment[variable.Name] = variable.Value.GetString() ?? "";
                }
            }

            return new LaunchProfile(url, environment);
        }
    }
}
