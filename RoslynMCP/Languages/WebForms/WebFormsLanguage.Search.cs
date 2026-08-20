using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp.Search;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageSearchContributor
{
    /// <summary>
    /// A pasted <c>ClientID</c> or <c>UniqueID</c>, resolved to the control the markup declares.
    /// </summary>
    /// <remarks>
    /// The id a user has in front of them when something is wrong — in a rendered page, a stack
    /// trace, an element inspector — is the one thing the ordinary search cannot answer. Its
    /// generated segments match no declaration, and a control inside an <c>&lt;ItemTemplate&gt;</c>
    /// has no declaration to match in the first place.
    /// </remarks>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query, Solution solution, CancellationToken ct)
    {
        // Decided from the query text alone, before a markup file is touched. Everything the user
        // ever types in the picker lands here, and nearly all of it leaves on this line.
        if (ClientIdQuery.Parse(query) is not { } segments)
            return [];

        return await ClientIdSearch.ResolveAsync(solution, segments, ct);
    }
}
