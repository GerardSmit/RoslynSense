using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WebFormsCore.SourceGenerator.Models;

/// <summary>
/// Basic diagnostic description for reporting diagnostic inside the incremental pipeline.
/// </summary>
/// <param name="Descriptor">Diagnostic descriptor.</param>
/// <param name="FilePath">File path.</param>
/// <param name="TextSpan">Text span.</param>
/// <param name="LineSpan">Line span.</param>
/// <see href="https://github.com/dotnet/roslyn/issues/62269#issuecomment-1170760367" />
public sealed record ReportedDiagnostic(
    DiagnosticDescriptor Descriptor,
    TextSpan TextSpan,
    FileLinePositionSpan FileLineSpan,
    EquatableArray<object> Arguments)
{
    /// <summary>
    /// Implicitly converts <see cref="ReportedDiagnostic"/> to <see cref="Diagnostic"/>.
    /// </summary>
    /// <param name="diagnostic">Diagnostic to convert.</param>
    public static implicit operator Diagnostic(ReportedDiagnostic diagnostic)
    {
        // A diagnostic that knows no file becomes one with no location, rather than throwing.
        // Location.Create names its first parameter filePath and rejects null, and
        // FileLinePositionSpan.Path is null whenever the reported location had no syntax tree
        // behind it — which is an ordinary thing for a parser to produce and not an error.
        //
        // Worth being careful about because of where it lands. This conversion runs while the
        // markup is being parsed, so the throw came out of Parse rather than out of whatever
        // reported the diagnostic, and it took down every feature for that file at once: hover,
        // folding, document symbols, semantic tokens, code lens, diagnostics — each one asks for
        // the parse first. The trigger was a control registered with a src= that File.Exists could
        // not confirm, which is what a symlinked web root does.
        var location = string.IsNullOrEmpty(diagnostic.FileLineSpan.Path)
            ? Location.None
            : Location.Create(diagnostic.FileLineSpan.Path, diagnostic.TextSpan, diagnostic.FileLineSpan.Span);

        return Diagnostic.Create(
            descriptor: diagnostic.Descriptor,
            location: location,
            messageArgs: diagnostic.Arguments.GetUnsafeArray());
    }

    /// <summary>
    /// Creates a new <see cref="ReportedDiagnostic"/> from <see cref="DiagnosticDescriptor"/> and <see cref="Location"/>.
    /// </summary>
    /// <param name="descriptor">Descriptor.</param>
    /// <param name="location">Location.</param>
    /// <param name="arguments">Arguments.</param>
    /// <returns>A new <see cref="ReportedDiagnostic"/>.</returns>
    public static ReportedDiagnostic Create(DiagnosticDescriptor descriptor, Location location, params object[] arguments)
    {
        return new(descriptor, location.SourceSpan, location.GetLineSpan(), arguments.ToImmutableArray());
    }
}