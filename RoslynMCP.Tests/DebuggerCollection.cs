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
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DebuggerCollection
{
    public const string Name = "debugger";
}
