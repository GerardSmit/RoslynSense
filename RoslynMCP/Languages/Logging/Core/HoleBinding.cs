using System.Collections.Immutable;

namespace RoslynMCP.Languages.Logging.Core;

/// <summary>
/// One hole and the value it renders, or the fact that it renders nothing.
/// </summary>
/// <param name="Position">Which value it reaches, counted from 0, whether or not one is there.
/// This is the number a message quotes, because "the third value" is what someone counts along the
/// argument list to check.</param>
internal readonly record struct BoundHole(TemplateHole Hole, int Position, TemplateValue? Value);

/// <summary>
/// Which value each hole renders — the one question every feature in the pack is asking, and the
/// one nobody can answer by reading the call.
/// </summary>
/// <remarks>
/// Three rules, and which applies is decided by the framework and by the template together:
/// <list type="bullet">
/// <item>a generated method binds by name, matching the parameter case-insensitively;</item>
/// <item>a composite-format template — every hole a number — binds by that number;</item>
/// <item>everything else binds by order of appearance, names notwithstanding.</item>
/// </list>
/// The third is the one that surprises people. <c>Log.Warning("{User} left {Room}", room, user)</c>
/// logs the room as User and the user as Room, forever, and nothing in the language, the library or
/// the compiler has a word to say about it.
/// </remarks>
internal static class HoleBinding
{
    public static ImmutableArray<BoundHole> Bind(MessageTemplate template, LogCallSite site)
    {
        if (template.Holes.IsEmpty)
            return [];

        var bound = ImmutableArray.CreateBuilder<BoundHole>(template.Holes.Length);

        foreach (var hole in template.Holes)
        {
            if (site.Binding == TemplateBinding.ByName)
            {
                int at = IndexOfName(site.Values, hole.Name);
                bound.Add(new BoundHole(hole, at, at < 0 ? null : site.Values[at]));
                continue;
            }

            int position = hole.Kind == HoleKind.Positional ? hole.Index : hole.Ordinal;

            bound.Add(new BoundHole(
                hole, position,
                position >= 0 && position < site.Values.Length ? site.Values[position] : null));
        }

        return bound.ToImmutable();
    }

    /// <summary>
    /// The values no hole renders.
    /// </summary>
    /// <remarks>
    /// Worth a warning in both directions of binding, for different reasons. Under a generated
    /// method the parameter is captured into the log state and never printed, which some sinks
    /// surface and most do not. At a call site it is worse: positional binding means an extra
    /// value is not an unused one but a <i>shifted</i> one, so the holes after the mistake are all
    /// rendering the wrong thing.
    /// </remarks>
    public static ImmutableArray<TemplateValue> Unrendered(MessageTemplate template, LogCallSite site)
    {
        if (site.Values.IsEmpty)
            return [];

        var reached = new bool[site.Values.Length];

        foreach (var bound in Bind(template, site))
        {
            if (bound.Position >= 0 && bound.Position < reached.Length)
                reached[bound.Position] = true;
        }

        var unused = ImmutableArray.CreateBuilder<TemplateValue>();

        for (int i = 0; i < reached.Length; i++)
        {
            if (!reached[i])
                unused.Add(site.Values[i]);
        }

        return unused.ToImmutable();
    }

    /// <summary>The names a hole can be completed to, in the order they are passed.</summary>
    public static ImmutableArray<TemplateValue> Offered(LogCallSite site) => site.Values;

    private static int IndexOfName(ImmutableArray<TemplateValue> values, string name)
    {
        for (int i = 0; i < values.Length; i++)
        {
            // Case-insensitively, the way the source generator matches — which is also why it
            // warns about two holes differing only by case.
            if (string.Equals(values[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
