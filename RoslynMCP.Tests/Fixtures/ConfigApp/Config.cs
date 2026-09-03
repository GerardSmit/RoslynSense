using Microsoft.Extensions.Configuration;

namespace ConfigApp;

/// <summary>
/// The read wrapped once and then called everywhere — where a scan that knows only the
/// framework's own shapes stops, and every call below it goes uncounted.
/// </summary>
public static class Config
{
    private static IConfiguration _configuration = null!;

    public static string? GetSetting(string setting) => _configuration[setting];

    /// <summary>A wrapper rooted in a section: its callers name keys inside that section.</summary>
    public static string? GetWrapped(string key) => _configuration.GetSection("Wrapped")[key];

    /// <summary>The same shape over another collection, which reads no setting at all.</summary>
    public static string? GetLocal(string key) => Extras[key];

    private static readonly Dictionary<string, string> Extras = new();
}

/// <summary>The call sites, which name a key and nothing else.</summary>
public class WrapperCallers
{
    public string? Timeout() => Config.GetSetting("Wrapped:Timeout");

    public string? Deep() => Config.GetWrapped("Deep");

    public string? Decoy() => Config.GetLocal("Orphan:Dead");
}
