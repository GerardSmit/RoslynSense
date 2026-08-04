using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A real DNN checkout, which the golden decomposition test reads file names out of.
/// </summary>
/// <remarks>
/// Not a fixture: the value of the corpus is that nobody curated it. Copying two hundred names
/// into the repository would freeze them at the moment they were copied and lose the one property
/// worth having, which is that they are what a real site puts on disk.
/// </remarks>
internal static class DnnPlatform
{
    public const string PathVariable = "ROSLYNSENSE_TEST_DNN_PLATFORM";

    public static string? Directory { get; } = Find();

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
            return IsCheckout(configured) ? configured : null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Dnn.Platform");
            if (IsCheckout(candidate))
                return candidate;

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>The platform directory rather than the repository root, so a directory that merely
    /// has the right name is not mistaken for a checkout.</summary>
    private static bool IsCheckout(string path) =>
        System.IO.Directory.Exists(Path.Combine(path, "DNN Platform"));
}

/// <summary>Skips when no DNN checkout is beside the repository.</summary>
public sealed class DnnPlatformFactAttribute : FactAttribute
{
    public DnnPlatformFactAttribute()
    {
        if (DnnPlatform.Directory is null)
            Skip = $"No DNN checkout found; set {DnnPlatform.PathVariable} to one to run the golden decomposition.";
    }
}
