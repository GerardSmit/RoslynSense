using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Serializes the tests that touch process-wide state.
/// </summary>
/// <remarks>
/// <para>
/// The suite used to run entirely single-threaded — one assembly-level
/// <c>DisableTestParallelization</c> — because a handful of tests share statics: the workspace
/// cache and its open-document overlay, the analyzer caches, the shadow-copy root, the debug
/// session, the hot reload agent registry, the on-disk stores, and the process environment. That
/// made every one of the other hundred-odd files pay for it, and a full run took twelve minutes.
/// </para>
/// <para>
/// One collection rather than one per family, deliberately. xUnit runs distinct collections in
/// parallel with each other, so splitting these into "workspace" and "debugger" would let two
/// groups race — and they are not independent: the debugger tests load projects, the hot reload
/// tests open workspaces, and the overlay tests feed the same cache. A single serialized group is
/// the only arrangement that is actually safe here.
/// </para>
/// <para>
/// Everything else runs in parallel. When adding a test, it belongs in this collection only if it
/// reads or writes state that outlives it.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedState
{
    public const string Name = "shared-state";
}
