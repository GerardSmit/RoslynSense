using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.WebConfig;

internal sealed partial class WebConfigLanguage : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
{
    /// <summary>
    /// What a lens count depends on: the buffer, the C# index over the project closure, and the
    /// markup usages — a count taken before the project finished loading must not outlive it.
    /// </summary>
    private sealed record LensGeneration(
        SourceText Text, ConfigurationManagerUsageIndex Index,
        ImmutableArray<ConfigSettingUsage> MarkupUsages, Solution Solution);

    public async ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct)
    {
        if (await WebConfigWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        return view.Project is { } project
            ? new LensGeneration(view.Text, view.Index, view.MarkupUsages, project.Solution)
            : null;
    }

    private const int MaxLensLocations = 100;

    private const string ReferencesKind = "references";

    /// <summary>
    /// A count over every <c>&lt;add&gt;</c> in both sections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emitted even when nothing reads the file, for the same reason the appsettings pack does:
    /// "0 references" over a setting nothing reads is the finding, not noise. A config file that
    /// has outlived three rewrites of the application it configures is exactly where the zeros
    /// are worth having.
    /// </para>
    /// <para>
    /// Counted here rather than at resolve, which is the same choice the override-chain lens beside
    /// it already makes and for the same reason: the answer is a bucketing of an index this call
    /// has already built, not a search. It is also what makes the lens survive a refresh. VS Code
    /// hands any command carrying arguments to an internal delegate keyed to the lens list it came
    /// in, and drops the key the moment a new list arrives — but the widget keeps drawing the old
    /// anchor until the new list is resolved. A lens that arrives already commanded re-renders a
    /// live key in the same message that killed the old one; one that arrives bare leaves a
    /// clickable anchor wired to a dead key for as long as its resolve takes, which during a
    /// solution load is seconds, and a click in that window reports a command that does not exist.
    /// </para>
    /// </remarks>
    public async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string uri = p.TextDocument.Uri;

        if (await WebConfigWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view
            || view.Project is null)
        {
            return [];
        }

        var lines = view.Text.Lines;
        var lenses = new List<LspCodeLens>();
        var external = await MetadataConfigurationIndex.GetAsync(view.Project, ct);
        var usages = WebConfigReferenceService.UsagesByName(view);

        foreach (var entry in view.Document.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.NameSpan == default || entry.NameSpan.End > view.Text.Length)
                continue;

            var start = lines.GetLinePosition(entry.NameSpan.Start);
            var range = LspConverters.ToRange(lines, entry.NameSpan);

            lenses.Add(new LspCodeLens(
                range,
                ReferenceCommand(
                    uri,
                    start.Line,
                    start.Character,
                    usages.GetValueOrDefault(new SettingKey(entry.Section, entry.Name), [])))
            {
                // Kept even though the command is already here: an LSP client is free to resolve
                // anyway, and the answer it gets back has to be the same one.
                Data = new CodeLensData(uri, start.Line, start.Character, ReferencesKind),
            });

            // Where else this name is decided — the nested configs that replace it, and the
            // application config it replaces.
            lenses.AddRange(ConfigOverrides.Lenses(
                WebConfigOverrides.ChainFor(view.Project.FilePath, entry.Section, entry.Name),
                view.FilePath, range));

            var kind = WebConfigMetadataReads.KindOf(entry.Section);

            if (ExternalReferences.Lens(external.ReadsFor(kind, entry.Name), uri, range) is { } lens)
                lenses.Add(lens);
        }

        return [.. lenses];
    }

    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: ReferencesKind } data)
            return lens;

        var locations = await LensLocationsAsync(data, ct);

        return lens with
        {
            Command = ReferenceCommand(data.Uri, data.Line, data.Character, locations),
        };
    }

    /// <summary>The click, wherever the count was taken.</summary>
    /// <remarks>A zero-count lens still carries the command with an empty location list: LSP
    /// requires a non-empty command id, and an empty peek is a sane result for a click.</remarks>
    private static Command ReferenceCommand(
        string uri, int line, int character, LspLocation[] locations) =>
        new(
            $"{locations.Length} {(locations.Length == 1 ? "reference" : "references")}",
            "roslynSense.showReferences",
            [uri, line, character, locations.Take(MaxLensLocations).ToArray()]);

    private static async Task<LspLocation[]> LensLocationsAsync(CodeLensData data, CancellationToken ct)
    {
        if (await WebConfigWorkspace.GetAsync(LspConverters.UriToPath(data.Uri), ct) is not { } view)
            return [];

        int offset = LspConverters.ToOffset(view.Text, new Position(data.Line, data.Character));

        return view.Document.EntryAt(offset) is { } entry
            ? WebConfigReferenceService.Usages(view, entry)
            : [];
    }
}
