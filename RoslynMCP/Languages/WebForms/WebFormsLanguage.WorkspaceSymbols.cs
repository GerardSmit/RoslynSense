using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PatternMatching;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageWorkspaceSymbolProvider
{
    /// <summary>Matches the cap the C# half applies, for the same reason: the client renders a
    /// picker, not a report.</summary>
    private const int MaxWorkspaceSymbols = 200;

    /// <summary>
    /// The declarations that only exist in markup: a control's <c>ID</c>, the page class an
    /// <c>Inherits</c> names, and the user controls a <c>&lt;%@ Register %&gt;</c> brings into
    /// scope. Roslyn's declaration search covers its own compilations, and none of these is in
    /// one.
    /// </summary>
    /// <remarks>
    /// Answered from <see cref="WebFormsIndex"/> rather than from the parse trees. Ctrl+T runs on
    /// every keystroke in the picker, and re-walking every page in the solution per keystroke is
    /// the difference between a usable feature and one that has to be switched off.
    /// </remarks>
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(
        string query, Solution solution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Roslyn's own matcher, so that "btnSub" and "bS" pick the same candidates in markup that
        // they pick in C# — a picker that ranked the two halves by different rules would read as
        // a bug.
        using var matcher = PatternMatcher.CreatePatternMatcher(query, includeMatchedSpans: false);

        var results = new List<SymbolInformation>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A multi-targeted project appears once per framework over the same directory, and
            // every one of them would contribute the same markup.
            if (project.FilePath is not { } path || !seenProjects.Add(path))
                continue;

            foreach (var file in await WebFormsIndex.ForProjectAsync(project, ct))
            {
                Collect(file, matcher, results);
                if (results.Count >= MaxWorkspaceSymbols)
                    return results;
            }
        }

        return results;
    }

    private static void Collect(
        WebFormsFileIndex file, PatternMatcher matcher, List<SymbolInformation> results)
    {
        string container = file.InheritsName ?? Path.GetFileName(file.FilePath);

        if (file.InheritsName is { Length: > 0 } pageClass && matcher.Matches(pageClass))
        {
            results.Add(new SymbolInformation(
                pageClass, LspSymbolKind.Class,
                At(file.FilePath, file.InheritsSpan),
                file.InheritsNamespace));
        }

        foreach (var control in file.Controls)
        {
            if (matcher.Matches(control.Id))
            {
                results.Add(new SymbolInformation(
                    control.Id, LspSymbolKind.Field,
                    At(file.FilePath, control.Span),
                    container));
            }
        }

        foreach (var registration in file.Registrations)
        {
            // The tag is what the user writes and therefore what they search for; the .ascx it
            // came from is the useful detail beside it.
            if (matcher.Matches(registration.TagName))
            {
                results.Add(new SymbolInformation(
                    $"{registration.Prefix}:{registration.TagName}", LspSymbolKind.Class,
                    At(file.FilePath, registration.Span),
                    registration.SourcePath ?? container));
            }
        }
    }

    private static LspLocation At(string filePath, LinePositionSpan span) =>
        new(LspConverters.PathToUri(filePath), LspConverters.ToRange(span));
}
