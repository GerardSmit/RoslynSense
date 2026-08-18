using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Run;

namespace RoslynMCP.Tools;

/// <summary>
/// Reading and writing <c>Properties/launchSettings.json</c>, which is what decides the URL,
/// arguments and environment a project runs with.
/// </summary>
[McpServerToolType]
public static class LaunchProfileTool
{
    [McpServerTool, Description(
        "List a project's launchSettings.json profiles, with the URL, arguments and environment " +
        "each one launches with, and which one RunProject uses by default. Pass the name to " +
        "RunProject's 'profile' parameter to use a specific one.")]
    public static string ListLaunchProfiles(
        [Description("Path to the .csproj.")]
        string projectPath)
    {
        var resolved = PathHelper.ResolveCsprojPath(projectPath);
        if (resolved is null)
            return $"Error: Could not find a .csproj for '{projectPath}'.";

        var projectDir = Path.GetDirectoryName(resolved)!;
        var settings = LaunchSettings.Load(projectDir);
        var spec = RunConfigResolver.Resolve(resolved);

        var sb = new StringBuilder();
        sb.AppendLine($"**Launch profiles — {Path.GetFileNameWithoutExtension(resolved)}**");
        sb.AppendLine();

        if (settings is null || settings.Profiles.Count == 0)
        {
            sb.AppendLine(
                $"No launchSettings.json at `{LaunchSettings.FilePath(projectDir)}`.");
            sb.AppendLine();
            if (spec.Url is not null)
            {
                sb.AppendLine(
                    $"Without one the project runs on `{spec.Url}` — a port derived from the " +
                    "project path, so it stays the same across runs. Use SetLaunchUrl to write " +
                    "it into a profile and make it explicit.");
            }
            return sb.ToString();
        }

        var selected = settings.Select(null)?.Name;

        sb.AppendLine("| Profile | Command | URL | Args | Environment |");
        sb.AppendLine("|---------|---------|-----|------|-------------|");
        foreach (var profile in settings.Profiles)
        {
            var marker = profile.Name == selected ? " *(default)*" : "";
            var environment = profile.EnvironmentVariables.Count == 0
                ? "-"
                : string.Join(", ", profile.EnvironmentVariables.Select(e => $"{e.Key}={e.Value}"));

            sb.AppendLine(
                $"| {profile.Name}{marker} | {profile.CommandName} | " +
                $"{profile.ApplicationUrl ?? settings.IisExpressApplicationUrl ?? "-"} | " +
                $"{profile.CommandLineArgs ?? "-"} | {environment} |");
        }

        sb.AppendLine();
        if (spec.CanRun && spec.Url is not null)
            sb.AppendLine($"RunProject would start it on **{spec.Url}**.");

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Pin the URL a project runs on by writing applicationUrl into its launchSettings.json " +
        "profile, creating the file or the profile when missing. Omit 'url' to write the stable " +
        "port the project already gets by default, which turns an implicit address into one the " +
        "project carries in source control.")]
    public static string SetLaunchUrl(
        [Description("Path to the .csproj.")]
        string projectPath,
        [Description("Absolute URL to bind, e.g. 'http://localhost:5080'. Semicolon-separated for " +
                     "several. Omit to keep the port the project already resolves to.")]
        string? url = null,
        [Description("Profile name to write. Defaults to the profile RunProject would pick, or the " +
                     "project name when there is no launchSettings.json yet.")]
        string? profile = null)
    {
        var resolved = PathHelper.ResolveCsprojPath(projectPath);
        if (resolved is null)
            return $"Error: Could not find a .csproj for '{projectPath}'.";

        var projectDir = Path.GetDirectoryName(resolved)!;
        var name = Path.GetFileNameWithoutExtension(resolved);
        var settings = LaunchSettings.Load(projectDir);

        var target = profile
            ?? settings?.Select(null)?.Name
            ?? name;

        if (string.IsNullOrWhiteSpace(url))
        {
            var spec = RunConfigResolver.Resolve(resolved);
            url = spec.Url ?? $"http://localhost:{RunConfigResolver.StablePort(resolved)}";
        }

        if (LaunchSettings.SetApplicationUrl(projectDir, target, url) is { } error)
            return $"Error: {error}";

        return $"Profile '{target}' of {name} now launches on {url} " +
               $"(`{LaunchSettings.FilePath(projectDir)}`).";
    }
}
