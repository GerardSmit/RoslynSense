using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Services;

namespace RoslynMCP.Tools.Razor;

/// <summary>
/// Resolves symbols in Razor files by mapping marked positions to generated C#
/// documents via the Razor source map.
/// </summary>
internal class RazorGoToDefinition : IGoToDefinitionHandler
{
    public bool CanHandle(string filePath) => RazorSourceMappingService.IsRazorFile(filePath);

    public async Task<string> ResolveAsync(
        string systemPath, string markupSnippet, int contextLines, CancellationToken cancellationToken)
    {
        if (!MarkupString.TryParse(markupSnippet, out var markup, out string? parseError))
            return $"Error: Invalid markup snippet. {parseError}";

        if (!File.Exists(systemPath))
            return $"Error: File {systemPath} does not exist.";

        string? projectPath = await NonCSharpProjectFinder.FindProjectAsync(systemPath, cancellationToken);

        if (string.IsNullOrEmpty(projectPath))
            return "Error: Couldn't find a project containing this Razor file.";

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            projectPath, targetFilePath: systemPath, cancellationToken: cancellationToken);

        // Build the Razor source map
        var sourceMap = await ProjectIndexCacheService.GetRazorSourceMapAsync(project, cancellationToken);

        // Find the marked position in the Razor file
        string fileText = await File.ReadAllTextAsync(systemPath, cancellationToken);
        var matches = MarkupSymbolResolver.FindAllOccurrences(fileText, markup!.PlainText);
        if (matches.Count != 1)
            return matches.Count == 0
                ? $"Error: Snippet '{markup.PlainText}' not found in {Path.GetFileName(systemPath)}."
                : $"Error: Snippet '{markup.PlainText}' found {matches.Count} times — use a longer snippet.";

        // Compute the marked position's line in the Razor file (1-indexed)
        int markedFileOffset = MarkupSymbolResolver.MapSnippetOffsetToFile(
            fileText, matches[0], markup.PlainText, markup.SpanStart);
        int razorLine = 1;
        for (int i = 0; i < markedFileOffset && i < fileText.Length; i++)
        {
            if (fileText[i] == '\n')
                razorLine++;
        }

        // Map Razor line → generated C# location
        var generatedLoc = RazorSourceMappingService.MapRazorToGenerated(sourceMap, systemPath, razorLine);
        if (generatedLoc is null)
            return $"No generated C# mapping found for line {razorLine} in {Path.GetFileName(systemPath)}.";

        // Find the generated document in the compilation
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return "Error: Unable to get compilation for the project.";

        var generatedDocs = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
        var genDoc = generatedDocs.FirstOrDefault(d =>
            string.Equals(d.FilePath ?? d.Name, generatedLoc.GeneratedFilePath, StringComparison.OrdinalIgnoreCase));
        if (genDoc is null)
            return $"Error: Generated document not found: {generatedLoc.GeneratedFilePath}";

        var genText = await genDoc.GetTextAsync(cancellationToken);
        int genLineIndex = generatedLoc.GeneratedLine - 1; // 0-indexed
        if (genLineIndex < 0 || genLineIndex >= genText.Lines.Count)
            return $"Error: Generated line {generatedLoc.GeneratedLine} is out of range.";

        // Find the marked text in the generated line and resolve the symbol
        var genLineText = genText.Lines[genLineIndex].ToString();
        int markedCol = genLineText.IndexOf(markup.MarkedText, StringComparison.Ordinal);
        if (markedCol < 0)
        {
            // Fallback: search nearby lines (±3) for the marked text
            for (int delta = 1; delta <= 3; delta++)
            {
                foreach (var d in new[] { -delta, delta })
                {
                    int tryLine = genLineIndex + d;
                    if (tryLine < 0 || tryLine >= genText.Lines.Count) continue;
                    var tryText = genText.Lines[tryLine].ToString();
                    markedCol = tryText.IndexOf(markup.MarkedText, StringComparison.Ordinal);
                    if (markedCol >= 0)
                    {
                        genLineIndex = tryLine;
                        genLineText = tryText;
                        break;
                    }
                }
                if (markedCol >= 0) break;
            }

            if (markedCol < 0)
                return $"No symbol '{markup.MarkedText}' found in generated C# for line {razorLine}.";
        }

        // Get the position in the generated document
        int genOffset = genText.Lines[genLineIndex].Start + markedCol;
        var semanticModel = await genDoc.GetSemanticModelAsync(cancellationToken);
        if (semanticModel is null)
            return "Error: Unable to get semantic model for generated document.";
        var syntaxTree = await genDoc.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree is null)
            return "Error: Unable to get syntax tree for generated document.";
        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var token = root.FindToken(genOffset);

        // Try to find the symbol at this position
        ISymbol? symbol = null;
        var node = token.Parent;
        while (node is not null)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is not null) break;

            var typeInfo = semanticModel.GetTypeInfo(node, cancellationToken);
            symbol = typeInfo.Type;
            if (symbol is not null) break;

            var declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (declaredSymbol is not null) { symbol = declaredSymbol; break; }

            node = node.Parent;
        }

        if (symbol is null)
            return $"No symbol found for '{markup.MarkedText}' in Razor file.";

        return await GoToDefinitionTool.FormatDefinitionAsync(symbol, project, contextLines, cancellationToken);
    }
}
