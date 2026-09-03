namespace RoslynMCP.Lsp.Completion;

/// <summary>
/// Relevance of a completion item as a 64-bit flag word, ordered so that comparing two words as
/// integers <em>is</em> the ranking: a higher bit always beats every combination of lower ones.
/// No weights, no floats, and the reason an item ranks where it does is readable off the bits
/// (see <see cref="CompletionRelevanceFormatter"/>). Modelled on ReSharper's LookupItemRelevance.
/// </summary>
/// <remarks>
/// Layout: the top band is match quality (recomputed on every keystroke), the middle band is
/// language-specific classification (computed once per completion request), the bottom band is
/// tie-breakers. Bits in the middle band live in <see cref="ClrLookupItemRelevance"/>.
/// </remarks>
[Flags]
public enum LookupItemRelevance : ulong
{
    None = 0,

    // --- match quality: stamped per keystroke from MatcherScore ---
    ExactMatch = 0x0800_0000_0000_0000,
    ExactNoCaseMatch = 0x0200_0000_0000_0000,
    PrefixMatch = 0x0100_0000_0000_0000,
    CamelHumpsCaseMatch = 0x0080_0000_0000_0000,
    PrefixNoCaseMatch = 0x0040_0000_0000_0000,
    CamelHumpsNoCaseMatch = 0x0020_0000_0000_0000,

    /// <summary>Every bit above that the sorter owns and rewrites on each keystroke.</summary>
    MatchSensitive = ExactMatch | ExactNoCaseMatch | PrefixMatch
                     | CamelHumpsCaseMatch | PrefixNoCaseMatch | CamelHumpsNoCaseMatch | Statistical,

    // --- tie-breakers ---
    HighSelectionPriority = 0x0000_0000_0000_0100,
    NormalSelectionPriority = 0x0000_0000_0000_0080,

    /// <summary>The local variable declared closest above the caret.</summary>
    ClosestLocalVar = 0x0000_0000_0000_0020,

    /// <summary>Most-used item of its relevance tier, per recorded usage.</summary>
    Statistical = 0x0000_0000_0000_0010,

    /// <summary>Item accepted last time completion ran in this context.</summary>
    LastChoice = 0x0000_0000_0000_0008,

    Other = 0x0000_0000_0000_0001,

    /// <summary>Bits below this are tie-breakers; grouping for statistics ignores them.</summary>
    AboveStatisticalMask = 0xFFFF_FFFF_FFFF_FFE0,
}

/// <summary>
/// The C#-specific middle band of <see cref="LookupItemRelevance"/>: what kind of thing the item
/// is and where it came from. Read top-down, this is the priority order of the completion list.
/// </summary>
[Flags]
public enum ClrLookupItemRelevance : ulong
{
    None = 0,

    /// <summary>Roslyn decided the item's type matches the type expected at the caret.</summary>
    ExpectedTypeMatch = 0x0008_0000_0000_0000,

    // --- element kind ---
    EnumMembers = 0x0000_4000_0000_0000,
    LocalVariablesAndParameters = 0x0000_1000_0000_0000,
    FieldsAndProperties = 0x0000_0800_0000_0000,
    Methods = 0x0000_0400_0000_0000,
    Events = 0x0000_0200_0000_0000,
    ExtensionMethods = 0x0000_0080_0000_0000,
    Keywords = 0x0000_0010_0000_0000,
    TypesAndNamespaces = 0x0000_0004_0000_0000,
    LiveTemplates = 0x0000_0002_0000_0000,

    // --- provenance ---
    /// <summary>Declared by the type being completed on (or by the enclosing type).</summary>
    MemberOfCurrentType = 0x0000_0000_1000_0000,

    /// <summary>Inherited from a base type or interface.</summary>
    MemberOfBaseType = 0x0000_0000_0800_0000,

    /// <summary>
    /// ToString/GetHashCode/Equals and friends: noise on every single dot. They carry no kind
    /// bit at all, which puts them below every real member instead of merely below their peers.
    /// </summary>
    MemberOfObject = 0x0000_0000_0400_0000,

    /// <summary>Already reachable from the file's usings.</summary>
    Imported = 0x0000_0000_0200_0000,

    /// <summary>Import completion: committing it adds a using directive.</summary>
    NotImported = 0x0000_0000_0100_0000,

    /// <summary>Cleared for [Obsolete] items, which sinks them below everything else of their kind.</summary>
    NotObsolete = 0x0000_0000_0020_0000,
}
