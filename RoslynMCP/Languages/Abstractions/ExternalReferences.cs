using System.Text;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages;

/// <summary>
/// How a setting read from a referenced assembly is shown: a lens beside the declaration, and a
/// line in its hover naming the assemblies.
/// </summary>
/// <remarks>
/// Kept apart from the reference count rather than folded into it. A reference count is a promise
/// that clicking lands on the code — these land on decompiled output, or on nothing when the
/// assembly cannot be decompiled, and quietly inflating "3 references" to "4" would make the
/// count mean two different things depending on which of its members you clicked.
/// </remarks>
internal static class ExternalReferences
{
    /// <summary>
    /// The lens for a setting some referenced assembly reads, or null when none does. No zero
    /// lens: unlike the reference count, where nothing reading a key is the finding, no external
    /// reader is the ordinary case for nearly every key in the file.
    /// </summary>
    public static LspCodeLens? Lens(
        IEnumerable<MetadataConfigurationRead> reads, string uri, LspRange range)
    {
        int count = reads.Count();

        if (count == 0)
            return null;

        return new LspCodeLens(range, new Command(
            count == 1 ? "1 external reference" : $"{count} external references",
            "roslynSense.showExternalConfigReads",
            [uri, range.Start.Line, range.Start.Character]));
    }

    /// <summary>The assemblies reading a setting, for the hover that the lens has no room for.</summary>
    public static void Append(StringBuilder builder, IEnumerable<MetadataConfigurationRead> reads)
    {
        var assemblies = reads
            .Select(read => read.AssemblyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (assemblies.Count == 0)
            return;

        builder.Append("\n\nRead by ");

        for (int i = 0; i < assemblies.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append('`').Append(assemblies[i]).Append('`');
        }

        builder.Append(" — compiled code, with no source in this solution.");
    }
}
