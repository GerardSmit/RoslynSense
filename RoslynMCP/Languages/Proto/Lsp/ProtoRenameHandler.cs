using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Rename.ConflictEngine;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Lsp;

/// <summary>
/// Renames a schema declaration and its handwritten C# consumers. Generated files participate
/// in Roslyn's temporary solution so binding and conflict resolution work, but are never emitted
/// as edits: protoc regenerates them from the renamed schema on the next build.
/// </summary>
internal static class ProtoRenameHandler
{
    public static async Task<PrepareRenameResult?> PrepareRenameAsync(TextDocumentPositionParams p, CancellationToken ct)
    {
        var target = await ResolveAsync(p.TextDocument, p.Position, ct);
        return target is null ? null : new PrepareRenameResult(
            LspConverters.ToRange(target.View.Text.Lines, target.Span), target.Declaration.Name.Value);
    }

    private sealed record Target(ProtoProjectView View, ProtoDeclaration Declaration, string Path, TextSpan Span);

    private static async Task<Target?> ResolveAsync(TextDocumentIdentifier document, Position position, CancellationToken ct)
    {
        var view = await ProtoWorkspace.GetAsync(LspConverters.UriToPath(document.Uri), ct);
        if (view?.Project is null)
            return null;
        int offset = LspConverters.ToOffset(view.Text, position);
        var hit = ProtoSymbolResolver.ResolveAt(view, offset);
        if (hit?.Target is not { } declaration || declaration is ProtoExtend || hit.WellKnown is not null)
            return null;
        var span = hit.Span;
        if (hit.IsReference)
        {
            // A qualified type reference selects its final identifier, not its enclosing type.
            int start = span.End - declaration.Name.Value.Length;
            if (offset < start || offset > span.End)
                return null;
            span = TextSpan.FromBounds(start, span.End);
        }
        return new(view, declaration, hit.TargetFile?.FilePath ?? view.FilePath, span);
    }

    public static async Task<WorkspaceEdit?> RenameAsync(RenameParams p, CancellationToken ct)
    {
        if (!Identifier(p.NewName))
            throw new ArgumentException("A proto name must be an ASCII identifier.");
        var original = await ResolveAsync(p.TextDocument, p.Position, ct);
        if (original is null)
            return null;
        var required = await RenameHandler.LoadRenameHierarchyAsync(original.View.Project!, ct);
        var target = await ResolveAsync(p.TextDocument, p.Position, ct);
        if (target is null || !original.View.Text.ContentEquals(target.View.Text)
            || original.Declaration.FullName != target.Declaration.FullName
            || !ProtoDocumentService.PathsEqual(original.Path, target.Path))
            return null;
        var owner = await ProtoWorkspace.GetAsync(target.Path, ct);
        if (owner?.Project?.FilePath is not { } projectPath)
            return null;
        var (_, project) = await WorkspaceService.GetOrOpenProjectAsync(projectPath, cancellationToken: ct);
        var solution = project.Solution;
        var loaded = solution.Projects.Select(x => x.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (required.Any(path => !loaded.Contains(path)))
            return null;

        var declaration = owner.Parse.FindByFullName(target.Declaration.FullName);
        if (declaration is null || declaration.Kind != target.Declaration.Kind)
            return null;
        var newText = owner.Text.WithChanges(new TextChange(declaration.Name.Span, p.NewName));
        var updatedSchema = ProtoParser.Parse(owner.FilePath, newText);
        if (updatedSchema.Diagnostics.Any(d => d.Severity == ProtoDiagnosticSeverity.Error)
            || updatedSchema.AllDeclarations.GroupBy(d => d.FullName).Any(g => g.Count() > 1))
            throw new ArgumentException("The new proto name conflicts with an existing declaration.");

        var schemaFiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            { [owner.FilePath] = owner.ProjectDirectory };
        var generated = new HashSet<DocumentId>();
        var actions = new List<(ProjectId Project, ISymbol Symbol, string Name)>();
        bool foundOwner = false;
        foreach (var candidate in solution.Projects.Where(x => x.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();
            var index = await ProtoGeneratedIndex.GetAsync(candidate, ct);
            foreach (var file in await ProtoWorkspace.ProtoFilesAsync(candidate, ct))
                schemaFiles.TryAdd(file, Path.GetDirectoryName(candidate.FilePath));
            foreach (var file in index.ProtoFiles)
                schemaFiles.TryAdd(file, Path.GetDirectoryName(candidate.FilePath));
            foreach (var document in candidate.Documents)
                if (index.IsGenerated(document)) generated.Add(document.Id);
            if (index.DocumentsFor(owner.FilePath).IsDefaultOrEmpty)
                continue;
            foundOwner = true;
            // Compare every declaration: renaming a message can change a colliding field name,
            // and renaming an enum can change how the generator strips its value-name prefixes.
            AddActions(candidate.Id, index, owner.Parse, updatedSchema, actions);
        }
        if (!foundOwner)
            throw new InvalidOperationException("Build the protobuf project before renaming its C# consumers.");

        var schemaEdits = SchemaEdits(schemaFiles, owner.Parse, declaration, p.NewName);
        var renamed = solution;
        // Track declarations through successive renames; SymbolKeys become stale when a
        // containing type changes. Annotations also distinguish RPC overloads precisely.
        var ordered = actions.DistinctBy(a => (a.Project, SymbolKey.Create(a.Symbol, ct).ToString()))
            .OrderBy(a => a.Symbol is INamedTypeSymbol ? 1
                : a.Symbol.ContainingType?.Name == a.Name ? 2 : 0)
            .ThenByDescending(a => Depth(a.Symbol)).ToArray();
        var tracked = new List<(DocumentId Document, SyntaxAnnotation Annotation, string Name)>();
        var compilations = new Dictionary<ProjectId, Compilation>();
        var annotations = new Dictionary<DocumentId, Dictionary<SyntaxNode, SyntaxAnnotation>>();
        foreach (var action in ordered)
        {
            if (!compilations.TryGetValue(action.Project, out var compilation))
                compilations[action.Project] = compilation = (await solution.GetProject(action.Project)!.GetCompilationAsync(ct))!;
            var symbol = SymbolKey.Create(action.Symbol, ct).Resolve(compilation, cancellationToken: ct).Symbol;
            var reference = symbol?.DeclaringSyntaxReferences.FirstOrDefault();
            if (reference is null || solution.GetDocument(reference.SyntaxTree) is not { } document)
                throw new InvalidOperationException("A generated declaration is unavailable; rebuild before rename.");
            var node = await reference.GetSyntaxAsync(ct);
            var annotation = new SyntaxAnnotation();
            if (!annotations.TryGetValue(document.Id, out var nodes))
                annotations[document.Id] = nodes = [];
            nodes.Add(node, annotation);
            tracked.Add((document.Id, annotation, action.Name));
        }
        foreach (var (id, nodes) in annotations)
        {
            var root = (await solution.GetDocument(id)!.GetSyntaxRootAsync(ct))!;
            renamed = renamed.WithDocumentSyntaxRoot(id,
                root.ReplaceNodes(nodes.Keys, (originalNode, rewritten) => rewritten.WithAdditionalAnnotations(nodes[originalNode])));
        }
        foreach (var action in tracked)
        {
            ct.ThrowIfCancellationRequested();
            var document = renamed.GetDocument(action.Document)!;
            var root = (await document.GetSyntaxRootAsync(ct))!;
            var node = root.GetAnnotatedNodes(action.Annotation).SingleOrDefault();
            var model = await document.GetSemanticModelAsync(ct);
            var symbol = node is null ? null : model!.GetDeclaredSymbol(node, ct);
            if (symbol is null)
                throw new InvalidOperationException("A generated symbol changed while preparing rename.");
            if (symbol.Name == action.Name) continue; // A cascading rename already reached it.
            var resolution = await Renamer.RenameSymbolAsync(renamed, symbol,
                action.Name, new SymbolRenameOptions(), ct);
            if (!resolution.IsSuccessful || !resolution.ReplacementTextValid
                || resolution.RelatedLocations.Any(location =>
                    (location.Type & RelatedLocationType.UnresolvedConflict) != 0))
                throw new InvalidOperationException("The proto rename conflicts with an existing C# symbol.");
            renamed = resolution.NewSolution!;
        }

        var changes = new Dictionary<string, TextEdit[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in renamed.GetChanges(solution).GetProjectChanges())
        foreach (var id in change.GetChangedDocuments())
        {
            if (generated.Contains(id)) continue;
            var before = solution.GetDocument(id)!;
            if (before.FilePath is not { } path) continue;
            var text = await before.GetTextAsync(ct);
            if (OpenDocumentStore.TryGet(path, out var open) && !open.ContentEquals(text)) return null;
            var edits = await renamed.GetDocument(id)!.GetTextChangesAsync(before, ct);
            var mapped = edits.Select(edit =>
                new TextEdit(LspConverters.ToRange(text.Lines, edit.Span), edit.NewText ?? "")).ToArray();
            string uri = LspConverters.PathToUri(path);
            if (changes.TryGetValue(uri, out var previous) && !previous.SequenceEqual(mapped))
                throw new InvalidOperationException("Linked C# documents require incompatible rename edits.");
            changes[uri] = mapped;
        }
        foreach (var (file, edits) in schemaEdits)
        {
            var current = ProtoDocumentService.GetParse(file.FilePath);
            if (current is null || !current.Text.ContentEquals(file.Text)) return null;
            if (edits.Count == 0) continue;
            changes[LspConverters.PathToUri(file.FilePath)] = edits.Select(edit =>
                new TextEdit(LspConverters.ToRange(file.Text.Lines, edit.Span), edit.NewText ?? "")).ToArray();
        }
        return new WorkspaceEdit(changes);
    }

    private static bool Identifier(string name) => name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static int Depth(ISymbol symbol)
    {
        int depth = 0;
        for (var type = symbol.ContainingType; type is not null; type = type.ContainingType) depth++;
        return depth;
    }

    private static void AddActions(ProjectId projectId, ProtoGeneratedIndex index, ProtoFile before, ProtoFile after,
        List<(ProjectId Project, ISymbol Symbol, string Name)> actions)
    {
        if (before.AllDeclarations.Length != after.AllDeclarations.Length)
            throw new ArgumentException("Rename changed the schema structure.");
        for (int i = 0; i < before.AllDeclarations.Length; i++)
        {
            var old = before.AllDeclarations[i];
            var next = after.AllDeclarations[i];
            void Add(ISymbol? symbol, string expected, string replacement, bool required = true)
            {
                if (expected == replacement) return;
                if (symbol is null)
                {
                    if (required) throw new InvalidOperationException("Generated protobuf code is missing; build before rename.");
                    return;
                }
                if (symbol.Name != expected)
                    throw new InvalidOperationException("Generated protobuf names do not match this schema; rebuild before rename.");
                actions.Add((projectId, symbol, replacement));
            }
            void Members(INamedTypeSymbol? type, string from, string to)
            {
                if (type is null || from == to) return;
                foreach (var member in type.GetMembers(from)) Add(member, from, to);
            }
            switch (old, next)
            {
                case (ProtoMessage a, ProtoMessage b):
                    Add(index.TypeFor(a), a.Name.Value, b.Name.Value);
                    break;
                case (ProtoEnum a, ProtoEnum b):
                    Add(index.TypeFor(a), a.Name.Value, b.Name.Value);
                    break;
                case (ProtoEnumValue a, ProtoEnumValue b):
                    Add(index.MemberFor(a), ProtoNaming.EnumMemberName(a), ProtoNaming.EnumMemberName(b));
                    break;
                case (ProtoField a, ProtoField b):
                    var property = index.PropertyFor(a);
                    Add(property, ProtoNaming.PropertyName(a), ProtoNaming.PropertyName(b));
                    var type = property?.ContainingType;
                    Members(type, ProtoNaming.FieldNumberConstName(a), ProtoNaming.FieldNumberConstName(b));
                    Members(type, ProtoNaming.HasPropertyName(a), ProtoNaming.HasPropertyName(b));
                    Members(type, ProtoNaming.ClearMethodName(a), ProtoNaming.ClearMethodName(b));
                    if (a.Oneof is { } oneof)
                        Members(type?.GetTypeMembers(ProtoNaming.OneofCaseEnumName(oneof)).SingleOrDefault(),
                            ProtoNaming.OneofCaseName(a), ProtoNaming.OneofCaseName(b));
                    break;
                case (ProtoOneof a, ProtoOneof b):
                    var owner = a.Parent is ProtoMessage message ? index.TypeFor(message) : null;
                    Members(owner, ProtoNaming.OneofCaseEnumName(a), ProtoNaming.OneofCaseEnumName(b));
                    Members(owner, ProtoNaming.OneofCasePropertyName(a), ProtoNaming.OneofCasePropertyName(b));
                    Members(owner, ProtoNaming.ClearMethodName(a), ProtoNaming.ClearMethodName(b));
                    break;
                case (ProtoService a, ProtoService b):
                    Add(index.ServiceTypeFor(a), ProtoNaming.ServiceClassName(a), ProtoNaming.ServiceClassName(b));
                    Add(index.ServiceBaseFor(a), ProtoNaming.ServiceBaseName(a), ProtoNaming.ServiceBaseName(b), false);
                    Add(index.ServiceClientFor(a), ProtoNaming.ServiceClientName(a), ProtoNaming.ServiceClientName(b), false);
                    break;
                case (ProtoRpc a, ProtoRpc b) when a.Name.Value != b.Name.Value:
                    var methods = index.MethodsFor(a);
                    if (methods.IsDefaultOrEmpty)
                        throw new InvalidOperationException("Generate the gRPC service code before renaming an RPC.");
                    foreach (var method in methods)
                    {
                        string suffix = method.Name == a.Name.Value ? "" : "Async";
                        Add(method, a.Name.Value + suffix, b.Name.Value + suffix);
                    }
                    break;
            }
        }
    }

    private static List<(ProtoFile File, List<TextChange> Edits)> SchemaEdits(
        IReadOnlyDictionary<string, string?> paths, ProtoFile owner, ProtoDeclaration target, string newName)
    {
        var result = new List<(ProtoFile, List<TextChange>)>();
        string newFullName = target.FullName[..^target.Name.Value.Length] + newName;
        var allPaths = new Dictionary<string, string?>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, root) in paths)
        {
            if (ProtoDocumentService.GetParse(path) is not { } file) continue;
            foreach (var visible in ProtoScope.Create(file, root).VisibleFiles)
                allPaths.TryAdd(visible.FilePath, root);
        }
        foreach (var (path, root) in allPaths)
        {
            var file = ProtoDocumentService.GetParse(path);
            if (file is null) throw new IOException("Cannot read protobuf schema: " + path);
            var edits = new List<TextChange>();
            if (ProtoDocumentService.PathsEqual(path, owner.FilePath))
                edits.Add(new TextChange(target.Name.Span, newName));
            var scope = ProtoScope.Create(file, root);
            if (target is ProtoMessage or ProtoEnum)
            foreach (var reference in file.TypeReferences)
            {
                var resolution = scope.Resolve(reference);
                if (resolution?.File is not { } resolvedFile
                    || !ProtoDocumentService.PathsEqual(resolvedFile.FilePath, owner.FilePath)) continue;
                if (resolution.FullName == target.FullName
                    || resolution.FullName.StartsWith(target.FullName + ".", StringComparison.Ordinal))
                    edits.Add(new TextChange(reference.Span, "." + newFullName + resolution.FullName[target.FullName.Length..]));
            }
            if (target is ProtoEnumValue value)
            foreach (var field in file.AllDeclarations.OfType<ProtoField>())
            {
                var resolved = scope.Resolve(field.Type, field);
                if (resolved is null || resolved.Declaration?.FullName != value.Parent?.FullName
                    || resolved.File is null || !ProtoDocumentService.PathsEqual(resolved.File.FilePath, owner.FilePath)) continue;
                foreach (var option in field.Options.Where(o => o.Name == "default" && o.Value == value.Name.Value))
                    edits.Add(new TextChange(option.ValueSpan, newName));
            }
            result.Add((file, edits));
        }
        return result;
    }
}
