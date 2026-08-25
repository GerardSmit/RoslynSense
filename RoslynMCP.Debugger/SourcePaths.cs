namespace RoslynMCP.Debugger;

/// <summary>
/// Comparing a path a build wrote with a path that exists here.
/// </summary>
/// <remarks>
/// The two are rarely the same string and often not even the same shape: a container build writes
/// <c>/src/App/Program.cs</c>, a deterministic build writes <c>/_/App/Program.cs</c>, and the file
/// on this machine is <c>D:\work\App\Program.cs</c>. What they do share is their tail, and how much
/// of it they share is the only ranking available before a checksum is read.
/// </remarks>
public static class SourcePaths
{
    /// <summary>
    /// How many trailing characters two paths share.
    /// </summary>
    /// <remarks>
    /// Counted only up to a whole path segment, so <c>App/Bar.cs</c> and <c>Quux/oBar.cs</c> are
    /// credited with nothing rather than with <c>Bar.cs</c> — half a file name is not evidence of
    /// anything. Both separators are treated as the same character, since a build on one platform
    /// read on another is exactly the case this exists for, and the comparison is
    /// case-insensitive because two of the three platforms involved are.
    /// </remarks>
    public static int SharedSuffixLength(string left, string right)
    {
        int shared = 0;
        int aligned = 0;
        int i = left.Length - 1;
        int j = right.Length - 1;

        while (i >= 0 && j >= 0)
        {
            char a = Separator(left[i]);
            char b = Separator(right[j]);
            if (char.ToLowerInvariant(a) != char.ToLowerInvariant(b))
                break;

            shared++;
            if (a == '/')
                aligned = shared;
            i--;
            j--;
        }

        // One path consumed entirely is aligned by definition: there is no earlier segment left for
        // the boundary to fall in.
        return i < 0 || j < 0 ? shared : aligned;
    }

    /// <summary>The path with both separators written the same way, for comparing.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/');

    private static char Separator(char c) => c == '\\' ? '/' : c;
}
