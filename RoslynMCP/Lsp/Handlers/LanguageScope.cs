using RoslynMCP.Languages;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Which language packs a static handler answers for. Every handler that asks a pack something
/// outside the <c>Route</c> dispatch takes the calling connection's <see cref="LanguageSession"/>;
/// this is what happens when it was not given one.
/// </summary>
/// <remarks>
/// Not every caller is an editor. MCP tools reach <see cref="FileOperationsHandler"/>, the
/// watched-file debounce belongs to no connection, and a pack calls back into
/// <see cref="NavigationHandlers"/> for its own code lenses. None of those has an editor's
/// language settings, and none should have: <c>roslynSense.languages.*</c> narrows what one
/// window is answered, while a call from outside every window is governed by the registration
/// gate alone — <c>roslynsense.json</c> and <c>--no-*</c>, the same gate the MCP tool surface is
/// behind.
/// </remarks>
internal static class LanguageScope
{
    private static Memo? s_memo;

    /// <summary>
    /// <paramref name="languages"/> when the call came from a connection, otherwise
    /// <see cref="Process"/>.
    /// </summary>
    public static LanguageSession Of(LanguageSession? languages) => languages ?? Process;

    /// <summary>
    /// Every pack the process registered, as a session with all of them enabled.
    /// </summary>
    /// <remarks>
    /// Memoized on the registry's identity — fixed once a host has built its container — because
    /// constructing a session computes the semantic-token legend, and the paths that land here
    /// include per-keystroke ones.
    /// </remarks>
    public static LanguageSession Process
    {
        get
        {
            var registry = LanguageRegistry.Current;
            if (s_memo is { } memo && ReferenceEquals(memo.Registry, registry))
                return memo.Session;

            var session = new LanguageSession(registry.Packs);
            s_memo = new Memo(registry, session);
            return session;
        }
    }

    private sealed record Memo(LanguageRegistry Registry, LanguageSession Session);
}
