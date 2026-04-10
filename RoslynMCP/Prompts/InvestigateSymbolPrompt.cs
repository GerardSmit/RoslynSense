using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMCP.Prompts;

/// <summary>
/// Generates a structured investigation workflow prompt for understanding
/// a symbol's definition, usages, and context across a project.
/// </summary>
[McpServerPromptType]
public static class InvestigateSymbolPrompt
{
    /// <summary>
    /// Produces a user-role prompt instructing the model to investigate a symbol
    /// using the available Roslyn tools: find it, go to its definition, locate usages,
    /// and summarize its role in the codebase.
    /// </summary>
    [McpServerPrompt(Name = "investigate-symbol"), Description(
        "Workflow prompt for investigating a C# symbol. " +
        "Produces step-by-step instructions to find the symbol's definition, " +
        "locate all usages, and summarize its role.")]
    public static string InvestigateSymbol(
        [Description("Path to any file in the project to search.")] string filePath,
        [Description("Name or pattern of the symbol to investigate.")] string symbolName)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(symbolName);

        return $$"""
            I need to understand the symbol `{{symbolName}}` in the project containing `{{filePath}}`.

            Please investigate it step by step:

            1. **Find the symbol** — Call `SemanticSymbolSearch` with `filePath={{filePath}}` and `query={{symbolName}}` to locate declarations matching this name.

            2. **Go to definition** — For each match found (especially if there is a primary/most relevant one), call `GoToDefinition` with an appropriate markup snippet to see its source code and context.

            3. **Find usages** — Call `FindUsages` with a markup snippet targeting the symbol to discover all references across the project.

            4. **Check structure** — Call `GetFileOutline` on the file containing the definition to show how the symbol fits within its surrounding type and namespace.

            5. **Summarize** — Provide a concise overview:
               - What the symbol is (type, method, property, etc.) and its purpose
               - Where it is defined and its accessibility
               - How many references it has and the key call sites
               - Any notable patterns (e.g., only used in tests, called from a single entry point, part of a public API)
            """;
    }
}
