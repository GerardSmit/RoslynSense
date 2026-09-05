using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.SemanticModelReuse;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace RoslynMCP.Services;

/// <summary>Semantic models for the exact snapshot selected by the feature handler.</summary>
/// <remarks>
/// Roslyn's default speculative-model service returns its previous real model when a method's
/// syntax tree is unchanged, before checking dependent semantic versions. A property edited in
/// A.cs consequently leaves completion in unchanged B.cs using A's old declarations. Our handlers
/// already select a frozen or full snapshot, and completion retains that snapshot's semantic model
/// before asking its providers. Reusing Document's own model cache preserves that selection without
/// retaining a second model keyed only by the caller's syntax tree.
/// </remarks>
[ExportWorkspaceService(typeof(ISemanticModelReuseWorkspaceService), ServiceLayer.Host), Shared]
internal sealed class SnapshotSemanticModelService : ISemanticModelReuseWorkspaceService
{
    [ImportingConstructor]
    public SnapshotSemanticModelService()
    {
    }

    public async ValueTask<SemanticModel> ReuseExistingSpeculativeModelAsync(
        Document document, SyntaxNode node, CancellationToken cancellationToken) =>
        await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
}
