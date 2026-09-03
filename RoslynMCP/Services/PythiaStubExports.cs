using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.ExternalAccess.Pythia.Api;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ExternalAccess.Pythia.Api;

namespace RoslynMCP.Services;

// Roslyn's CSharp.Features MEF catalog contains Pythia (IntelliCode) provider parts whose
// implementation contracts only exist inside Visual Studio. Their imports are required, so
// any export query touching the same contract (e.g. every ISignatureHelpProvider for
// SignatureHelpService) fails composition outright. These no-op exports satisfy the
// contracts; the providers then simply contribute nothing.

[Export(typeof(IPythiaSignatureHelpProviderImplementation)), Shared]
internal sealed class NullPythiaSignatureHelpImplementation : IPythiaSignatureHelpProviderImplementation
{
    public Task<(ImmutableArray<PythiaSignatureHelpItemWrapper> items, int? selectedItemIndex)>
        GetMethodGroupItemsAndSelectionAsync(
            ImmutableArray<IMethodSymbol> accessibleMethods,
            Document document,
            InvocationExpressionSyntax invocationExpression,
            SemanticModel semanticModel,
            SymbolInfo currentSymbol,
            CancellationToken cancellationToken) =>
        Task.FromResult((ImmutableArray<PythiaSignatureHelpItemWrapper>.Empty, (int?)null));
}

[Export(typeof(IPythiaDeclarationNameRecommenderImplementation)), Shared]
internal sealed class NullPythiaDeclarationNameRecommender : IPythiaDeclarationNameRecommenderImplementation
{
    public Task<ImmutableArray<string>> ProvideRecommendationsAsync(
        PythiaDeclarationNameContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ImmutableArray<string>.Empty);
}
