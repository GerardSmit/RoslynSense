using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMCP.Config;

public static class RoslynSenseConfigLoader
{
    public const string FileName = "roslynsense.json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static (RoslynSenseConfig? Config, string? FilePath, string? LoadError) Load(string startDir)
    {
        if (string.IsNullOrEmpty(startDir))
            return (null, null, null);

        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(startDir); }
        catch { return (null, null, null); }

        while (dir is not null && dir.Exists)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
                return ParseFile(candidate);

            if (dir.Parent is null) break;
            if (string.Equals(dir.FullName, dir.Root.FullName, StringComparison.OrdinalIgnoreCase)) break;

            dir = dir.Parent;
        }

        return (null, null, null);
    }

    private static (RoslynSenseConfig? Config, string? FilePath, string? LoadError) ParseFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return (new RoslynSenseConfig(), path, null);

            var cfg = JsonSerializer.Deserialize<RoslynSenseConfig>(json, s_options);
            return (cfg ?? new RoslynSenseConfig(), path, null);
        }
        catch (JsonException ex)
        {
            return (null, path, $"Invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            return (null, path, $"Read failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, path, $"Access denied: {ex.Message}");
        }
    }
}
