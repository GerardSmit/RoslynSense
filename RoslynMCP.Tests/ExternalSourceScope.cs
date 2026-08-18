using RoslynMCP.Config;

namespace RoslynMCP.Tests;

/// <summary>
/// Turns off everything that would fetch a dependency's real source, for tests whose subject is
/// what happens when it cannot be had.
/// </summary>
/// <remarks>
/// Without this, such a test asserts one thing on a machine with a route to the symbol server and
/// another on a machine without one. Belongs to <see cref="SharedState"/>, since the flags are
/// process-wide.
/// </remarks>
internal sealed class ExternalSourceScope : IDisposable
{
    private readonly bool _external = LspFeatureOptions.ExternalSource;
    private readonly bool _sourceLink = LspFeatureOptions.SourceLink;

    private ExternalSourceScope()
    {
        LspFeatureOptions.ExternalSource = false;
        LspFeatureOptions.SourceLink = false;
    }

    /// <summary>Nothing but decompilation can answer, until the scope is disposed.</summary>
    public static ExternalSourceScope Offline() => new();

    public void Dispose()
    {
        LspFeatureOptions.ExternalSource = _external;
        LspFeatureOptions.SourceLink = _sourceLink;
    }
}
