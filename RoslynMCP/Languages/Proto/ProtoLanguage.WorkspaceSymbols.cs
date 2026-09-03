using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.PatternMatching;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageWorkspaceSymbolProvider
{
    /// <summary>Matches the cap the C# half applies, for the same reason: the client renders a
    /// picker, not a report.</summary>
    private const int MaxWorkspaceSymbols = 200;

    /// <summary>
    /// The declarations a <c>.proto</c> makes, for Ctrl+T. None of them is in a compilation — the
    /// generated class beside it is, but that is <c>WidgetsReflection</c>-adjacent noise under
    /// <c>obj</c>, and someone looking for <c>message Widget</c> means the schema.
    /// </summary>
    /// <remarks>
    /// The file list per project is the one <see cref="ProtoWorkspace.ProtoFilesAsync"/> keeps warm.
    /// The parses behind it are memoized against each buffer's checksum, so a keystroke in the
    /// picker costs a read and a hash per file rather than a re-parse of the solution's schemas —
    /// and the read is what keeps an unsaved edit authoritative here the way it is everywhere else
    /// in the pack.
    /// </remarks>
    public async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(
        string query, Solution solution, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Roslyn's own matcher, so that "GetWid" and "gW" pick the same candidates in a .proto that
        // they pick in C# — a picker that ranked its halves by different rules would read as a bug.
        using var matcher = PatternMatcher.CreatePatternMatcher(query, includeMatchedSpans: false);

        var results = new List<SymbolInformation>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A multi-targeted project appears once per framework over the same directory, and
            // every one of them would contribute the same protos.
            if (project.FilePath is not { } path || !seenProjects.Add(path))
                continue;

            foreach (string file in await ProtoWorkspace.ProtoFilesAsync(project, ct))
            {
                ct.ThrowIfCancellationRequested();

                // The server and the client of one gRPC contract are separate projects compiling
                // the same .proto, so without this every declaration in it is listed twice.
                if (!seenFiles.Add(file))
                    continue;

                if (ProtoDocumentService.GetParse(file) is not { } parse)
                    continue;

                CollectWorkspaceSymbols(parse, matcher, results);

                if (results.Count >= MaxWorkspaceSymbols)
                    return results;
            }
        }

        return results;
    }

    /// <summary>
    /// What one file contributes: its messages, enums, services and rpcs.
    /// </summary>
    /// <remarks>
    /// Not fields and not enum values. Every schema names them the same way — <c>id</c>,
    /// <c>name</c>, <c>created_at</c>, <c>UNSPECIFIED</c> — so a query that matched one would match
    /// forty and push the message somebody was looking for off the end of the list. Those two are
    /// what the document outline is for, one file at a time.
    /// </remarks>
    private static void CollectWorkspaceSymbols(
        ProtoFile file, PatternMatcher matcher, List<SymbolInformation> results)
    {
        foreach (var declaration in file.AllDeclarations)
        {
            // A service is an Interface rather than a Class: it declares a contract that C# classes
            // implement and holds nothing itself, which is what the picker's icon should say.
            int kind = declaration.Kind switch
            {
                ProtoDeclarationKind.Message => LspSymbolKind.Class,
                ProtoDeclarationKind.Enum => LspSymbolKind.Enum,
                ProtoDeclarationKind.Service => LspSymbolKind.Interface,
                ProtoDeclarationKind.Rpc => LspSymbolKind.Method,
                _ => 0,
            };

            if (kind == 0 || !matcher.Matches(declaration.Name.Value))
                continue;

            results.Add(new SymbolInformation(
                declaration.Name.Value,
                kind,
                new LspLocation(
                    LspConverters.PathToUri(file.FilePath),
                    LspConverters.ToRange(file.Text.Lines, declaration.Name.Span)),
                ContainerName(declaration, file)));

            if (results.Count >= MaxWorkspaceSymbols)
                return;
        }
    }

    /// <summary>
    /// What the picker shows beside the name: the enclosing declaration's proto name, or the file's
    /// package for a top-level one, or the file name when it declares no package.
    /// </summary>
    /// <remarks>
    /// The proto name and not the C# namespace. Ctrl+T over a <c>.proto</c> is a question about the
    /// schema, and the namespace protoc derives depends on a <c>csharp_namespace</c> option that
    /// most files do not set — deriving it would mean reproducing a naming rule this pack goes out
    /// of its way never to predict.
    /// </remarks>
    private static string ContainerName(ProtoDeclaration declaration, ProtoFile file)
    {
        if (declaration.Parent is { } parent)
            return parent.FullName;

        return file.Package.Length > 0 ? file.Package : Path.GetFileName(file.FilePath);
    }
}
