namespace RoslynMCP.Services;

/// <summary>A unit of long-running work the user should see feedback for.</summary>
public interface IProgressScope : IAsyncDisposable
{
    void Report(string message, int? percentage = null);
}

/// <summary>
/// Layer-safe progress reporting. Services announce work; whoever can render it installs a
/// factory. The LSP layer does that at session start (<c>$/progress</c>); with no editor
/// attached — an MCP-only process — everything falls through to a no-op, so callers never
/// need to know whether anyone is watching.
/// </summary>
public static class ProgressReporter
{
    private static readonly IProgressScope s_noop = new NoopScope();

    /// <summary>Installed by the LSP layer. Null means nothing can render progress.</summary>
    public static Func<string, CancellationToken, Task<IProgressScope>>? Factory { get; set; }

    public static async Task<IProgressScope> BeginAsync(string title, CancellationToken ct = default)
    {
        if (Factory is not { } factory)
            return s_noop;

        try { return await factory(title, ct); }
        catch { return s_noop; } // progress must never break the work it describes
    }

    private sealed class NoopScope : IProgressScope
    {
        public void Report(string message, int? percentage = null) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
