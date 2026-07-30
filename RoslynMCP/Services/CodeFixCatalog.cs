using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;

namespace RoslynMCP.Services;

/// <summary>
/// Discovers Roslyn's built-in C# code fix and refactoring providers from the
/// Microsoft.CodeAnalysis.CSharp.Features assembly (referenced by the project).
/// Shared by the MCP get_code_actions tool and the LSP textDocument/codeAction handler.
/// </summary>
internal static class CodeFixCatalog
{
    private static IReadOnlyList<CodeFixProvider>? s_fixProviders;
    private static IReadOnlyList<CodeRefactoringProvider>? s_refactoringProviders;

    public static IReadOnlyList<CodeFixProvider> GetCodeFixProviders() =>
        s_fixProviders ??= LoadProviders<CodeFixProvider>();

    public static IReadOnlyList<CodeRefactoringProvider> GetRefactoringProviders() =>
        s_refactoringProviders ??= LoadProviders<CodeRefactoringProvider>();

    private static IReadOnlyList<T> LoadProviders<T>() where T : class
    {
        var providers = new List<T>();
        try
        {
            var featuresAssembly = System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");
            foreach (var type in featuresAssembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(T).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is T provider)
                        providers.Add(provider);
                }
                catch
                {
                    // Some providers require dependencies or special constructors
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeFixCatalog] Failed to load Features providers: {ex.Message}");
        }
        return providers;
    }
}
