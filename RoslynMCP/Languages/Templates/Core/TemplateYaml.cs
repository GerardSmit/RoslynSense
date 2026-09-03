using System.Collections.Immutable;
using RoslynMCP.Lsp.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Templates.Core;

/// <summary>
/// One template file, read into declarations that each know where they were written.
/// </summary>
/// <remarks>
/// <para>
/// Read through the representation model rather than deserialized into a shape. A deserializer
/// gives back values and throws the positions away, and a position is most of what this pack is
/// for: a row in the tree is worth having because clicking it lands on the line that declares it.
/// </para>
/// <para>
/// Only the four things the tree draws are looked at — the entries, their names and parents, what
/// each hosts, and where the modules are served from. Everything else in the file is settings for
/// the application that reads it, and is skipped rather than modelled, so a file that grows a
/// section this pack has never heard of still lists.
/// </para>
/// </remarks>
internal static class TemplateYaml
{
    /// <summary>The mapping holding the tree.</summary>
    public const string Entries = "tabs";

    /// <summary>
    /// Two mappings, one word.
    /// </summary>
    /// <remarks>
    /// At the top of a file it declares modules — a name, and the controls that render one. On an
    /// entry it is a list of the modules that entry hosts, each naming one by <see cref="Type"/>.
    /// The file says <c>modules</c> for both, so this pack reads both.
    /// </remarks>
    public const string Modules = "modules";

    public const string Name = "name";

    public const string Parent = "parent";

    public const string Type = "type";

    public const string Controls = "controls";

    public const string Level = "level";

    public const string Path = "path";

    /// <summary>What one file declares, or why it could not be read.</summary>
    public static TemplateDocument Read(string filePath, string text)
    {
        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            return new TemplateDocument(filePath, [], [], $"line {ex.Start.Line}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new TemplateDocument(filePath, [], [], ex.Message);
        }

        var entries = ImmutableArray.CreateBuilder<TemplateEntry>();
        var modules = ImmutableArray.CreateBuilder<TemplateModule>();

        foreach (var document in stream.Documents)
        {
            foreach (var (key, node, _) in Pairs(document.RootNode, filePath))
            {
                if (key.Equals(Entries, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (name, value, at) in Pairs(node, filePath))
                        entries.Add(Entry(name, value, at, filePath));
                }
                else if (key.Equals(Modules, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (name, value, at) in Pairs(node, filePath))
                        modules.Add(Module(name, value, at, filePath));
                }
            }
        }

        return new TemplateDocument(filePath, entries.ToImmutable(), modules.ToImmutable());
    }

    private static TemplateEntry Entry(
        string key, YamlNode? node, TemplateSite site, string filePath)
    {
        var names = ImmutableArray.CreateBuilder<TemplateName>();
        var hosted = ImmutableArray.CreateBuilder<TemplateModuleUse>();
        string? parent = null;

        foreach (var (field, value, _) in Pairs(node, filePath))
        {
            if (field.Equals(Name, StringComparison.OrdinalIgnoreCase))
                ReadNames(value, filePath, names);
            else if (field.Equals(Parent, StringComparison.OrdinalIgnoreCase))
                parent = Text(value);
            else if (field.Equals(Modules, StringComparison.OrdinalIgnoreCase))
                ReadHosted(value, filePath, hosted);
        }

        return new TemplateEntry(key, parent, names.ToImmutable(), hosted.ToImmutable(), site);
    }

    /// <summary>
    /// The display names of an entry.
    /// </summary>
    /// <remarks>
    /// Either a mapping of language tag to text, or one plain name. Both spellings occur in the
    /// same folder, so both are read, and the plain one is filed under the empty tag rather than
    /// under a language it never claimed.
    /// </remarks>
    private static void ReadNames(
        YamlNode? node, string filePath, ImmutableArray<TemplateName>.Builder names)
    {
        if (Text(node) is { Length: > 0 } plain)
        {
            Add(names, string.Empty, plain);
            return;
        }

        foreach (var (tag, text, _) in Pairs(node, filePath))
        {
            if (Text(text) is { Length: > 0 } localized)
                Add(names, tag, localized);
        }
    }

    /// <summary>One name per tag, and the first spelling of a tag wins.</summary>
    /// <remarks>
    /// A file repeating a language is a file that was edited twice, and YAML itself would keep the
    /// last of the two — but the order is what <see cref="TemplateEntry.Label"/> reads, so keeping
    /// the first keeps the position of the tag as well as its text.
    /// </remarks>
    private static void Add(ImmutableArray<TemplateName>.Builder names, string tag, string text)
    {
        foreach (var known in names)
        {
            if (known.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                return;
        }

        names.Add(new TemplateName(tag, text));
    }

    private static void ReadHosted(
        YamlNode? node, string filePath, ImmutableArray<TemplateModuleUse>.Builder hosted)
    {
        if (node is not YamlSequenceNode sequence)
            return;

        foreach (var item in sequence.Children)
        {
            foreach (var (field, value, at) in Pairs(item, filePath))
            {
                if (field.Equals(Type, StringComparison.OrdinalIgnoreCase)
                    && Text(value) is { Length: > 0 } type)
                {
                    hosted.Add(new TemplateModuleUse(type, at));
                }
            }
        }
    }

    private static TemplateModule Module(
        string key, YamlNode? node, TemplateSite site, string filePath)
    {
        string? name = null;
        var controls = ImmutableArray.CreateBuilder<TemplateControl>();

        foreach (var (field, value, _) in Pairs(node, filePath))
        {
            if (field.Equals(Name, StringComparison.OrdinalIgnoreCase))
                name = Text(value);
            else if (field.Equals(Controls, StringComparison.OrdinalIgnoreCase))
                ReadControls(value, filePath, controls);
        }

        return new TemplateModule(key, name, controls.ToImmutable(), site);
    }

    private static void ReadControls(
        YamlNode? node, string filePath, ImmutableArray<TemplateControl>.Builder controls)
    {
        foreach (var (control, body, at) in Pairs(node, filePath))
        {
            string? level = null;
            string? path = null;

            foreach (var (setting, text, _) in Pairs(body, filePath))
            {
                if (setting.Equals(Level, StringComparison.OrdinalIgnoreCase))
                    level = Text(text);
                else if (setting.Equals(Path, StringComparison.OrdinalIgnoreCase))
                    path = Text(text);
            }

            controls.Add(new TemplateControl(control, level, path, at));
        }
    }

    /// <summary>
    /// The pairs of a mapping, in file order, each with the range of its own key.
    /// </summary>
    /// <remarks>
    /// In file order because a template folder has no ordering field: the order things are written
    /// in is the only order there is, and a reader comparing the tree against the file is
    /// comparing against that. Anything that is not a mapping yields nothing rather than throwing
    /// — a hand-written file gets a key wrong sooner or later, and the rest of it is still worth
    /// listing.
    /// </remarks>
    private static IEnumerable<(string Key, YamlNode? Value, TemplateSite Site)> Pairs(
        YamlNode? node, string filePath)
    {
        if (node is not YamlMappingNode mapping)
            yield break;

        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { Length: > 0 } key })
                continue;

            yield return (key, pair.Value, new TemplateSite(filePath, RangeOf(pair.Key)));
        }
    }

    private static string? Text(YamlNode? node) =>
        node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    /// <summary>A node's own span, as the editor counts lines and columns.</summary>
    /// <remarks>
    /// YAML marks are 1-based on both axes and LSP positions are 0-based on both, so both are
    /// decremented. A mark at column 0 would be a parser that reported nothing, and clamping is
    /// cheaper than a range the client rejects.
    /// </remarks>
    private static Range RangeOf(YamlNode node) => new(At(node.Start), At(node.End));

    private static Position At(Mark mark) =>
        new(Math.Max(0, (int)mark.Line - 1), Math.Max(0, (int)mark.Column - 1));
}
