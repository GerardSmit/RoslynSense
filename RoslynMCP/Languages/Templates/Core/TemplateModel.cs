using System.Collections.Immutable;
using RoslynMCP.Lsp.Protocol;

using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>Where something is written: a file, and the range of the key that declares it.</summary>
/// <remarks>
/// The key rather than the block under it. A range covering the whole declaration would select
/// forty lines when the row is clicked, and the thing a reader wants to see is the name they just
/// clicked on with its own block underneath — which is what putting the caret on the key gives.
/// </remarks>
internal readonly record struct TemplateSite(string FilePath, Range Range);

/// <summary>One control of a module: a name, the access level it needs, and the file serving it.</summary>
/// <param name="Path">
/// As written, which is relative to the application root rather than to the file it is written in.
/// Resolving it against the disk is <see cref="TemplateSet.Resolve"/>'s job, and it can fail.
/// </param>
internal sealed record TemplateControl(
    string Name, string? Level, string? Path, TemplateSite Site);

/// <summary>
/// One module: a thing a page can host, and the controls that render it.
/// </summary>
internal sealed record TemplateModule(
    string Key, string? Name, ImmutableArray<TemplateControl> Controls, TemplateSite Site)
{
    /// <summary>The name a `default` control is written under.</summary>
    /// <remarks>
    /// A module's controls are keyed by the mode they serve — <c>edit</c>, <c>generate</c>,
    /// <c>details</c> — and the one that renders the page for an ordinary visitor is the one with
    /// no mode. Two spellings mean that: the level says <c>view</c>, or the name is
    /// <c>default</c>, which is the key a control registered under the empty name is written with.
    /// </remarks>
    private const string Default = "default";

    private const string ViewLevel = "view";

    /// <summary>
    /// The control a reader means by "the implementation", or null when the module declares none.
    /// </summary>
    /// <remarks>
    /// The view control rather than the first one, because a module's first control in file order
    /// is as often <c>edit</c> — and landing in the settings screen when the row named the page is
    /// the wrong half of the answer. The first control is the fallback rather than the rule, for
    /// the modules that declare only an editor.
    /// </remarks>
    public TemplateControl? View =>
        Controls.FirstOrDefault(control =>
            string.Equals(control.Name, Default, StringComparison.OrdinalIgnoreCase)
            || string.Equals(control.Level, ViewLevel, StringComparison.OrdinalIgnoreCase))
        ?? (Controls.IsEmpty ? null : Controls[0]);
}

/// <summary>One display name of an entry: the language tag it was written under, and the text.</summary>
/// <param name="Tag">
/// The language tag, or the empty string for a name written as a plain scalar under no language.
/// </param>
internal readonly record struct TemplateName(string Tag, string Text);

/// <summary>A module a page hosts, by the name the module was registered under.</summary>
internal sealed record TemplateModuleUse(string Type, TemplateSite Site);

/// <summary>
/// One entry of the tree: a page, its place under its parent, and what it hosts.
/// </summary>
/// <param name="Names">
/// The display names, in the order the file writes them — one per language tag, or a single one
/// under the empty tag when the name is written as a plain scalar. Empty when the entry declares no
/// name at all, which is when the key is shown.
///
/// A list rather than a dictionary because the order is the answer to "which language". A folder of
/// templates written for a Dutch application writes the Dutch name first and the translations after
/// it, so first-written is the one the developer recognises; keyed by tag, the fallback would have
/// to pick alphabetically, and <c>de-DE</c> would win every row in the tree.
/// </param>
internal sealed record TemplateEntry(
    string Key,
    string? Parent,
    ImmutableArray<TemplateName> Names,
    ImmutableArray<TemplateModuleUse> Modules,
    TemplateSite Site)
{
    /// <summary>
    /// What the row says.
    /// </summary>
    /// <remarks>
    /// The configured language when the entry writes it, then whichever the file wrote first, then
    /// the key. A row reading nothing because one file omitted <c>nl-NL</c> would be worse than a
    /// row reading the name in the wrong language, and a row reading the key is still a row
    /// somebody can find their way from — which is the whole job.
    /// </remarks>
    public string Label(string? locale)
    {
        if (locale is { Length: > 0 } && Find(locale) is { Length: > 0 } preferred)
            return preferred;

        return Names.IsEmpty ? Key : Names[0].Text;
    }

    private string? Find(string tag) =>
        Names
            .FirstOrDefault(name => name.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
            .Text;
}

/// <summary>What one YAML file declares, with every declaration carrying its own position.</summary>
/// <param name="Error">
/// Why the file was not read, when it was not. A file with a tab where YAML wants spaces is a real
/// thing to find in a folder of two hundred, and losing the other hundred and ninety-nine over it
/// would be the wrong trade.
/// </param>
internal sealed record TemplateDocument(
    string FilePath,
    ImmutableArray<TemplateEntry> Entries,
    ImmutableArray<TemplateModule> Modules,
    string? Error = null);
