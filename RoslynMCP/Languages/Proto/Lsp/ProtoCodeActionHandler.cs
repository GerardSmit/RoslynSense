using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto.Lsp;

/// <summary>
/// The quick fixes a <c>.proto</c> needs: writing the <c>import</c> that would make an unresolved
/// name resolve, and building the project when protoc has never run over the file.
/// </summary>
/// <remarks>
/// <para>
/// Both actions are complete as listed, where the ASPX ones defer their expensive half to a
/// command. Nothing here is expensive: an import is one line of text at an offset the parse already
/// knows, and the build is a command the client hands straight back rather than an edit at all — so
/// there is nothing left for <c>codeAction/resolve</c> to do.
/// </para>
/// <para>
/// The request's own range is not the only place a fix is looked for. A client passes the
/// diagnostics it is showing in <see cref="CodeActionContext"/>, and the lightbulb it draws sits on
/// the line rather than on the token; resolving at each of those ranges as well is what makes the
/// import reachable from anywhere on the line the squiggle is under.
/// </para>
/// </remarks>
internal static class ProtoCodeActionHandler
{
    public static async Task<CodeAction[]> CodeActionsAsync(CodeActionParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        if (await ProtoWorkspace.GetAsync(path, ct) is not { } view)
            return [];

        var actions = new List<CodeAction>();
        var titles = new HashSet<string>(StringComparer.Ordinal);

        var scope = view.CreateScope();

        foreach (int offset in Offsets(view.Text, p))
        {
            ct.ThrowIfCancellationRequested();

            var hit = ProtoSymbolResolver.ResolveAt(
                view.Parse, offset, scope, view.Index, view.ProjectDirectory);

            if (hit is null)
                continue;

            foreach (var action in await ImportActionsAsync(view, hit, ct))
            {
                if (titles.Add(action.Title))
                    actions.Add(action);
            }
        }

        if (BuildAction(view) is { } build && titles.Add(build.Title))
            actions.Add(build);

        return [.. actions];
    }

    /// <summary>
    /// The offsets worth resolving: where the request says the caret is, and where each diagnostic
    /// the client is showing begins.
    /// </summary>
    /// <remarks>
    /// Clamped rather than converted through <see cref="LspConverters.ToOffset"/>, because a
    /// diagnostic in the context is one the client last received and the buffer may have shrunk
    /// since — a position past the end of the text would throw out of a request whose other fixes
    /// are still valid.
    /// </remarks>
    private static IEnumerable<int> Offsets(SourceText text, CodeActionParams p)
    {
        var seen = new HashSet<int>();

        foreach (var position in Positions(p))
        {
            if (Offset(text, position) is { } offset && seen.Add(offset))
                yield return offset;
        }
    }

    private static IEnumerable<Position> Positions(CodeActionParams p)
    {
        yield return p.Range.Start;

        foreach (var diagnostic in p.Context.Diagnostics)
            yield return diagnostic.Range.Start;
    }

    private static int? Offset(SourceText text, Position position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count)
            return null;

        var line = text.Lines[position.Line];
        return Math.Clamp(line.Start + Math.Max(position.Character, 0), line.Start, line.End);
    }

    // ---- Adding the missing import -----------------------------------------------------------

    /// <summary>
    /// The caret is on a name that resolves to nothing: offer the <c>import</c> for every
    /// <c>.proto</c> in the project that declares something the name could have meant.
    /// </summary>
    /// <remarks>
    /// One action per file rather than one per candidate name, because the file is what the fix
    /// writes and two names in one file would produce two actions with the same edit. protoc's own
    /// <c>google/protobuf</c> protos are offered first: they belong to no project, so nothing in
    /// the project's file set would ever name them, and forgetting
    /// <c>import "google/protobuf/timestamp.proto"</c> is the single most common way to arrive
    /// here.
    /// </remarks>
    private static async Task<IReadOnlyList<CodeAction>> ImportActionsAsync(
        ProtoProjectView view, ProtoHit hit, CancellationToken ct)
    {
        if (hit.Kind is not (ProtoHitKind.FieldType or ProtoHitKind.RpcRequestType
            or ProtoHitKind.RpcResponseType or ProtoHitKind.ExtendTarget))
            return [];

        if (hit is not { TypeRef: { IsScalar: false } reference, ResolvedProtoTarget: null, WellKnown: null })
            return [];

        var candidates = Candidates(reference.Text, ProtoScope.ScopeOf(hit.Declaration, view.Parse));
        if (candidates.Count == 0)
            return [];

        var actions = new List<CodeAction>();
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wellKnown in ProtoWellKnownTypes.All)
        {
            if (candidates.Contains(wellKnown.FullName, StringComparer.Ordinal)
                && offered.Add(wellKnown.ProtoPath)
                && !ProtoImportEdits.Imports(view.Parse, wellKnown.ProtoPath))
            {
                actions.Add(ImportAction(view, wellKnown.ProtoPath, wellKnown.FullName));
            }
        }

        if (view.Project is not { } project)
            return actions;

        foreach (string protoPath in await ProtoWorkspace.ProtoFilesAsync(project, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (ProtoDocumentService.PathsEqual(protoPath, view.FilePath)
                || ProtoDocumentService.GetParse(protoPath) is not { } declaring)
            {
                continue;
            }

            if (Declared(declaring, candidates) is not { } declared)
                continue;

            // The path as protoc knows the file, not as the file system does: an import statement
            // names a file relative to a proto root, and an absolute path in one compiles nowhere.
            if (ProtoImportResolver.ToProtoPath(protoPath, view.ProjectDirectory) is not { } importPath
                || !offered.Add(importPath)
                || ProtoImportEdits.Imports(view.Parse, importPath))
            {
                continue;
            }

            actions.Add(ImportAction(view, importPath, declared));
        }

        return actions;
    }

    /// <summary>The first candidate name this file declares as a message or an enum, or
    /// <c>null</c> when it declares none of them.</summary>
    private static string? Declared(ProtoFile file, List<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (file.FindByFullName(candidate) is ProtoMessage or ProtoEnum)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Every fully-qualified name the written reference could have meant, innermost scope first.
    /// </summary>
    /// <remarks>
    /// The same walk <see cref="ProtoScope.ResolveIn"/> makes, without the resolving: the file that
    /// would have answered has not been imported yet, so there is nothing in scope to look the name
    /// up against and the candidate names are the whole of what can be produced. A rooted name —
    /// one written with a leading dot — names exactly one thing and skips the walk, which is what
    /// it is for.
    /// </remarks>
    private static List<string> Candidates(string written, string scope)
    {
        string name = written.TrimStart('.');
        if (name.Length == 0)
            return [];

        if (written.StartsWith('.'))
            return [name];

        var candidates = new List<string>();

        for (string current = scope; ; )
        {
            candidates.Add(current.Length == 0 ? name : current + "." + name);

            if (current.Length == 0)
                return candidates;

            int dot = current.LastIndexOf('.');
            current = dot < 0 ? string.Empty : current[..dot];
        }
    }

    private static CodeAction ImportAction(ProtoProjectView view, string importPath, string fullName) =>
        new($"Import \"{importPath}\" for '{fullName}'",
            "quickfix",
            new WorkspaceEdit(new Dictionary<string, TextEdit[]>
            {
                [LspConverters.PathToUri(view.FilePath)] =
                    [ProtoImportEdits.Insert(view.Parse, importPath)],
            }));

    // ---- Building what has never been built --------------------------------------------------

    /// <summary>
    /// Nothing has generated C# from this file, so every declaration in it binds to no symbol:
    /// offer the build that would fix all of them at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action carries <see cref="ExecuteCommandHandler.BuildCommand"/> rather than an edit,
    /// which is the one command shape a code action can use without the client implementing
    /// anything: the client hands it back as <c>workspace/executeCommand</c> and the server runs
    /// the same build a debug launch does. It is advertised unconditionally in
    /// <see cref="ExecuteCommandHandler.Commands"/>, so this works whether or not the pack is the
    /// one that put it there.
    /// </para>
    /// <para>
    /// Only for a file some project provably owns. Where nothing claims the <c>.proto</c> the
    /// nearest <c>.csproj</c> on disk is a guess, and offering to build a project that was never
    /// going to compile the file would report a green build over a still-dark editor.
    /// </para>
    /// </remarks>
    private static CodeAction? BuildAction(ProtoProjectView view)
    {
        if (view.Projects.IsDefaultOrEmpty
            || view.Project?.FilePath is not { Length: > 0 } projectPath
            || !view.Index.DocumentsFor(view.FilePath).IsDefaultOrEmpty)
        {
            return null;
        }

        return new CodeAction(
            $"Build {Path.GetFileNameWithoutExtension(projectPath)} to generate the C# for this file",
            "quickfix",
            Edit: null)
        {
            Command = new Command("Build", ExecuteCommandHandler.BuildCommand, [projectPath]),
        };
    }

}

/// <summary>
/// Where an <c>import</c> goes in a file that does not have one for it yet.
/// </summary>
/// <remarks>
/// Shared with completion rather than owned by the code action, because completion offers
/// well-known types the file has not imported and has to carry the import along with the type name:
/// a completion item is resolved without a document, so an edit that is not already on the item can
/// never be added to it, and the two features writing the line to different places would make the
/// same fix look like two.
/// </remarks>
internal static class ProtoImportEdits
{
    public static bool Imports(ProtoFile file, string protoPath) =>
        file.Imports.Any(import =>
            string.Equals(import.Path, protoPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The edit that adds the import, or <c>null</c> when the file already has it.</summary>
    public static TextEdit? TryInsert(ProtoFile file, string protoPath) =>
        Imports(file, protoPath) ? null : Insert(file, protoPath);

    /// <summary>
    /// The edit that adds the import.
    /// </summary>
    /// <remarks>
    /// Alphabetical among the imports the file already has, which is the order protoc's style guide
    /// asks for and the order that keeps a diff to one line. A file with no imports yet gets one
    /// under its <c>syntax</c>/<c>package</c> header instead, standing on its own: it reuses the
    /// blank line the header already has where there is one, and writes the separators itself where
    /// there is not. Both sides, because a statement blank-separated from only one of its
    /// neighbours reads as belonging to the other — and for an import that is the declaration it
    /// would otherwise be sitting on top of.
    /// </remarks>
    public static TextEdit Insert(ProtoFile file, string protoPath)
    {
        var text = file.Text;
        string eol = LineEnding(text);
        string statement = $"import \"{protoPath}\";";

        if (file.Imports.Length > 0)
        {
            foreach (var existing in file.Imports)
            {
                if (string.CompareOrdinal(protoPath, existing.Path) < 0)
                    return AtLine(text, LineOf(text, existing.Span.Start), statement + eol);
            }

            return AtLine(text, LineOf(text, file.Imports[^1].Span.End) + 1, statement + eol);
        }

        int header = HeaderLine(file);

        if (header < 0)
            return AtLine(text, 0, statement + eol + eol);

        // Nothing follows the header, so there is nothing to be separated from.
        if (header + 1 >= text.Lines.Count)
            return AtLine(text, header + 1, eol + statement + eol);

        return IsBlank(text.Lines[header + 1])
            ? AtLine(text, header + 2, statement + eol + eol)
            : AtLine(text, header + 1, eol + statement + eol + eol);
    }

    /// <summary>The last line of the file's header — its <c>package</c> if it declares one, its
    /// <c>syntax</c> otherwise, and <c>-1</c> for a file that declares neither.</summary>
    private static int HeaderLine(ProtoFile file)
    {
        int header = -1;

        if (!file.SyntaxSpan.IsEmpty)
            header = LineOf(file.Text, file.SyntaxSpan.End);

        if (!file.PackageSpan.IsEmpty)
            header = Math.Max(header, LineOf(file.Text, file.PackageSpan.End));

        return header;
    }

    private static TextEdit AtLine(SourceText text, int line, string inserted)
    {
        int position = line >= text.Lines.Count
            ? text.Length
            : text.Lines[Math.Max(line, 0)].Start;

        return new TextEdit(
            LspConverters.ToRange(text.Lines, new TextSpan(position, 0)), inserted);
    }

    private static int LineOf(SourceText text, int offset) =>
        text.Lines.GetLinePosition(Math.Clamp(offset, 0, text.Length)).Line;

    private static bool IsBlank(TextLine line) => line.ToString().Trim().Length == 0;

    /// <summary>The line ending the file itself uses, so an inserted line does not mix them.</summary>
    private static string LineEnding(SourceText text)
    {
        if (text.Lines.Count == 0)
            return Environment.NewLine;

        var first = text.Lines[0];

        return first.EndIncludingLineBreak > first.End
            ? text.ToString(TextSpan.FromBounds(first.End, first.EndIncludingLineBreak))
            : Environment.NewLine;
    }
}
