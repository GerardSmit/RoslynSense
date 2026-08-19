using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.MetadataConfiguration;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.AppSettings;

internal sealed partial class AppSettingsLanguage : ILanguageCodeLensProvider, ILanguageCodeLensGeneration
{
    /// <summary>
    /// What a lens count depends on: the buffer, the usage index over the project closure, and
    /// the solution — a bound property's references live in projects that may still be loading,
    /// and a count taken before they arrive must not outlive their arrival.
    /// </summary>
    private sealed record LensGeneration(
        SourceText Text, ConfigurationUsageIndex Index, Solution Solution);

    public async ValueTask<object?> LensGenerationAsync(string uri, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return null;

        return view.Project is { } project
            ? new LensGeneration(view.Text, view.Index, project.Solution)
            : null;
    }

    private const int MaxLensLocations = 100;

    private const string ReferencesKind = "references";

    /// <summary>
    /// A count over every key. The counting is deferred to resolve — codeLens re-fires on every
    /// edit and scroll, and a bound key's count is a solution-wide symbol search.
    /// </summary>
    /// <remarks>
    /// Emitted even when the index is empty, unlike the Dbml pack's: an empty index here does not
    /// mean "not built yet", it means the code reads no configuration — and "0 references" over a
    /// key nothing reads is the finding, not noise. A settings file accumulates keys for code
    /// that was deleted years ago, and the zeros are how they are found.
    /// </remarks>
    public async Task<LspCodeLens[]> CodeLensAsync(CodeLensParams p, CancellationToken ct)
    {
        string uri = p.TextDocument.Uri;

        if (await AppSettingsWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view
            || view.Project is null)
        {
            return [];
        }

        var lines = view.Text.Lines;
        var lenses = new List<LspCodeLens>();
        var external = await MetadataConfigurationIndex.GetAsync(view.Project, ct);

        foreach (var key in view.Document.Keys)
        {
            ct.ThrowIfCancellationRequested();

            if (key.NameSpan.IsEmpty)
                continue;

            var start = lines.GetLinePosition(key.NameSpan.Start);
            var range = LspConverters.ToRange(lines, key.NameSpan);

            lenses.Add(new LspCodeLens(range, Command: null)
            {
                Data = new CodeLensData(uri, start.Line, start.Character, ReferencesKind),
            });

            // Where else this key is decided. Counted here rather than at resolve: the chain is
            // the other configuration files, which are read from a cache, not searched for.
            lenses.AddRange(ConfigOverrides.Lenses(
                AppSettingsOverrides.ChainFor(view.Project.FilePath, key.Path),
                view.FilePath, range));

            if (ExternalReferences.Lens(
                    external.ReadsFor(MetadataConfigurationKind.Path, key.Path), uri, range) is { } lens)
            {
                lenses.Add(lens);
            }
        }

        return [.. lenses];
    }

    public async Task<LspCodeLens> ResolveCodeLensAsync(LspCodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { Kind: ReferencesKind } data)
            return lens;

        var locations = await LensLocationsAsync(data, ct);

        // A zero-count lens still carries the command with an empty location list: LSP requires
        // a non-empty command id, and an empty peek is a sane result for a click.
        return lens with
        {
            Command = new Command(
                $"{locations.Length} {(locations.Length == 1 ? "reference" : "references")}",
                "roslynSense.showReferences",
                [data.Uri, data.Line, data.Character, locations.Take(MaxLensLocations).ToArray()]),
        };
    }

    private static async Task<LspLocation[]> LensLocationsAsync(CodeLensData data, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(LspConverters.UriToPath(data.Uri), ct) is not { } view)
            return [];

        int offset = LspConverters.ToOffset(view.Text, new Position(data.Line, data.Character));

        return view.Document.KeyAt(offset) is { } key
            ? await AppSettingsReferenceService.UsagesAsync(view, key, ct)
            : [];
    }
}
