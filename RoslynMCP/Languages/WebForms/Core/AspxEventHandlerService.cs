using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore;
using WebFormsCore.Nodes;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// Generating the code-behind method an event attribute names — the
/// <c>OnClick="BtnSave_Click"</c> half of WebForms that the designer used to write for you.
/// </summary>
internal static class AspxEventHandlerService
{
    /// <summary>
    /// The name Visual Studio would pick for a handler: control ID, underscore, event name,
    /// with a numeric suffix if that is already taken.
    /// </summary>
    public static string SuggestName(ControlNode control, IEventSymbol @event, INamedTypeSymbol? codeBehind)
    {
        string owner = control.Id
            ?? control.FieldName
            ?? control.ControlType.Name;

        string baseName = $"{owner}_{@event.Name}";
        if (codeBehind is null || codeBehind.GetDeep<IMethodSymbol>(baseName) is null)
            return baseName;

        for (int i = 1; ; i++)
        {
            string candidate = baseName + i;
            if (codeBehind.GetDeep<IMethodSymbol>(candidate) is null)
                return candidate;
        }
    }

    /// <summary>Methods already on the code-behind that could be wired to this event.</summary>
    public static IEnumerable<IMethodSymbol> CompatibleHandlers(
        INamedTypeSymbol? codeBehind, IEventSymbol @event)
    {
        if (codeBehind is null || InvokeMethod(@event) is not { } invoke)
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<IMethodSymbol>();

        for (var type = codeBehind; type is not null; type = type.BaseType)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary || !seen.Add(method.Name))
                    continue;
                if (IsCompatible(method, invoke))
                    results.Add(method);
            }

            // Framework base classes are full of members that happen to match; the handler the
            // user wants is one they wrote.
            if (type.BaseType is null || type.BaseType.Locations.All(l => !l.IsInSource))
                break;
        }

        return results;
    }

    private static bool IsCompatible(IMethodSymbol method, IMethodSymbol invoke)
    {
        if (method.Parameters.Length != invoke.Parameters.Length)
            return false;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var declared = invoke.Parameters[i].Type;
            var actual = method.Parameters[i].Type;
            // Contravariance: a handler may take a base type of what the delegate passes.
            if (!SymbolEqualityComparer.Default.Equals(declared, actual)
                && !declared.IsAssignableToSymbol(actual))
                return false;
        }

        return true;
    }

    /// <summary>The document the handler should be written into: the code-behind the user
    /// edits, never the generated designer half.</summary>
    public static Document? FindCodeBehindDocument(
        INamedTypeSymbol codeBehind, Project project, string aspxPath)
    {
        var candidates = codeBehind.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p => !p.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // `Default.aspx.cs` for `Default.aspx`, when the class is split across more than one file.
        string preferred = aspxPath + ".cs";
        string chosen = candidates.FirstOrDefault(
            p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];

        return WorkspaceService.FindDocumentInProject(project, chosen);
    }

    /// <summary>
    /// The edits that add <paramref name="handlerName"/> to the code-behind, as
    /// (file path, text changes). Returns an empty list when there is nowhere to write it.
    /// </summary>
    public static async Task<(string FilePath, IReadOnlyList<TextChange> Changes)?> GenerateAsync(
        AspxDocument document,
        IEventSymbol @event,
        string handlerName,
        CancellationToken ct)
    {
        if (document.CodeBehind is not { } codeBehind)
            return null;

        var target = FindCodeBehindDocument(codeBehind, document.Project, document.FilePath);
        if (target?.FilePath is not { Length: > 0 } targetPath)
            return null;

        // The method below is built out of C# syntax nodes, so it can only go into a C# file.
        if (target.Project.Language != LanguageNames.CSharp)
            return null;

        var root = await target.GetSyntaxRootAsync(ct);
        if (root is null)
            return null;

        var declaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.ValueText == codeBehind.Name);
        if (declaration is null)
            return null;

        var method = BuildMethod(@event, handlerName)
            .WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation);

        var updated = target.WithSyntaxRoot(
            root.ReplaceNode(declaration, declaration.AddMembers(method)));

        updated = await Simplifier.ReduceAsync(updated, Simplifier.Annotation, cancellationToken: ct);
        updated = await Formatter.FormatAsync(updated, Formatter.Annotation, cancellationToken: ct);

        var changes = await updated.GetTextChangesAsync(target, ct);
        return (targetPath, changes.ToList());
    }

    private static MethodDeclarationSyntax BuildMethod(IEventSymbol @event, string name)
    {
        var invoke = InvokeMethod(@event);

        var parameters = invoke is null
            ? [Parameter("object", "sender"), Parameter("System.EventArgs", "e")]
            : invoke.Parameters
                .Select(p => Parameter(
                    p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), p.Name))
                .ToArray();

        bool isAsync = invoke is not null && IsAwaitable(invoke.ReturnType);

        var modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword));
        if (isAsync)
            modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        // An async handler still declares the delegate's return type; `async void` would swallow
        // the exceptions the page's async pipeline is there to observe.
        var returnType = isAsync && invoke is not null
            ? SyntaxFactory.ParseTypeName(invoke.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        return SyntaxFactory.MethodDeclaration(returnType, SyntaxFactory.Identifier(name))
            .WithModifiers(modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block());
    }

    private static ParameterSyntax Parameter(string type, string name) =>
        SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
            .WithType(SyntaxFactory.ParseTypeName(type));

    private static IMethodSymbol? InvokeMethod(IEventSymbol @event) =>
        @event.Type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke } ? invoke : null;

    private static bool IsAwaitable(ITypeSymbol type) =>
        type.Name is "Task" or "ValueTask";

    /// <summary>Assignability that also accepts a base class or implemented interface, which is
    /// what makes a handler taking <c>EventArgs</c> valid for a <c>DataGridEventArgs</c> event.</summary>
    private static bool IsAssignableToSymbol(this ITypeSymbol source, ITypeSymbol target)
    {
        for (var t = source; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, target))
                return true;
        }

        return source.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
    }
}
