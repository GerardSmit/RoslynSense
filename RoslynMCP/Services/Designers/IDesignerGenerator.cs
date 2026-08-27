using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services.Designers;

/// <summary>
/// The generated content for one designer file, or the reasons it could not be produced.
/// </summary>
/// <param name="DesignerPath">Where the designer file belongs, whether or not it exists yet.</param>
/// <param name="Content">
/// The full designer file text, or <c>null</c> when generation failed. A null result must leave any
/// existing file untouched — a stale designer is far better than a truncated one.
/// </param>
public sealed record DesignerResult(
    string DesignerPath,
    string? Content,
    IReadOnlyList<string> Errors)
{
    public static DesignerResult Failed(string designerPath, params string[] errors) =>
        new(designerPath, null, errors);

    /// <summary>
    /// Whether the generated content declares nothing at all — an empty partial class. That is a
    /// legitimate result (markup with no server IDs, or a variant whose fields live in the shared
    /// designer of its group), but it is not worth a file that does not exist yet.
    /// </summary>
    public bool DeclaresNoMembers { get; init; }

    /// <summary>
    /// Other source files whose designers this result made stale, and which should therefore be
    /// regenerated as well — the other markup files of a shared code-behind class. Empty for the
    /// common single-file case.
    /// </summary>
    public IReadOnlyList<string> RelatedSources { get; init; } = [];
}

/// <summary>
/// Produces the generated companion file for a source Visual Studio would run a custom tool on
/// (<c>.aspx</c>/<c>.ascx</c>/<c>.master</c> markup, a <c>.dbml</c> model).
/// </summary>
public interface IDesignerGenerator
{
    /// <summary>Whether this generator owns the given source file.</summary>
    bool CanHandle(string filePath);

    /// <summary>The designer file path this generator would produce for the source file.</summary>
    string GetDesignerPath(string filePath);

    Task<DesignerResult> GenerateAsync(string filePath, Project project, CancellationToken cancellationToken);
}
