using System.Security.Cryptography;
using System.Text;

namespace RoslynMCP.Config;

/// <summary>
/// Where the personal layers live. One place because two programs have to agree on it — the
/// server resolving settings, and the editor extension writing them from its settings page.
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// The environment variable that moves the personal layers somewhere else. Set it and both
    /// <see cref="GlobalConfigFile"/> and <see cref="PersonalConfigFile"/> resolve underneath it.
    /// </summary>
    /// <remarks>
    /// For the test suite above all, which must not read or write the machine's real settings, and
    /// for anyone running several isolated configurations side by side.
    /// </remarks>
    public const string HomeOverrideVariable = "ROSLYNSENSE_HOME";

    /// <summary>The per-machine directory: <c>~/.roslynsense</c>.</summary>
    /// <remarks>
    /// The user profile rather than <c>ApplicationData</c>, so the path is the same sentence on
    /// every platform and a person can find it without knowing what a roaming profile is. Empty
    /// when the profile directory cannot be resolved, which is how a sandboxed process with no
    /// home ends up with no personal layers rather than with an exception.
    /// </remarks>
    public static string HomeDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable(HomeOverrideVariable) is { Length: > 0 } overridden)
                return overridden;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return profile.Length == 0 ? string.Empty : Path.Combine(profile, ".roslynsense");
        }
    }

    /// <summary>The global <c>roslynsense.json</c>, or null when there is no home directory.</summary>
    public static string? GlobalConfigFile =>
        HomeDirectory is { Length: > 0 } home
            ? Path.Combine(home, RoslynSenseConfigLoader.FileName)
            : null;

    /// <summary>
    /// The personal per-directory <c>roslynsense.json</c> for a working directory, or null when
    /// there is no home directory.
    /// </summary>
    public static string? PersonalConfigFile(string workingDirectory)
    {
        if (HomeDirectory is not { Length: > 0 } home || string.IsNullOrWhiteSpace(workingDirectory))
            return null;

        return Path.Combine(
            home, "projects", MangleDirectory(workingDirectory), RoslynSenseConfigLoader.FileName);
    }

    /// <summary>
    /// A directory path flattened into one file-name-safe segment:
    /// <c>D:\Sources\RoslynSense</c> becomes <c>D--Sources-RoslynSense-1f0c2a9b</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two halves, and both are load-bearing. The readable half replaces every character that is
    /// not a letter or a digit with a dash, because the person looking at
    /// <c>~/.roslynsense/projects</c> is trying to work out which of these directories is theirs
    /// and a hash alone tells them nothing. That half is lossy — <c>D:\a\b-c</c> and
    /// <c>D:\a-b\c</c> both flatten to <c>D--a-b-c</c> — so the second half is eight hex digits of
    /// the full path, which is what actually keeps two checkouts apart. Personal settings quietly
    /// applying to the wrong solution is not a failure anyone would think to look for.
    /// </para>
    /// <para>
    /// The path is normalised first, so <c>D:\Sources\RoslynSense\</c> and
    /// <c>D:/Sources/RoslynSense</c> land on the same segment, and the hash is taken over the
    /// lower-cased form so a differently-cased spelling of the same Windows directory does too.
    /// </para>
    /// </remarks>
    public static string MangleDirectory(string directory)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch { full = directory.Trim(); }

        var mangled = new StringBuilder(full.Length + 9);
        foreach (char c in full)
            mangled.Append(char.IsLetterOrDigit(c) ? c : '-');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        mangled.Append('-').Append(Convert.ToHexString(hash, 0, 4).ToLowerInvariant());

        return mangled.ToString();
    }
}
