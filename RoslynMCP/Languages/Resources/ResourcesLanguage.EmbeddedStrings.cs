using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Resources.Core;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// A resource key inside a C# string literal, claimed by the pack rather than found by Roslyn.
/// </summary>
/// <remarks>
/// The one embedded language whose literals carry no signal at all: no <c>[StringSyntax]</c> could
/// be written onto <c>Localization.GetString</c> without owning the assembly it lives in, and what
/// a key really is — argument N of a method named in <c>roslynsense.json</c> — is not something
/// Roslyn has a way to say. See <see cref="IConfiguredStringLanguage"/>.
/// </remarks>
internal sealed partial class ResourcesLanguage : IConfiguredStringLanguage
{
    /// <summary>What a claimed token reports as its language, and what <c>// lang=resx</c> above a
    /// literal names.</summary>
    private const string SyntaxIdentifier = "Resx";

    public ImmutableArray<string> StringSyntaxIdentifiers { get; } = [SyntaxIdentifier];

    public Task<string?> DetectAsync(
        Document document, SyntaxToken token, SemanticModel semanticModel, CancellationToken ct) =>
        Task.FromResult(
            ResourceKeySearch.IsKeyLiteral(Settings, semanticModel, token, ct) ? SyntaxIdentifier : null);

    /// <summary>
    /// The key under the caret and the families it could be read from, or null when the literal
    /// turns out not to be a key after all.
    /// </summary>
    /// <remarks>
    /// The match is redone rather than carried over from <see cref="Detect"/>:
    /// <see cref="EmbeddedStringContext"/> is the seam's own shape and has nowhere to put a
    /// pack-private payload, and re-matching costs one bind against a call this snapshot has
    /// already bound once.
    /// <para>
    /// Deliberately not <see cref="ResourceKeySearch.LocateAsync"/>, which the rename and reference
    /// paths use: that one keeps only the families that declare the key, and a key nothing declares
    /// is exactly what hover has to explain and the missing-key diagnostic has to report.
    /// </para>
    /// </remarks>
    private async Task<ResourceKeySearch.CodeMatch?> KeyAtAsync(
        EmbeddedStringContext context, CancellationToken ct)
    {
        var project = context.Document.Project;
        var catalog = await CatalogAsync(project, ct);

        return await ResourceKeySearch.KeyAtAsync(
            Settings, catalog, project, context.SemanticModel, context.Token, ct);
    }

    /// <summary>The candidate families with their key tables read, in precedence order.</summary>
    private static ImmutableArray<ResourceFamily> Loaded(ResourceKeySearch.CodeMatch match)
    {
        var families = ImmutableArray.CreateBuilder<ResourceFamily>(match.Candidates.Length);

        foreach (var candidate in match.Candidates)
            families.Add(ResourceCatalogService.Load(candidate));

        return families.ToImmutable();
    }

    /// <summary>
    /// Every file of every candidate that declares the key: the neutral one first, then the
    /// translations by culture, then the customizations by rank.
    /// </summary>
    /// <remarks>
    /// A key that only a translation or a customization declares is a note rather than an error.
    /// <c>TryGetFromResourceFile</c> reads each file directly and never requires the neutral one to
    /// carry the key first, so such a key is listed rather than treated as absent.
    /// </remarks>
    private static IEnumerable<ResourceFileIndex> Declaring(
        ImmutableArray<ResourceFamily> families, string key)
    {
        foreach (var family in families)
        {
            foreach (var file in family.Files)
            {
                if (file.Entries.ContainsKey(key))
                    yield return file;
            }
        }
    }
}
