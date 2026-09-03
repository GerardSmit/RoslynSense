using System.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages;

/// <summary>One file's declaration of one setting: where it is, and what it says there.</summary>
/// <param name="Label">How the file is worth naming to a reader — a bare file name where the
/// directory carries nothing (<c>appsettings.Development.json</c>), a project-relative path where
/// it carries everything (<c>Admin\web.config</c>).</param>
/// <param name="Value">The value as written, or null for a declaration that has none to show —
/// a section rather than a leaf.</param>
internal readonly record struct ConfigDeclaration(
    string FilePath, string Label, string? Value, LspLocation Location);

/// <summary>
/// What an override chain does to one setting, said in the two places a reader looks: the hover
/// over the declaration, and a lens above it.
/// </summary>
/// <remarks>
/// <para>
/// Both configuration systems have the same shape and the same blind spot. A value is declared in
/// more than one file, one of them wins, and the file you are reading says nothing about which —
/// <c>appsettings.json</c> looks authoritative while <c>appsettings.Development.json</c> quietly
/// replaces half of it, and an application <c>web.config</c> looks authoritative while a
/// subdirectory's does the same. This is the C# inheritance marker's answer to the same problem:
/// an arrow up to what this replaces, an arrow down to what replaces it.
/// </para>
/// <para>
/// The chain is given weakest-first — base file, then overlays, then secrets; application root,
/// then nested directories — so everything after the current file overrides it and everything
/// before it is what the current file overrides. Which of the stronger ones actually applies is
/// not knowable here: it depends on the environment the application runs under, and saying
/// "overridden in" rather than "overridden by" keeps the difference honest.
/// </para>
/// </remarks>
internal static class ConfigOverrides
{
    /// <summary>A value longer than this is a paragraph, not a value; the file itself is a click
    /// away for the rest.</summary>
    private const int MaxValueLength = 60;

    /// <summary>
    /// The chain split around the file being read: what it overrides, and what overrides it. The
    /// current file's own declaration is in neither.
    /// </summary>
    public static (List<ConfigDeclaration> Weaker, List<ConfigDeclaration> Stronger) Split(
        IReadOnlyList<ConfigDeclaration> chain, string currentFilePath)
    {
        var weaker = new List<ConfigDeclaration>();
        var stronger = new List<ConfigDeclaration>();
        bool passed = false;

        foreach (var declaration in chain)
        {
            if (string.Equals(declaration.FilePath, currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                passed = true;
                continue;
            }

            (passed ? stronger : weaker).Add(declaration);
        }

        return (weaker, stronger);
    }

    /// <summary>
    /// The override half of a hover: what this declaration replaces and what replaces it, each
    /// with the value that file gives, so the reader can see the change rather than infer it.
    /// </summary>
    public static void Append(
        StringBuilder builder, IReadOnlyList<ConfigDeclaration> chain, string currentFilePath)
    {
        var (weaker, stronger) = Split(chain, currentFilePath);

        if (stronger.Count > 0)
        {
            builder.Append("\n\nOverridden in:");

            foreach (var declaration in stronger)
                Line(builder, declaration);
        }

        if (weaker.Count > 0)
        {
            builder.Append("\n\nOverrides:");

            foreach (var declaration in weaker)
                Line(builder, declaration);
        }
    }

    private static void Line(StringBuilder builder, ConfigDeclaration declaration)
    {
        builder.Append("\n- `").Append(declaration.Label).Append('`');

        if (Display(declaration.Value) is { } value)
            builder.Append(" → `").Append(value).Append('`');
    }

    /// <summary>
    /// The lenses for one declaration: down to the files that override it, up to the ones it
    /// overrides. Neither is emitted when there is nothing to point at — unlike the reference
    /// count, where a zero is the finding, a setting declared once is the normal case and a lens
    /// saying so on every line is noise.
    /// </summary>
    public static IEnumerable<LspCodeLens> Lenses(
        IReadOnlyList<ConfigDeclaration> chain, string currentFilePath, LspRange range)
    {
        var (weaker, stronger) = Split(chain, currentFilePath);

        string uri = LspConverters.PathToUri(currentFilePath);

        if (stronger.Count > 0)
            yield return Lens(uri, range, "↓ overridden", stronger);

        if (weaker.Count > 0)
            yield return Lens(uri, range, "↑ overrides", weaker);
    }

    /// <summary>
    /// The title says only what happened; which file did it is a hover away and a click away.
    /// A lens sits at the end of a line the reader is already reading, and a file name there —
    /// long, repeated down the file, the same on every key — costs more than it says.
    /// </summary>
    private static LspCodeLens Lens(
        string uri, LspRange range, string title, List<ConfigDeclaration> targets) =>
        new(range, new Command(
            title, "roslynSense.showReferences",
            [
                // The peek opens where the lens is and lists the other declarations beside it.
                uri, range.Start.Line, range.Start.Character,
                targets.Select(d => d.Location).ToArray(),
            ]));

    /// <summary>A value as a hover can show it: one line, quoted-code short.</summary>
    private static string? Display(string? value)
    {
        if (value is null)
            return null;

        string flattened = value.ReplaceLineEndings(" ").Trim();

        if (flattened.Length == 0)
            return "(empty)";

        return flattened.Length > MaxValueLength
            ? flattened[..MaxValueLength] + "…"
            : flattened;
    }
}
