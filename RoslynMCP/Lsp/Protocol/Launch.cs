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
    [property: JsonPropertyName("configuration")] string? Configuration = null);

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
    [property: JsonPropertyName("error")] string? Error);

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
