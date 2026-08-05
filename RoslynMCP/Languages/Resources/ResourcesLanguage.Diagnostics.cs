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

    /// <summary>RSX0002 — "'{0}' has no translation in {1}".</summary>
    private const string MissingTranslation = "RSX0002";

    /// <summary>What the server calls itself in every diagnostic it publishes.</summary>
    private const string DiagnosticSource = "roslyn-sense";

    /// <summary>How many names a single message lists before it stops being readable.</summary>
    private const int MaxNames = 5;

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

        diagnostics.AddRange(Untranslated(filePath, contents, text));

        return Task.FromResult(diagnostics.ToArray());
    }

    /// <summary>
    /// For each key this neutral file declares, the translations that do not — reported at the
    /// key's own declaration, because the missing entries have no position anywhere else.
    /// </summary>
    /// <remarks>
    /// Information rather than a warning: an untranslated string still renders —
    /// <c>TryGetFromResourceFile</c> reads each file directly and falls back through the cascade —
    /// so this is the translator's worklist, not a defect. One diagnostic per key, with the
    /// cultures folded into it, rather than one per (key, culture): the count is bounded by the
    /// file's own entries no matter how many languages the site ships.
    /// <para>
    /// Only plain translations are measured. A customization is <em>meant</em> to carry the handful
    /// of keys it overrides and nothing else, so counting <c>View.ascx.Portal-3.resx</c> would
    /// flag every key in the family on every override in the solution.
    /// </para>
    /// </remarks>
    private IEnumerable<Diagnostic> Untranslated(string filePath, ResxContents contents, SourceText text)
    {
        if (ResourceDocuments.FamilyOf(filePath, Settings.Discovery.Overrides) is not { } family)
            yield break;

        if (ResourceDocuments.Member(family, filePath) is not { Culture: null, OverrideRank: 0 })
            yield break;

        var translations = new List<ResourceFileIndex>();

        foreach (var file in family.Files)
        {
            if (file is { Culture: not null, OverrideRank: 0 })
                translations.Add(ResourceCatalogService.Read(file));
        }

        if (translations.Count == 0)
            yield break;

        foreach (var entry in contents.Entries.Values.OrderBy(e => e.KeySpan.Start))
        {
            if (entry.KeySpan == default)
                continue;

            var missing = new List<string>();

            foreach (var translation in translations)
            {
                if (!translation.Entries.ContainsKey(entry.Key))
                    missing.Add(translation.Culture!.Name);
            }

            if (missing.Count == 0)
                continue;

            missing.Sort(StringComparer.OrdinalIgnoreCase);

            yield return new Diagnostic(
                LspConverters.ToRange(text.Lines, entry.KeySpan),
                LspConverters.ToLspSeverity(DiagnosticSeverity.Info),
                MissingTranslation,
                DiagnosticSource,
                $"'{entry.Key}' has no translation in {Names(missing)}.");
        }
    }

    private static string Names(List<string> names)
    {
        if (names.Count <= MaxNames)
            return string.Join(", ", names);

        return $"{string.Join(", ", names.Take(MaxNames))} and {names.Count - MaxNames} more";
    }
}
