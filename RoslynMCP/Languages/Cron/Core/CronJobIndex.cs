using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Lsp;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Every scheduled-job registration in a project, computed once per <see cref="Compilation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The scan walks every invocation in every syntax tree of the project, which is exactly the cost
/// that must not be paid twice. A compilation is immutable, so the answer cannot change under it: a
/// keystroke produces a new one whose index is built on first ask, and the old one falls out with
/// its compilation — the same bargain <see cref="Mediator.Core.MediatorHandlerIndex"/> makes.
/// </para>
/// <para>
/// Syntax before symbols, throughout. A registration is an invocation whose simple name is one of a
/// handful, and the name is readable without binding anything — so the semantic model is asked
/// about the few dozen calls that could be registrations rather than the few thousand that are not.
/// </para>
/// </remarks>
internal sealed class CronJobIndex(CronSettings settings)
{
    /// <summary>
    /// One table per index, and one index per pack — so the settings are not part of the key.
    /// </summary>
    /// <remarks>
    /// They were, briefly, and it was worse than useless. <see cref="CronSettings"/> is a record
    /// holding <see cref="ImmutableArray{T}"/> fields, and an immutable array compares by the
    /// identity of the array underneath it rather than by its contents — so two structurally
    /// identical settings are unequal, and a settings-keyed cache would have missed on every call
    /// the moment anything held a second instance. Owning the table instead makes the question not
    /// arise: an index can only ever be asked under the settings it was built with.
    /// </remarks>
    private readonly ConditionalWeakTable<Compilation, IReadOnlyList<CronJob>> _cache = new();

    /// <summary>
    /// The jobs of a compilation, built once.
    /// </summary>
    /// <remarks>
    /// A compilation belongs to exactly one project, so <paramref name="projectPath"/> is a fact
    /// about the key rather than part of it.
    /// </remarks>
    public IReadOnlyList<CronJob> Of(
        Compilation compilation, string projectPath, CancellationToken ct)
    {
        if (_cache.TryGetValue(compilation, out var cached))
            return cached;

        return _cache.GetValue(compilation, c => Build(c, projectPath, settings, ct));
    }

    private static IReadOnlyList<CronJob> Build(
        Compilation compilation, string projectPath, CronSettings settings, CancellationToken ct)
    {
        var found = ImmutableArray.CreateBuilder<CronJob>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();

            if (tree.FilePath is not { Length: > 0 } filePath)
                continue;

            var candidates = tree.GetRoot(ct)
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(Could)
                .ToList();

            if (candidates.Count == 0)
                continue;

            // Only now, and only for a file that holds a candidate: asking for a semantic model
            // binds the tree, and most files in a project hold no registration at all.
            var model = compilation.GetSemanticModel(tree);
            var text = tree.GetText(ct);

            foreach (var call in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (Read(call, model, text, projectPath, filePath, settings, ct) is { } job)
                    found.Add(job);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The syntax gate: is this call named like a registration at all.</summary>
    private static bool Could(InvocationExpressionSyntax invocation) =>
        CronPresets.SchedulingMethods.Contains(Called(invocation));

    private static string Called(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>One call, read as a job — or not read at all, which is the usual answer.</summary>
    private static CronJob? Read(
        InvocationExpressionSyntax call,
        SemanticModel model,
        Microsoft.CodeAnalysis.Text.SourceText text,
        string projectPath,
        string filePath,
        CronSettings settings,
        CancellationToken ct)
    {
        if (model.GetSymbolInfo(call, ct).Symbol is not IMethodSymbol method)
            return null;

        var declared = method.ReducedFrom ?? method;
        if (Match(declared, settings) is not { } binding)
            return null;

        var arguments = Arguments(call, method, declared);

        var (methodFacet, target) = binding.MethodIndex is { } methodIndex
            ? CronFacts.Method(At(arguments, methodIndex), model, ct)
            : CronFacts.Method(At(arguments, MethodPosition(declared, settings)), model, ct);

        // Quartz names a class through a type argument rather than passing one, so a registration
        // with no method argument still has a job to name.
        if (methodFacet.Origin == CronOrigin.Absent)
            (methodFacet, target) = CronFacts.TypeArgument(method, ct);

        var cron = binding.Kind == CronRegistrationKind.Remove
            ? CronFacet.Absent
            : CronFacts.Read(At(arguments, CronPosition(binding, declared, settings)), model, ct);

        var id = CronFacts.Read(At(arguments, binding.IdIndex ?? IdPosition(declared)), model, ct);

        var (targetRange, targetUri) = Declaration(target, ct);

        return new CronJob(
            JobId: id,
            Cron: cron,
            Method: methodFacet,
            Library: binding.Library,
            Kind: binding.Kind,
            Dialect: binding.Library == CronLibrary.Unknown
                ? CronTypes.For(model.Compilation).Dialect ?? binding.Dialect
                : binding.Dialect,
            ProjectPath: projectPath,
            FilePath: filePath,
            Offset: call.SpanStart,
            Registration: LspConverters.ToRange(text.Lines, call.Span),
            Target: targetRange,
            TargetUri: targetUri);
    }

    /// <summary>The binding this call satisfies, if any does.</summary>
    private static CronBinding? Match(IMethodSymbol declared, CronSettings settings)
    {
        foreach (var binding in settings.Bindings)
        {
            if (!binding.MemberName.Equals(declared.Name, StringComparison.Ordinal))
                continue;

            if (binding.ContainingType is { } type
                && !MemberSignature.DeclaredBy(declared.ContainingType, type))
            {
                continue;
            }

            if (binding.ParameterTypes is { } expected && !MemberSignature.Matches(declared, expected))
                continue;

            return binding;
        }

        return null;
    }

    /// <summary>
    /// The call's arguments in the declaration's parameter order, so an index means the same thing
    /// whether or not the call used named arguments.
    /// </summary>
    private static ExpressionSyntax?[] Arguments(
        InvocationExpressionSyntax call, IMethodSymbol method, IMethodSymbol declared)
    {
        var parameters = declared.Parameters;
        var slots = new ExpressionSyntax?[parameters.Length];

        // An extension method called as one writes no argument for its receiver, while the
        // declaration ReducedFrom put back still has it as parameter zero. Keyed on the reduction
        // rather than on the syntax, because the same method called statically writes it.
        int position = method.ReducedFrom is not null ? 1 : 0;
        foreach (var argument in call.ArgumentList.Arguments)
        {
            int slot;
            if (argument.NameColon?.Name.Identifier.ValueText is { } name)
            {
                slot = parameters.IndexOf(parameters.FirstOrDefault(p => p.Name == name));
                if (slot < 0)
                    continue;
            }
            else
            {
                slot = position++;
            }

            if (slot >= 0 && slot < slots.Length)
                slots[slot] = argument.Expression;
        }

        return slots;
    }

    private static ExpressionSyntax? At(ExpressionSyntax?[] arguments, int? index) =>
        index is { } position && position >= 0 && position < arguments.Length
            ? arguments[position]
            : null;

    /// <summary>
    /// Which argument carries the schedule: the binding's position, or the parameter whose name
    /// says so — the rule that stands in for every Hangfire overload at once.
    /// </summary>
    private static int? CronPosition(
        CronBinding binding, IMethodSymbol declared, CronSettings settings)
    {
        if (binding.CronIndex is { } position)
            return position;

        for (int i = 0; i < declared.Parameters.Length; i++)
        {
            if (settings.ParameterNames.Contains(
                declared.Parameters[i].Name, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The first parameter that takes a delegate or an expression tree.</summary>
    private static int? MethodPosition(IMethodSymbol declared, CronSettings settings)
    {
        _ = settings;

        for (int i = 0; i < declared.Parameters.Length; i++)
        {
            var type = declared.Parameters[i].Type;
            if (type.TypeKind == TypeKind.Delegate
                || type.Name is "Expression" or "Action" or "Func")
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The first string parameter that is not the schedule — the job's own name.</summary>
    private static int? IdPosition(IMethodSymbol declared)
    {
        for (int i = 0; i < declared.Parameters.Length; i++)
        {
            var parameter = declared.Parameters[i];
            if (MemberSignature.Named(parameter.Type, "string")
                && !CronPresets.ParameterNames.Contains(
                    parameter.Name, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Where a symbol is written, when it is written in source at all.</summary>
    private static (Lsp.Protocol.Range? Range, string? Uri) Declaration(
        ISymbol? symbol, CancellationToken ct)
    {
        if (symbol?.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference)
            return (null, null);

        var tree = reference.SyntaxTree;
        if (tree.FilePath is not { Length: > 0 } path)
            return (null, null);

        // The name rather than the whole declaration: a method body can be a screenful, and a
        // selection covering all of it reads as a mistake rather than as a destination.
        var node = reference.GetSyntax(ct);
        var span = node switch
        {
            MethodDeclarationSyntax method => method.Identifier.Span,
            TypeDeclarationSyntax type => type.Identifier.Span,
            _ => node.Span,
        };

        return (LspConverters.ToRange(tree.GetText(ct).Lines, span), LspConverters.PathToUri(path));
    }
}
