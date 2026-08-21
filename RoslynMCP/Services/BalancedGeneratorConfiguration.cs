using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMCP.Services;

/// <summary>
/// Runs source generators the way Visual Studio does: on save and build, not on every fork of the
/// solution. The MEF default for <see cref="IWorkspaceConfigurationService"/> returns
/// <see cref="SourceGeneratorExecutionPreference.Automatic"/>, under which every edit fork re-runs
/// the generator drivers before diagnostics, code lens or an overlay snapshot can bind; VS and the
/// Roslyn LSP servers override it to Balanced, where a fork reattaches the previously generated
/// trees and regeneration waits for the explicit version bumps the daemon already sends
/// (<c>EnqueueUpdateSourceGeneratorVersion</c> on didSave and after builds — see LspServer).
/// This export outranks the default by living in <see cref="ServiceLayer.Host"/>.
/// </summary>
/// <remarks>
/// Generated documents are deliberately stale between saves — Visual Studio's shipped trade-off.
/// Tools that must observe fresh generator output enqueue a forced version bump first.
/// </remarks>
[ExportWorkspaceService(typeof(IWorkspaceConfigurationService), ServiceLayer.Host), Shared]
internal sealed class BalancedGeneratorConfiguration : IWorkspaceConfigurationService
{
    [ImportingConstructor]
    public BalancedGeneratorConfiguration()
    {
    }

    public WorkspaceConfigurationOptions Options { get; } =
        new(SourceGeneratorExecution: SourceGeneratorExecutionPreference.Balanced);
}
