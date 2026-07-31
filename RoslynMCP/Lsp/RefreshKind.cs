namespace RoslynMCP.Lsp;

/// <summary>Client-side data that a server-side change can invalidate.</summary>
[Flags]
internal enum RefreshKind
{
    None = 0,
    Diagnostics = 1,
    CodeLens = 2,
    InlayHint = 4,
    All = Diagnostics | CodeLens | InlayHint,
}
