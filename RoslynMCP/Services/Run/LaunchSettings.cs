using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMCP.Services.Run;

/// <summary>One profile from <c>Properties/launchSettings.json</c>.</summary>
public sealed record LaunchProfileInfo
{
    public required string Name { get; init; }

    /// <summary>
    /// <c>Project</c>, <c>Executable</c>, <c>IISExpress</c>, <c>IIS</c>, <c>SPAProxy</c> or
    /// <c>DebugRoslynComponent</c>. Absent in the file means <c>Project</c>, which is what the
    /// dotnet CLI assumes.
    /// </summary>
    public required string CommandName { get; init; }

    public string? ApplicationUrl { get; init; }
    public string? CommandLineArgs { get; init; }
    public string? ExecutablePath { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool LaunchBrowser { get; init; }
    public string? LaunchUrl { get; init; }
    public bool UseSsl { get; init; }
    public bool? HotReloadEnabled { get; init; }

    /// <summary>The consuming project of a <c>DebugRoslynComponent</c> profile.</summary>
    public string? TargetProject { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this profile describes something this server knows how to start.</summary>
    public bool IsLaunchable => CommandName is "Project" or "Executable" or "IISExpress";
}

/// <summary>
/// The whole of a project's <c>Properties/launchSettings.json</c>: every profile plus the
/// <c>iisSettings</c> block, read once so a caller can offer a picker rather than guess a name.
/// </summary>
/// <remarks>
/// Reading is total — an unreadable or malformed file yields <c>null</c> rather than failing a
/// run, because a launch profile only ever refines a launch that already has a default. Writing
/// goes through <see cref="JsonNode"/> so profiles and settings this server does not model
/// survive the round trip untouched.
/// </remarks>
public sealed class LaunchSettings
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public required string Path { get; init; }

    public required IReadOnlyList<LaunchProfileInfo> Profiles { get; init; }

    /// <summary>The <c>iisSettings.iisExpress.applicationUrl</c>, which an IISExpress profile
    /// binds to instead of carrying a URL of its own.</summary>
    public string? IisExpressApplicationUrl { get; init; }

    public static string FilePath(string projectDir) =>
        System.IO.Path.Combine(projectDir, "Properties", "launchSettings.json");

    public static LaunchSettings? Load(string projectDir)
    {
        var path = FilePath(projectDir);
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), ReadOptions);
            var root = document.RootElement;

            var profiles = new List<LaunchProfileInfo>();
            if (root.TryGetProperty("profiles", out var profilesElement) &&
                profilesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var profile in profilesElement.EnumerateObject())
                {
                    if (profile.Value.ValueKind == JsonValueKind.Object)
                        profiles.Add(Parse(profile.Name, profile.Value));
                }
            }

            return new LaunchSettings
            {
                Path = path,
                Profiles = profiles,
                IisExpressApplicationUrl = ReadIisExpressUrl(root),
            };
        }
        catch (Exception)
        {
            // A malformed launchSettings.json falls back to defaults rather than blocking the run.
            return null;
        }
    }

    /// <summary>
    /// The profile a launch should use: the named one, else the first that can actually be
    /// started — which is what the dotnet CLI picks when no profile is given.
    /// </summary>
    public LaunchProfileInfo? Select(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Profiles.FirstOrDefault(
                p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        return Profiles.FirstOrDefault(p => p.CommandName == "Project")
            ?? Profiles.FirstOrDefault(p => p.IsLaunchable);
    }

    /// <summary>
    /// Writes <paramref name="url"/> as a profile's <c>applicationUrl</c>, creating the file and
    /// the profile when they are missing. This is how a web project gets a port that survives a
    /// restart instead of one this server picked for it.
    /// </summary>
    /// <returns>The error, or <c>null</c> when the file was written.</returns>
    public static string? SetApplicationUrl(string projectDir, string profileName, string url)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return "A profile name is required.";

        if (!Uri.TryCreate(url.Split(';').FirstOrDefault(), UriKind.Absolute, out _))
            return $"'{url}' is not an absolute URL.";

        try
        {
            var path = FilePath(projectDir);
            var root = ReadOrCreateRoot(path);

            if (root["profiles"] is not JsonObject profiles)
            {
                profiles = [];
                root["profiles"] = profiles;
            }

            // Match case-insensitively so an existing profile is edited rather than shadowed by a
            // near-duplicate that the CLI would then never pick.
            var existingName = profiles
                .Select(p => p.Key)
                .FirstOrDefault(k => k.Equals(profileName, StringComparison.OrdinalIgnoreCase));

            if (existingName is not null && profiles[existingName] is JsonObject existing)
            {
                existing["applicationUrl"] = url;
            }
            else
            {
                profiles[profileName] = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["launchBrowser"] = true,
                    ["applicationUrl"] = url,
                    ["environmentVariables"] = new JsonObject
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    },
                };
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static JsonObject ReadOrCreateRoot(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonNode.Parse(
                File.ReadAllText(path),
                nodeOptions: null,
                documentOptions: ReadOptions) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // Overwriting a file we cannot parse would silently discard the user's profiles.
            throw new InvalidOperationException(
                $"'{path}' is not valid JSON. Fix it before setting a launch URL.");
        }
    }

    private static LaunchProfileInfo Parse(string name, JsonElement profile)
    {
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

        return new LaunchProfileInfo
        {
            Name = name,
            CommandName = String(profile, "commandName") ?? "Project",
            ApplicationUrl = String(profile, "applicationUrl"),
            CommandLineArgs = String(profile, "commandLineArgs"),
            ExecutablePath = String(profile, "executablePath"),
            WorkingDirectory = String(profile, "workingDirectory"),
            LaunchBrowser = Bool(profile, "launchBrowser") ?? false,
            LaunchUrl = String(profile, "launchUrl"),
            UseSsl = Bool(profile, "useSSL") ?? false,
            HotReloadEnabled = Bool(profile, "hotReloadEnabled"),
            TargetProject = String(profile, "targetProject"),
            EnvironmentVariables = environment,
        };
    }

    private static string? ReadIisExpressUrl(JsonElement root) =>
        root.TryGetProperty("iisSettings", out var iisSettings) &&
        iisSettings.ValueKind == JsonValueKind.Object &&
        iisSettings.TryGetProperty("iisExpress", out var iisExpress) &&
        iisExpress.ValueKind == JsonValueKind.Object
            ? String(iisExpress, "applicationUrl")
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
