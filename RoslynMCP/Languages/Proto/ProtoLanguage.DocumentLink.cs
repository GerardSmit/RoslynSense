using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// textDocument/documentLink for a <c>.proto</c>: every <c>import</c> becomes Ctrl-clickable.
/// </summary>
/// <remarks>
/// <para>
/// The import path is the only thing in the file that names another file, and it is also the one
/// thing in it a reader cannot follow by eye. It is written relative to the proto root Grpc.Tools
/// hands protoc rather than to the directory the file sits in, so <c>import "common/types.proto"</c>
/// in <c>widgets/widgets.proto</c> means the sibling folder and not <c>widgets/common</c>. A link
/// turns that convention from something to be learned into something to be clicked.
/// </para>
/// <para>
/// A path that resolves to nothing produces no link rather than a broken one. Whatever the reason —
/// the file was never added, the proto root is not what this can see, or Grpc.Tools is not restored
/// so its own <c>google/protobuf</c> copies are nowhere on the machine — an underline that fails on
/// the click says less than no underline does, and the diagnostics provider is where "this import
/// does not resolve" belongs.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage : ILanguageDocumentLinkProvider
{
    public async Task<DocumentLink[]> DocumentLinksAsync(DocumentLinkParams p, CancellationToken ct)
    {
        var document = await ProtoDocumentService.GetAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return [];

        var file = document.Parse;
        var links = new List<DocumentLink>(file.Imports.Length);

        foreach (var import in file.Imports)
        {
            ct.ThrowIfCancellationRequested();

            // The project's directory, not the file's: it is the proto root for everything inside
            // it, and passing it is the difference between resolving a `.proto` linked in from
            // outside the project and resolving none of that file's imports at all.
            if (ProtoImportResolver.Resolve(import.Path, file.FilePath, document.ProjectDirectory)
                is not { } target)
            {
                continue;
            }

            // The path span carries its quotes, which is what should light up: the click target a
            // user aims at is the string they see, and an underline stopping inside the quotes
            // reads as a rendering mistake.
            links.Add(new DocumentLink(
                LspConverters.ToRange(file.Text.Lines, import.PathSpan),
                LspConverters.PathToUri(target),
                target));
        }

        return [.. links];
    }
}
