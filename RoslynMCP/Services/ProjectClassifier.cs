using System.Collections.Concurrent;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMCP.Services;

/// <summary>Whether a project uses the SDK-style or the legacy (pre-2017) csproj format.</summary>
public enum ProjectStyle
{
    Sdk,
    Legacy,
}

/// <summary>Which .NET family a project targets.</summary>
public enum RuntimeFlavor
{
    Unknown,
    NetFramework,
    NetCore,
    NetStandard,
}

/// <summary>What kind of application a project produces, as far as running it is concerned.</summary>
public enum AppKind
{
    Unknown,
    ClassLibrary,
    ConsoleApp,
    WindowsApp,

    /// <summary>An ASP.NET Core / Blazor Server web app, launched through the dotnet CLI.</summary>
    AspNetCore,

    /// <summary>A legacy System.Web site (WebForms/MVC5), launched through IIS Express.</summary>
    AspNetClassic,
}

/// <summary>Which build driver a project needs.</summary>
public enum BuildTool
{
    DotnetCli,

    /// <summary>Legacy projects need full Visual Studio MSBuild, not the dotnet CLI.</summary>
    VisualStudioMsBuild,
}

/// <summary>Which debugger bootstrap a project's output needs.</summary>
public enum DebugRuntime
{
    NetFramework,
    CoreClr,
}

/// <summary>A project's configured platform target.</summary>
public enum TargetArch
{
    AnyCpu,
    X86,
    X64,
    Arm64,
}

/// <summary>
/// Everything the build, test, run and debug paths need to know about a project's shape.
/// </summary>
public sealed record ProjectClassification(
    string ProjectPath,
    ProjectStyle Style,
    string? Sdk,
    RuntimeFlavor Runtime,
    string? TargetFramework,
    AppKind Kind,
    bool IsTestProject,
    BuildTool BuildTool,
    DebugRuntime DebugRuntime,
    TargetArch Arch)
{
    /// <summary>Whether <see cref="Kind"/> denotes something that can be started.</summary>
    public bool IsRunnable => Kind is AppKind.ConsoleApp or AppKind.WindowsApp
        or AppKind.AspNetCore or AppKind.AspNetClassic;
}

/// <summary>
/// The single source of truth for "what kind of project is this?".
/// </summary>
/// <remarks>
/// Two entry points, because callers genuinely differ. <see cref="Classify(string)"/> reads only
/// the project file and never loads a workspace — <c>BuildProject</c> runs before anything is
/// loaded and <c>ListProjects</c> deliberately avoids Roslyn entirely. <see cref="Classify(Project)"/>
/// layers Roslyn facts on top when a workspace is already open, which picks up values injected by
/// <c>Directory.Build.props</c> that a plain file read cannot see.
/// </remarks>
public static class ProjectClassifier
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ProjectClassification Value)> s_cache = new();

    /// <summary>
    /// Classifies a project from its file alone. Cheap enough to call per tool invocation; results
    /// are cached until the project file's timestamp changes.
    /// </summary>
    public static ProjectClassification Classify(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(projectPath);
        }
        catch
        {
            stamp = DateTime.MinValue;
        }

        if (s_cache.TryGetValue(projectPath, out var cached) && cached.Stamp == stamp)
            return cached.Value;

        var result = ClassifyCore(projectPath);
        s_cache[projectPath] = (stamp, result);
        return result;
    }

    /// <summary>
    /// Classifies an already-loaded Roslyn project, refining the file-only result with the
    /// compilation's actual output kind and preprocessor symbols.
    /// </summary>
    public static ProjectClassification Classify(Project project)
    {
        // An in-memory project (no file on disk) still carries usable compilation facts, so fall
        // back to an empty baseline rather than giving up.
        var basePath = project.FilePath;
        var baseline = string.IsNullOrEmpty(basePath) ? Unknown(project.Name) : Classify(basePath);

        // Preprocessor symbols beat the file scan: they reflect the evaluated TFM, including one
        // set by Directory.Build.props rather than the csproj itself.
        var (runtime, tfm) = InferFrameworkFromSymbols(project);
        if (runtime != RuntimeFlavor.Unknown)
            baseline = baseline with { Runtime = runtime, TargetFramework = tfm ?? baseline.TargetFramework };

        // OutputKind is likewise authoritative over an <OutputType> the file scan may not see.
        if (project.CompilationOptions is { } options)
        {
            var kind = options.OutputKind switch
            {
                OutputKind.ConsoleApplication => AppKind.ConsoleApp,
                OutputKind.WindowsApplication => AppKind.WindowsApp,
                OutputKind.DynamicallyLinkedLibrary => AppKind.ClassLibrary,
                _ => AppKind.Unknown,
            };

            // A web app stays a web app: both flavors build as libraries as far as Roslyn is
            // concerned (ASP.NET Core apps are libraries plus a generated entry point), so the
            // file-scan verdict wins whenever it identified one.
            if (kind != AppKind.Unknown && baseline.Kind is not (AppKind.AspNetCore or AppKind.AspNetClassic))
                baseline = baseline with { Kind = kind };
        }

        return baseline with
        {
            BuildTool = SelectBuildTool(baseline.Style),
            DebugRuntime = SelectDebugRuntime(baseline.Runtime),
        };
    }

    private static ProjectClassification ClassifyCore(string projectPath)
    {
        var props = ReadProjectFile(projectPath);

        var style = props.Sdk is null ? ProjectStyle.Legacy : ProjectStyle.Sdk;
        var tfm = NormalizeTargetFramework(props.TargetFramework, props.TargetFrameworkVersion);
        var runtime = InferFlavor(tfm, style);
        var kind = InferAppKind(projectPath, props, style);

        return new ProjectClassification(
            ProjectPath: projectPath,
            Style: style,
            Sdk: props.Sdk,
            Runtime: runtime,
            TargetFramework: tfm,
            Kind: kind,
            IsTestProject: props.IsTestProject,
            BuildTool: SelectBuildTool(style),
            DebugRuntime: SelectDebugRuntime(runtime),
            Arch: ParseArch(props.PlatformTarget));
    }

    private static ProjectClassification Unknown(string name) => new(
        ProjectPath: name,
        Style: ProjectStyle.Sdk,
        Sdk: null,
        Runtime: RuntimeFlavor.Unknown,
        TargetFramework: null,
        Kind: AppKind.Unknown,
        IsTestProject: false,
        BuildTool: BuildTool.DotnetCli,
        DebugRuntime: DebugRuntime.CoreClr,
        Arch: TargetArch.AnyCpu);

    /// <summary>Legacy projects need full VS MSBuild; the dotnet CLI cannot build them.</summary>
    private static BuildTool SelectBuildTool(ProjectStyle style) =>
        style == ProjectStyle.Legacy ? BuildTool.VisualStudioMsBuild : BuildTool.DotnetCli;

    private static DebugRuntime SelectDebugRuntime(RuntimeFlavor runtime) =>
        runtime == RuntimeFlavor.NetFramework ? DebugRuntime.NetFramework : DebugRuntime.CoreClr;

    private static TargetArch ParseArch(string? platformTarget) => platformTarget?.Trim().ToLowerInvariant() switch
    {
        "x86" => TargetArch.X86,
        "x64" => TargetArch.X64,
        "arm64" => TargetArch.Arm64,
        _ => TargetArch.AnyCpu,
    };

    private readonly record struct RawProjectProperties(
        string? Sdk,
        string? TargetFramework,
        string? TargetFrameworkVersion,
        string? OutputType,
        string? PlatformTarget,
        bool IsTestProject,
        bool HasWebProjectProperties);

    /// <summary>
    /// One forward-only XML pass over the project file. Element names are compared by
    /// <see cref="XmlReader.LocalName"/> so the same code handles SDK-style projects (no namespace)
    /// and legacy ones (the 2003 MSBuild namespace).
    /// </summary>
    private static RawProjectProperties ReadProjectFile(string projectPath)
    {
        string? sdk = null, tfm = null, tfv = null, outputType = null, platform = null;
        bool isTest = false, hasWebProps = false;

        if (!File.Exists(projectPath))
            return new RawProjectProperties(null, null, null, null, null, false, false);

        try
        {
            using var reader = XmlReader.Create(
                projectPath, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });

            var sawRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (!sawRoot)
                {
                    if (reader.LocalName == "Project")
                    {
                        sdk = reader.GetAttribute("Sdk");
                        sawRoot = true;
                    }
                    continue;
                }

                switch (reader.LocalName)
                {
                    // <Sdk Name="..."/> import style, an alternative to the root attribute.
                    case "Sdk":
                        sdk ??= reader.GetAttribute("Name");
                        break;
                    case "TargetFramework":
                        tfm ??= reader.ReadElementContentAsString();
                        break;
                    case "TargetFrameworks":
                        // Multi-targeting: the first entry is what the tooling defaults to.
                        tfm ??= reader.ReadElementContentAsString()
                            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .FirstOrDefault();
                        break;
                    case "TargetFrameworkVersion":
                        tfv ??= reader.ReadElementContentAsString();
                        break;
                    case "OutputType":
                        outputType ??= reader.ReadElementContentAsString();
                        break;
                    case "PlatformTarget":
                        platform ??= reader.ReadElementContentAsString();
                        break;
                    case "WebProjectProperties":
                        hasWebProps = true;
                        break;
                    case "PackageReference":
                    case "Reference":
                        if (!isTest && reader.GetAttribute("Include") is { } include)
                            isTest = IsTestPackage(include);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // A malformed or unreadable project file degrades to "unknown", never throws at a caller.
        }

        return new RawProjectProperties(sdk, tfm, tfv, outputType, platform, isTest, hasWebProps);
    }

    private static bool IsTestPackage(string name) =>
        name.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("NUnit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MSTest", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reduces both TFM spellings to one: SDK-style <c>&lt;TargetFramework&gt;net472&lt;/&gt;</c> and
    /// legacy <c>&lt;TargetFrameworkVersion&gt;v4.7.2&lt;/&gt;</c> both become <c>net472</c>.
    /// </summary>
    private static string? NormalizeTargetFramework(string? targetFramework, string? targetFrameworkVersion)
    {
        if (!string.IsNullOrWhiteSpace(targetFramework))
            return targetFramework.Trim();

        if (string.IsNullOrWhiteSpace(targetFrameworkVersion))
            return null;

        var version = targetFrameworkVersion.Trim().TrimStart('v', 'V').Replace(".", "");
        return version.Length == 0 ? null : "net" + version;
    }

    /// <summary>
    /// Distinguishes .NET Framework from modern .NET by the moniker's shape: modern monikers carry
    /// a dotted version (<c>net10.0</c>), .NET Framework ones do not (<c>net48</c>).
    /// </summary>
    private static RuntimeFlavor InferFlavor(string? tfm, ProjectStyle style)
    {
        if (string.IsNullOrEmpty(tfm))
        {
            // A legacy project file without any moniker predates the SDK, so it is .NET Framework.
            return style == ProjectStyle.Legacy ? RuntimeFlavor.NetFramework : RuntimeFlavor.Unknown;
        }

        if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            return RuntimeFlavor.NetStandard;

        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return RuntimeFlavor.NetCore;

        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return tfm.Contains('.') ? RuntimeFlavor.NetCore : RuntimeFlavor.NetFramework;

        return RuntimeFlavor.Unknown;
    }

    private static AppKind InferAppKind(string projectPath, RawProjectProperties props, ProjectStyle style)
    {
        if (props.Sdk is { } sdk && (
                sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                sdk.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) ||
                sdk.Contains("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase)))
            return AppKind.AspNetCore;

        // A legacy System.Web site: a library-shaped project with a web.config beside it and the
        // WebProjectProperties block Visual Studio's web flavor writes.
        if (style == ProjectStyle.Legacy && props.HasWebProjectProperties && HasWebConfig(projectPath))
            return AppKind.AspNetClassic;

        return props.OutputType?.Trim().ToLowerInvariant() switch
        {
            "exe" => AppKind.ConsoleApp,
            "winexe" => AppKind.WindowsApp,
            "library" => AppKind.ClassLibrary,

            // An SDK-style project without an explicit OutputType builds a library; a legacy one
            // always states it, so a missing value there means the file could not be read.
            _ => style == ProjectStyle.Sdk ? AppKind.ClassLibrary : AppKind.Unknown,
        };
    }

    private static bool HasWebConfig(string projectPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(projectPath);
            return dir is not null && File.Exists(Path.Combine(dir, "web.config"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the evaluated target framework off the compilation's preprocessor symbols, which the
    /// SDK derives from the resolved TFM. Returns the most specific moniker available.
    /// </summary>
    private static (RuntimeFlavor Flavor, string? TargetFramework) InferFrameworkFromSymbols(Project project)
    {
        if (project.ParseOptions is not CSharpParseOptions parseOptions)
            return (RuntimeFlavor.Unknown, null);

        var symbols = parseOptions.PreprocessorSymbolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A project defines every version symbol up to its target (NET10_0 alongside NET9_0), so
        // the highest version in the most specific family is the actual target — not whichever
        // symbol the set happens to enumerate first.
        var modern = BestVersioned(symbols, "NET", underscored: true);
        if (modern is not null)
            return (RuntimeFlavor.NetCore, modern);

        var core = BestVersioned(symbols, "NETCOREAPP", underscored: true);
        if (core is not null)
            return (RuntimeFlavor.NetCore, core);

        var standard = BestVersioned(symbols, "NETSTANDARD", underscored: true);
        if (standard is not null)
            return (RuntimeFlavor.NetStandard, standard);

        var framework = BestVersioned(symbols, "NET", underscored: false);
        if (framework is not null)
            return (RuntimeFlavor.NetFramework, framework);

        // Family symbols without a version still identify the flavor; the caller renders these.
        if (symbols.Contains("NETFRAMEWORK")) return (RuntimeFlavor.NetFramework, null);
        if (symbols.Contains("NETCOREAPP")) return (RuntimeFlavor.NetCore, null);
        if (symbols.Contains("NETSTANDARD")) return (RuntimeFlavor.NetStandard, null);

        return (RuntimeFlavor.Unknown, null);
    }

    /// <summary>
    /// Finds the highest-versioned symbol in one family and returns it as a lowercase moniker.
    /// </summary>
    /// <param name="underscored">
    /// Distinguishes the two symbol shapes that share the <c>NET</c> prefix: modern .NET separates
    /// major from minor with an underscore (<c>NET10_0</c> → <c>net10.0</c>) while .NET Framework
    /// runs the digits together (<c>NET472</c> → <c>net472</c>).
    /// </param>
    private static string? BestVersioned(HashSet<string> symbols, string prefix, bool underscored)
    {
        string? best = null;
        var bestKey = (Major: -1, Minor: -1);

        foreach (var symbol in symbols)
        {
            // "_OR_GREATER" variants describe compatibility, not the target itself.
            if (symbol.EndsWith("_OR_GREATER", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = symbol[prefix.Length..];
            if (suffix.Length == 0 || !char.IsDigit(suffix[0]))
                continue;
            if (suffix.Contains('_') != underscored)
                continue;

            // NETCOREAPP would otherwise also match the shorter "NET" prefix.
            if (prefix == "NET" && (
                    symbol.StartsWith("NETCOREAPP", StringComparison.OrdinalIgnoreCase) ||
                    symbol.StartsWith("NETSTANDARD", StringComparison.OrdinalIgnoreCase)))
                continue;

            var parts = suffix.Split('_');
            if (!int.TryParse(parts[0], out var major))
                continue;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;

            if ((major, minor).CompareTo(bestKey) <= 0)
                continue;

            bestKey = (major, minor);
            best = underscored
                ? symbol.ToLowerInvariant().Replace('_', '.')
                : symbol.ToLowerInvariant();
        }

        return best;
    }
}
