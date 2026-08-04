using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageDiagnosticProvider
{
    /// <summary>RSX0001 — "The key '{0}' is declared more than once in this file".</summary>
    private const string DuplicateKey = "RSX0001";

    /// <summary>RSX0002 — "'{0}' declares {1} this translation does not: {2}".</summary>
    private const string MissingTranslation = "RSX0002";

    /// <summary>What the server calls itself in every diagnostic it publishes.</summary>
    private const string DiagnosticSource = "roslyn-sense";

    /// <summary>How many missing keys a single message names before it stops being readable.</summary>
    private const int MaxNamedKeys = 5;

    /// <summary>
    /// What is wrong with one <c>.resx</c>: a key it declares twice, and — when it is a
    /// translation — the keys its original has that it does not.
    /// </summary>
    public Task<Diagnostic[]> DiagnosticsAsync(string filePath, CancellationToken ct)
    {
        if (ResourceCatalogService.Text(filePath) is not { } text)
            return Task.FromResult(Array.Empty<Diagnostic>());

        var contents = ResxReader.Read(text);
        var diagnostics = new List<Diagnostic>();

        foreach (string key in contents.DuplicateKeys)
        {
            ct.ThrowIfCancellationRequested();

            // The reader keeps the first entry, so this points at the declaration the file will go
            // on using rather than at the copy. Either one is the fix, and the first is the one
            // whose span is known.
            if (!contents.Entries.TryGetValue(key, out var entry) || entry.KeySpan == default)
                continue;

            diagnostics.Add(new Diagnostic(
                LspConverters.ToRange(text.Lines, entry.KeySpan),
                LspConverters.ToLspSeverity(DiagnosticSeverity.Warning),
                DuplicateKey,
                DiagnosticSource,
                $"The key '{key}' is declared more than once in this file."));
        }

        if (Untranslated(filePath, contents, text) is { } missing)
            diagnostics.Add(missing);

        return Task.FromResult(diagnostics.ToArray());
    }

    /// <summary>
    /// The keys this file's neutral original declares and it does not, as one report.
    /// </summary>
    /// <remarks>
    /// Information rather than a warning, and one diagnostic rather than one per key: an
    /// untranslated string still renders — <c>TryGetFromResourceFile</c> reads each file directly
    /// and falls back through the cascade — so this is the translator's worklist, not a defect. A
    /// row per key would put a hundred identical locations in the Problems panel and get the whole
    /// rule switched off.
    /// <para>
    /// Only a plain translation is compared. A customization is <em>meant</em> to carry the handful
    /// of keys it overrides and nothing else, so measuring <c>View.ascx.Portal-3.resx</c> against
    /// the base file would report every key in the family on every override in the solution.
    /// </para>
    /// </remarks>
    private Diagnostic? Untranslated(string filePath, ResxContents contents, SourceText text)
    {
        if (ResourceDocuments.FamilyOf(filePath, Settings.Discovery.Overrides) is not { } family)
            return null;

        if (ResourceDocuments.Member(family, filePath) is not { Culture: not null, OverrideRank: 0 })
            return null;

        if (family.Neutral is not { } neutral)
            return null;

        var original = ResourceCatalogService.Read(neutral);
        var missing = new List<string>();

        foreach (string key in original.Entries.Keys.Order(StringComparer.Ordinal))
        {
            if (!contents.Entries.ContainsKey(key))
                missing.Add(key);
        }

        if (missing.Count == 0)
            return null;

        string count = missing.Count == 1 ? "1 key" : $"{missing.Count} keys";

        return new Diagnostic(
            FirstLine(text),
            LspConverters.ToLspSeverity(DiagnosticSeverity.Info),
            MissingTranslation,
            DiagnosticSource,
            $"'{Path.GetFileName(neutral.FilePath)}' declares {count} this translation does not: "
            + $"{Names(missing)}.");
    }

    private static string Names(List<string> keys)
    {
        if (keys.Count <= MaxNamedKeys)
            return string.Join(", ", keys);

        return $"{string.Join(", ", keys.Take(MaxNamedKeys))} and {keys.Count - MaxNamedKeys} more";
    }

    /// <summary>
    /// Where a report about the file as a whole goes. The protocol has no range meaning "this
    /// document", and a zero-width one at the origin is a squiggle the user cannot see.
    /// </summary>
    private static Lsp.Protocol.Range FirstLine(SourceText text) =>
        LspConverters.ToRange(text.Lines, text.Lines[0].Span);
}
