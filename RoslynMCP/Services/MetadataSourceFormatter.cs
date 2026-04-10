using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

internal static class MetadataSourceFormatter
{
    private const int MaxTypeMembers = 24;

    private static readonly Regex s_xmlTagRegex = new(
        "<.*?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static string FormatExternalDefinition(ISymbol symbol)
    {
        string assemblyName = symbol.ContainingAssembly?.Name
            ?? symbol.Locations.FirstOrDefault(location => location.IsInMetadata)?.MetadataModule?.ContainingAssembly?.Name
            ?? "unknown assembly";

        var sb = new StringBuilder();
        sb.AppendLine("## External Definition");
        sb.AppendLine();
        sb.AppendLine($"**Provenance**: metadata-as-source preview from `{assemblyName}`");
        sb.AppendLine($"**Assembly**: {assemblyName}");

        if (symbol.ContainingNamespace is { IsGlobalNamespace: false })
            sb.AppendLine($"**Namespace**: {symbol.ContainingNamespace.ToDisplayString()}");

        if (symbol.ContainingType is not null)
            sb.AppendLine($"**Containing Type**: {symbol.ContainingType.ToDisplayString()}");

        string summary = ExtractDocumentationSummary(symbol);
        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine($"**Summary**: {summary}");

        sb.AppendLine();
        sb.AppendLine("```csharp");
        AppendPreview(sb, symbol);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "_This preview is derived from assembly metadata. Real source and decompiled method bodies are not available in this workspace._");

        return sb.ToString();
    }

    private static void AppendPreview(StringBuilder sb, ISymbol symbol)
    {
        string? namespaceName = GetNamespaceName(symbol);
        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            sb.AppendLine($"namespace {namespaceName};");
            sb.AppendLine();
        }

        if (symbol is INamedTypeSymbol type)
        {
            AppendTypePreview(sb, type, focusedMember: null, indent: "");
            return;
        }

        if (symbol.ContainingType is not null)
        {
            AppendTypePreview(sb, symbol.ContainingType, focusedMember: symbol, indent: "");
            return;
        }

        AppendSummaryComment(sb, symbol, "");
        sb.AppendLine(BuildMemberDeclaration(symbol));
    }

    private static void AppendTypePreview(
        StringBuilder sb,
        INamedTypeSymbol type,
        ISymbol? focusedMember,
        string indent)
    {
        AppendSummaryComment(sb, type, indent);

        if (type.TypeKind == TypeKind.Delegate)
        {
            sb.AppendLine($"{indent}{BuildDelegateDeclaration(type)}");
            return;
        }

        sb.AppendLine($"{indent}{BuildTypeDeclaration(type)}");
        sb.AppendLine($"{indent}{{");

        if (type.TypeKind == TypeKind.Enum)
        {
            foreach (var member in type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue))
            {
                if (focusedMember is not null &&
                    SymbolEqualityComparer.Default.Equals(member, focusedMember))
                {
                    sb.AppendLine($"{indent}    // target symbol");
                }

                sb.AppendLine($"{indent}    {member.Name},");
            }
        }
        else
        {
            var members = SelectMembers(type, focusedMember, out bool truncated);
            if (members.Count == 0)
            {
                sb.AppendLine($"{indent}    // No accessible metadata members were surfaced.");
            }
            else
            {
                foreach (var member in members)
                {
                    if (focusedMember is not null &&
                        SymbolEqualityComparer.Default.Equals(member, focusedMember))
                    {
                        sb.AppendLine($"{indent}    // target symbol");
                    }

                    AppendSummaryComment(sb, member, $"{indent}    ");
                    sb.AppendLine($"{indent}    {BuildMemberDeclaration(member)}");
                    sb.AppendLine();
                }

                if (truncated)
                    sb.AppendLine($"{indent}    // ... additional members omitted for brevity");
            }
        }

        sb.AppendLine($"{indent}}}");
    }

    private static List<ISymbol> SelectMembers(
        INamedTypeSymbol type,
        ISymbol? focusedMember,
        out bool truncated)
    {
        var members = type.GetMembers()
            .Where(member => (SymbolFormatter.ShouldDisplayMember(member) &&
                              member.DeclaredAccessibility != Accessibility.Private) ||
                             (focusedMember is not null &&
                              SymbolEqualityComparer.Default.Equals(member, focusedMember)))
            .OrderByDescending(member => focusedMember is not null &&
                                         SymbolEqualityComparer.Default.Equals(member, focusedMember))
            .ThenBy(SymbolFormatter.MemberSortOrder)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        truncated = members.Count > MaxTypeMembers;
        if (!truncated)
            return members;

        var selected = members.Take(MaxTypeMembers).ToList();
        if (focusedMember is not null &&
            !selected.Any(member => SymbolEqualityComparer.Default.Equals(member, focusedMember)))
        {
            selected[^1] = focusedMember;
        }

        return selected;
    }

    private static string BuildTypeDeclaration(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(type));
        AddIfNotEmpty(parts, GetTypeModifiers(type));
        parts.Add(GetTypeKeyword(type));
        parts.Add(type.Name + FormatTypeParameters(type.TypeParameters));

        string declaration = string.Join(" ", parts);
        string baseList = BuildBaseList(type);
        if (!string.IsNullOrWhiteSpace(baseList))
            declaration += $" : {baseList}";

        return declaration;
    }

    private static string BuildDelegateDeclaration(INamedTypeSymbol type)
    {
        var invokeMethod = type.DelegateInvokeMethod;
        if (invokeMethod is null)
            return BuildTypeDeclaration(type) + ";";

        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(type));
        parts.Add("delegate");
        parts.Add(FormatType(invokeMethod.ReturnType));
        parts.Add(type.Name + FormatTypeParameters(type.TypeParameters));

        return $"{string.Join(" ", parts)}({FormatParameters(invokeMethod.Parameters)});";
    }

    private static string BuildBaseList(INamedTypeSymbol type)
    {
        var baseTypes = new List<string>();

        if ((type.TypeKind == TypeKind.Class || type.IsRecord) &&
            type.BaseType is not null &&
            type.BaseType.SpecialType != SpecialType.System_Object)
        {
            baseTypes.Add(FormatType(type.BaseType));
        }

        foreach (var interfaceType in type.Interfaces)
            baseTypes.Add(FormatType(interfaceType));

        return string.Join(", ", baseTypes.Distinct(StringComparer.Ordinal));
    }

    private static string BuildMemberDeclaration(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => BuildMethodDeclaration(method),
            IPropertySymbol property => BuildPropertyDeclaration(property),
            IFieldSymbol field => BuildFieldDeclaration(field),
            IEventSymbol @event => BuildEventDeclaration(@event),
            _ => $"// {symbol.ToDisplayString()}"
        };
    }

    private static string BuildMethodDeclaration(IMethodSymbol method)
    {
        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(method));
        AddIfNotEmpty(parts, GetMethodModifiers(method));

        string signature = method.MethodKind switch
        {
            MethodKind.Constructor => $"{method.ContainingType.Name}({FormatParameters(method.Parameters)})",
            MethodKind.Ordinary => $"{FormatType(method.ReturnType)} {method.Name}{FormatTypeParameters(method.TypeParameters)}({FormatParameters(method.Parameters)})",
            _ => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
        };

        parts.Add(signature);
        return string.Join(" ", parts) + ";";
    }

    private static string BuildPropertyDeclaration(IPropertySymbol property)
    {
        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(property));
        AddIfNotEmpty(parts, GetPropertyModifiers(property));
        parts.Add($"{FormatType(property.Type)} {property.Name}");

        var accessors = new List<string>();
        if (property.GetMethod is not null)
            accessors.Add("get;");
        if (property.SetMethod is not null)
            accessors.Add(property.SetMethod.IsInitOnly ? "init;" : "set;");

        return $"{string.Join(" ", parts)} {{ {string.Join(" ", accessors)} }}";
    }

    private static string BuildFieldDeclaration(IFieldSymbol field)
    {
        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(field));

        if (field.IsConst)
        {
            parts.Add("const");
        }
        else
        {
            if (field.IsStatic)
                parts.Add("static");
            if (field.IsReadOnly)
                parts.Add("readonly");
        }

        parts.Add($"{FormatType(field.Type)} {field.Name}");
        return string.Join(" ", parts) + ";";
    }

    private static string BuildEventDeclaration(IEventSymbol @event)
    {
        var parts = new List<string>();
        AddIfNotEmpty(parts, GetAccessibilityText(@event));
        if (@event.IsStatic)
            parts.Add("static");
        parts.Add($"event {FormatType(@event.Type)} {@event.Name}");
        return string.Join(" ", parts) + ";";
    }

    private static string GetTypeKeyword(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record class",
        { TypeKind: TypeKind.Class } => "class",
        { TypeKind: TypeKind.Interface } => "interface",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Enum } => "enum",
        { TypeKind: TypeKind.Delegate } => "delegate",
        _ => "class"
    };

    private static string GetTypeModifiers(INamedTypeSymbol type)
    {
        var parts = new List<string>();

        if (type.IsStatic)
        {
            parts.Add("static");
            return string.Join(" ", parts);
        }

        if (type.TypeKind == TypeKind.Class && type.IsAbstract && !type.IsSealed && !type.IsRecord)
            parts.Add("abstract");
        if (type.TypeKind == TypeKind.Class && type.IsSealed && !type.IsRecord)
            parts.Add("sealed");
        if (type.TypeKind == TypeKind.Struct && type.IsReadOnly)
            parts.Add("readonly");
        if (type.IsRefLikeType)
            parts.Add("ref");

        return string.Join(" ", parts);
    }

    private static string GetMethodModifiers(IMethodSymbol method)
    {
        var parts = new List<string>();

        if (method.IsStatic)
            parts.Add("static");
        if (method.IsAbstract && method.ContainingType?.TypeKind != TypeKind.Interface)
            parts.Add("abstract");
        else if (method.IsOverride)
            parts.Add("override");
        else if (method.IsVirtual)
            parts.Add("virtual");

        if (method.IsSealed && method.IsOverride)
            parts.Insert(0, "sealed");
        if (method.IsExtern)
            parts.Add("extern");

        return string.Join(" ", parts);
    }

    private static string GetPropertyModifiers(IPropertySymbol property)
    {
        var parts = new List<string>();

        if (property.IsStatic)
            parts.Add("static");
        if (property.IsAbstract && property.ContainingType?.TypeKind != TypeKind.Interface)
            parts.Add("abstract");
        else if (property.IsOverride)
            parts.Add("override");
        else if (property.IsVirtual)
            parts.Add("virtual");

        return string.Join(" ", parts);
    }

    private static string GetAccessibilityText(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => string.Empty
    };

    private static string FormatParameters(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(FormatParameter));

    private static string FormatParameter(IParameterSymbol parameter)
    {
        var parts = new List<string>();

        if (parameter.IsParams)
            parts.Add("params");

        switch (parameter.RefKind)
        {
            case RefKind.Ref:
                parts.Add("ref");
                break;
            case RefKind.Out:
                parts.Add("out");
                break;
            case RefKind.In:
                parts.Add("in");
                break;
        }

        parts.Add(FormatType(parameter.Type));
        parts.Add(parameter.Name);

        if (parameter.HasExplicitDefaultValue)
            parts.Add($"= {FormatDefaultValue(parameter.ExplicitDefaultValue)}");

        return string.Join(" ", parts);
    }

    private static string FormatDefaultValue(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        _ => value.ToString() ?? "null"
    };

    private static string FormatType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string FormatTypeParameters(ImmutableArray<ITypeParameterSymbol> typeParameters) =>
        typeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", typeParameters.Select(parameter => parameter.Name)) + ">";

    private static void AppendSummaryComment(StringBuilder sb, ISymbol symbol, string indent)
    {
        string summary = ExtractDocumentationSummary(symbol);
        if (string.IsNullOrWhiteSpace(summary))
            return;

        sb.AppendLine($"{indent}// {summary}");
    }

    private static string ExtractDocumentationSummary(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return string.Empty;

        string withoutTags = s_xmlTagRegex.Replace(xml, " ");
        string decoded = WebUtility.HtmlDecode(withoutTags);
        return string.Join(
            ' ',
            decoded.Split(
                ['\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? GetNamespaceName(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol type when !type.ContainingNamespace.IsGlobalNamespace => type.ContainingNamespace.ToDisplayString(),
            _ when symbol.ContainingNamespace is { IsGlobalNamespace: false } => symbol.ContainingNamespace.ToDisplayString(),
            _ => null
        };

    private static void AddIfNotEmpty(List<string> parts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value);
    }
}
