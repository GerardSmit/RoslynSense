namespace RoslynMCP.Lsp.Search;

/// <summary>
/// Which files a search may return, and which of them belong at the bottom.
/// </summary>
/// <remarks>
/// Two separate ideas. <see cref="IsExcluded"/> is about build output: nothing under
/// <c>obj/</c> or <c>bin/</c> is a thing anyone navigates to, and it is where the generated
/// <c>AssemblyInfo.cs</c> of every SDK project lives — the reason that file used to be the first
/// hit for "assembly". <see cref="IsGenerated"/> is about files that <em>are</em> part of the
/// project but were written by a tool: still reachable, just never ahead of hand-written code.
/// </remarks>
public static class SearchFileRules
{
    private static readonly string[] s_excludedDirectories =
        ["obj", "bin", ".git", ".vs", "node_modules", "packages", "TestResults", ".idea"];

    /// <summary>Suffixes every generator in the .NET world reaches for.</summary>
    private static readonly string[] s_generatedSuffixes =
        [
            ".designer.cs", ".designer.vb", ".g.cs", ".g.i.cs", ".generated.cs",
            ".feature.cs", ".xaml.cs", ".razor.g.cs", "assemblyinfo.cs", "assemblyattributes.cs",
        ];

    private static readonly char[] s_separators = ['/', '\\'];

    /// <summary>
    /// Extensions with no text inside: images, archives, compiled and media files. Shared with
    /// <see cref="TextSearch"/>, which skips them — the name search still lists them, ranked last.
    /// </summary>
    private static readonly HashSet<string> s_binaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".zip", ".nupkg", ".snupkg", ".7z", ".gz", ".tar",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".svgz",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp3", ".mp4", ".wav", ".avi", ".mov",
        ".db", ".sqlite", ".mdf", ".ldf", ".cache", ".bin", ".dat", ".snk", ".pfx",
        ".xlsx", ".docx", ".pptx", ".pdf",
    };

    /// <summary>Build output and tooling directories: never a search result.</summary>
    public static bool IsExcluded(string path)
    {
        foreach (var segment in path.Split(s_separators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string excluded in s_excludedDirectories)
            {
                if (segment.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Tool-written but still part of the project — ranked last rather than hidden.</summary>
    public static bool IsGenerated(string path)
    {
        string name = Path.GetFileName(path);
        foreach (string suffix in s_generatedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An asset rather than something anyone reads: listed, but never ahead of code.</summary>
    public static bool IsBinaryAsset(string path) =>
        s_binaryExtensions.Contains(Path.GetExtension(path));
}
