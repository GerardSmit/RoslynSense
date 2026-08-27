using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

/// <summary>Where the .NET debug adapter lives. <see cref="Provisioned"/> is true when the
/// server had to download it, which the client can mention rather than leaving a long pause
/// unexplained.</summary>
public sealed record DebuggerPathResult(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("provisioned")] bool Provisioned,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// What this machine can build and debug .NET Framework projects with. Empty paths mean the tool
/// was not found, which the client turns into an actionable message rather than a failed build.
/// </summary>
public sealed record ToolchainInfo(
    [property: JsonPropertyName("msbuildPath")] string MsBuildPath,
    [property: JsonPropertyName("hasDesktopClr")] bool HasDesktopClr,
    [property: JsonPropertyName("iisExpressPath")] string? IisExpressPath);

public sealed record LaunchTargetsParams(
    [property: JsonPropertyName("configuration")] string? Configuration = null,
    [property: JsonPropertyName("launchProfile")] string? LaunchProfile = null);

/// <summary>Which project owns a file, so F5 can run what the user is looking at.</summary>
public sealed record TargetForFileParams(
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("configuration")] string? Configuration = null,
    [property: JsonPropertyName("launchProfile")] string? LaunchProfile = null);

/// <summary>One debuggable (or explicitly non-debuggable) project.</summary>
public sealed record LaunchTarget(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("targetFramework")] string? TargetFramework,
    [property: JsonPropertyName("isNetFramework")] bool IsNetFramework,
    [property: JsonPropertyName("isTestProject")] bool IsTestProject,
    [property: JsonPropertyName("runnable")] bool Runnable,
    [property: JsonPropertyName("program")] string? Program,
    [property: JsonPropertyName("args")] string[] Args,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("env")] Dictionary<string, string> Env,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("error")] string? Error,
    // The launchSettings.json profiles this project offers, so the client can turn them into run
    // configurations instead of making the user name one, and which one this target used.
    [property: JsonPropertyName("launchProfiles")] LaunchProfileDescriptor[] LaunchProfiles,
    [property: JsonPropertyName("launchProfile")] string? LaunchProfile = null,
    // Where to open a browser: the app URL with the profile's launchUrl applied.
    [property: JsonPropertyName("browseUrl")] string? BrowseUrl = null,
    [property: JsonPropertyName("launchBrowser")] bool? LaunchBrowser = null)
{
    /// <summary>
    /// Whether this target is debugged by the adapter the server ships rather than by netcoredbg.
    /// </summary>
    /// <remarks>
    /// Answered here rather than derived by the client from <see cref="IsNetFramework"/>: a
    /// .NET Framework target always is, and a .NET one is whenever the engine setting says so, and
    /// that setting lives on the server. A client working it out for itself would be reading a
    /// different copy of it — or, for a value set in <c>roslynsense.json</c>, none at all.
    /// </remarks>
    [JsonPropertyName("serverDebugAdapter")]
    public bool ServerDebugAdapter { get; init; }
}

/// <summary>A launchSettings.json profile, as much of it as a client needs to show and pick it.</summary>
public sealed record LaunchProfileDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("commandName")] string CommandName,
    [property: JsonPropertyName("applicationUrl")] string? ApplicationUrl,
    [property: JsonPropertyName("commandLineArgs")] string? CommandLineArgs,
    [property: JsonPropertyName("launchBrowser")] bool LaunchBrowser,
    [property: JsonPropertyName("launchUrl")] string? LaunchUrl,
    [property: JsonPropertyName("environmentVariables")] Dictionary<string, string> EnvironmentVariables);

/// <summary>A .NET process the debugger can attach to. <see cref="ProjectName"/> is set for
/// processes this server launched, which is what makes the picker readable.</summary>
public sealed record AttachTarget(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("projectName")] string? ProjectName,
    [property: JsonPropertyName("url")] string? Url);

public sealed record BuildResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("errors")] BuildMessage[] Errors,
    [property: JsonPropertyName("warnings")] BuildMessage[] Warnings);

public sealed record BuildMessage(
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string Message);
