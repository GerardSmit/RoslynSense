using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Serializes the debugger tests.
/// </summary>
/// <remarks>
/// Debugging is process-global: the desktop CLR debugging interface is obtained once per process
/// and <see cref="RoslynMCP.Services.DebugSessionManager"/> holds a single session, so two test
/// classes attaching concurrently interfere with each other. xUnit runs collections in parallel
/// but the classes within one collection sequentially, which is exactly what is needed here.
/// </remarks>
/// <remarks>
/// The name now resolves to <see cref="SharedState"/>'s collection rather than one of its own.
/// Two separately-defined collections run in parallel with each other, and the debugger tests are
/// not independent of the workspace ones — they load projects and open workspaces — so a private
/// "debugger" collection would have serialized them against themselves while still racing the
/// rest. Existing <c>[Collection(DebuggerCollection.Name)]</c> attributes keep working.
/// </remarks>
public sealed class DebuggerCollection
{
    public const string Name = SharedState.Name;
}
