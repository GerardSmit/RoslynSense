using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMCP.Services;

/// <summary>
/// The MEF composition every workspace runs on, built once for the process.
/// </summary>
/// <remarks>
/// <para>
/// A class of its own rather than a field on <see cref="WorkspaceService"/>, and not for tidiness:
/// touching any <see cref="WorkspaceService"/> member runs its static initializer, which registers
/// MSBuild — a vswhere probe and locator hook worth ~200 ms that the composition does not need.
/// From here, the composition starts the moment the process knows it will want a workspace, while
/// MSBuild registration proceeds on whatever thread first asks for it; on a cold open the two run
/// side by side instead of back to back.
/// </para>
/// <para>
/// Sharing one composition is the intended use: a <c>MefHostServices</c> is a stateless container
/// of exports, Roslyn's own <c>MefHostServices.DefaultHost</c> is a process-wide singleton for
/// exactly this reason, and the per-workspace state that does exist lives on the
/// <c>Workspace</c> rather than on the host.
/// </para>
/// <para>
/// The own-assembly addition is what makes this a custom host rather than the default one: it
/// exports no-op implementations of the VS-only Pythia contracts that the C# feature providers
/// import, and without them composition fails at the first completion request
/// (see <c>PythiaStubExports</c>).
/// </para>
/// </remarks>
internal static class HostComposition
{
    private static readonly Lazy<MefHostServices> s_hostServices =
        new(() => MefHostServices.Create(
                MefHostServices.DefaultAssemblies
                    .Add(typeof(NullPythiaSignatureHelpImplementation).Assembly)),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The three workspaces-layer assemblies alone — everything an MSBuild evaluation workspace
    /// needs, and about a quarter of the full composition's wall time. The Features assemblies
    /// carry the completion, diagnostics and code-action exports that only the live workspace
    /// serves; a workspace that exists to drive BuildHost evaluations and be disposed never asks
    /// for any of them, and on a cold open the difference is the prewarm lanes starting while the
    /// full composition is still loading. The Pythia stub stays out for the same reason it exists
    /// at all: the contracts it satisfies are imported by feature providers this set does not
    /// contain.
    /// </summary>
    private static readonly Lazy<MefHostServices> s_liteHostServices =
        new(() => MefHostServices.Create(
                MefHostServices.DefaultAssemblies
                    .Where(a => !(a.GetName().Name ?? "").Contains(
                        "Features", StringComparison.Ordinal))),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static MefHostServices HostServices => s_hostServices.Value;

    /// <summary>For throwaway evaluation workspaces only — see <see cref="s_liteHostServices"/>.</summary>
    public static MefHostServices LiteHostServices => s_liteHostServices.Value;

    /// <summary>
    /// Builds the MEF composition ahead of the first request that needs it, on a background thread.
    /// </summary>
    /// <remarks>
    /// Pure warm-up: it loads no project, reads no solution and allocates nothing that is not going
    /// to be allocated anyway the moment the editor asks for anything semantic. It exists because
    /// the composition is unavoidable, fixed, and otherwise lands squarely inside the first request
    /// the user waits on.
    /// </remarks>
    public static void WarmInBackground() =>
        _ = Task.Run(() =>
        {
            try
            {
                // Lite first: it is the one a cold open blocks on (the scratch workspace behind
                // the prewarm lanes), and its assemblies are a strict subset of the full set, so
                // warming it first costs the full composition nothing it wasn't going to pay.
                _ = s_liteHostServices.Value;
                _ = s_hostServices.Value;
            }
            catch (Exception ex)
            {
                // Nothing awaits this. Left to the real caller to fail properly, with its own
                // error handling and its own message.
                Console.Error.WriteLine($"[HostComposition] MEF warm-up failed: {ex.Message}");
            }
        });
}
