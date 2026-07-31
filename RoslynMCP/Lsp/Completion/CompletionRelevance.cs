using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using RoslynItem = Microsoft.CodeAnalysis.Completion.CompletionItem;

namespace RoslynMCP.Lsp.Completion;

/// <summary>
/// Turns a Roslyn completion item into the static half of its relevance word — everything that
/// does not depend on what the user has typed so far. Computed once per completion request; the
/// match-sensitive bits are stamped on top per keystroke by <see cref="CompletionRanker"/>.
/// </summary>
internal static class CompletionRelevance
{
    public static ulong Compute(RoslynItem item, CompletionSemanticContext semantics)
    {
        var relevance = LookupItemRelevance.Other | LookupItemRelevance.NormalSelectionPriority;
        var tags = item.Tags;

        if (item.Rules.MatchPriority >= MatchPriority.Preselect)
            relevance |= LookupItemRelevance.HighSelectionPriority;

        if (item.DisplayText == semantics.ClosestLocalName)
            relevance |= LookupItemRelevance.ClosestLocalVar;

        var clr = ClrLookupItemRelevance.None;

        // Roslyn's own "this fits the type expected here" signal (the ★ items in VS) — the
        // cheap equivalent of ReSharper's expected-type analysis, and it is already computed.
        if (tags.Contains(WellKnownTags.TargetTypeMatch))
            clr |= ClrLookupItemRelevance.ExpectedTypeMatch;

        if (!tags.Contains(WellKnownTags.Deprecated))
            clr |= ClrLookupItemRelevance.NotObsolete;

        clr |= IsUnimported(item) ? ClrLookupItemRelevance.NotImported : ClrLookupItemRelevance.Imported;

        var provenance = semantics.ProvenanceOf(item.DisplayText);
        switch (provenance)
        {
            case MemberProvenance.CurrentType:
                clr |= ClrLookupItemRelevance.MemberOfCurrentType | KindOf(tags, provenance);
                break;
            case MemberProvenance.BaseType:
                clr |= ClrLookupItemRelevance.MemberOfBaseType | KindOf(tags, provenance);
                break;
            case MemberProvenance.Object:
                // No kind bit on purpose: ToString and friends belong under every real member,
                // not merely under the other methods.
                clr |= ClrLookupItemRelevance.MemberOfObject;
                break;
            default:
                clr |= KindOf(tags, provenance);
                break;
        }

        return (ulong)relevance | (ulong)clr;
    }

    /// <summary>Import completion items: committing one adds a using directive.</summary>
    public static bool IsUnimported(RoslynItem item) =>
        item.Flags.HasFlag(CompletionItemFlags.Expanded) || !string.IsNullOrEmpty(item.InlineDescription);

    private static ClrLookupItemRelevance KindOf(IReadOnlyList<string> tags, MemberProvenance provenance)
    {
        foreach (string tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable:
                    return ClrLookupItemRelevance.LocalVariablesAndParameters;
                case WellKnownTags.EnumMember:
                    return ClrLookupItemRelevance.EnumMembers;
                case WellKnownTags.Field or WellKnownTags.Property or WellKnownTags.Constant:
                    return ClrLookupItemRelevance.FieldsAndProperties;
                case WellKnownTags.ExtensionMethod:
                    // Roslyn tags an item ExtensionMethod as soon as *any* overload behind the
                    // name is one, so string.ToUpper (which has a MemoryExtensions overload)
                    // would be demoted with the real extensions. The type's own member list is
                    // the tie-breaker: found there, it is an instance method.
                    return provenance == MemberProvenance.Unknown
                        ? ClrLookupItemRelevance.ExtensionMethods
                        : ClrLookupItemRelevance.Methods;
                case WellKnownTags.Method or WellKnownTags.Operator:
                    return ClrLookupItemRelevance.Methods;
                case WellKnownTags.Event:
                    return ClrLookupItemRelevance.Events;
                case WellKnownTags.Keyword:
                    return ClrLookupItemRelevance.Keywords;
                case WellKnownTags.Snippet:
                    return ClrLookupItemRelevance.LiveTemplates;
                case WellKnownTags.Class or WellKnownTags.Interface or WellKnownTags.Structure
                    or WellKnownTags.Enum or WellKnownTags.Delegate or WellKnownTags.Namespace
                    or WellKnownTags.Module or WellKnownTags.TypeParameter:
                    return ClrLookupItemRelevance.TypesAndNamespaces;
            }
        }

        return ClrLookupItemRelevance.None;
    }
}

/// <summary>Renders a relevance word as its flag names — the "why is this item here" view.</summary>
public static class CompletionRelevanceFormatter
{
    public static string Format(ulong relevance)
    {
        var names = new List<string>();
        foreach (LookupItemRelevance flag in Enum.GetValues<LookupItemRelevance>())
        {
            if (flag is LookupItemRelevance.None or LookupItemRelevance.MatchSensitive
                or LookupItemRelevance.AboveStatisticalMask)
                continue;
            if ((relevance & (ulong)flag) == (ulong)flag)
                names.Add(flag.ToString());
        }

        foreach (ClrLookupItemRelevance flag in Enum.GetValues<ClrLookupItemRelevance>())
        {
            if (flag == ClrLookupItemRelevance.None)
                continue;
            if ((relevance & (ulong)flag) == (ulong)flag)
                names.Add(flag.ToString());
        }

        return names.Count == 0 ? "None" : string.Join(" | ", names);
    }
}
