using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host;

namespace RoslynMCP.Services;

/// <summary>
/// Discovers Roslyn's built-in C# code fix and refactoring providers from the workspace's MEF
/// composition (which includes Microsoft.CodeAnalysis.CSharp.Features). MEF satisfies the
/// providers' [ImportingConstructor] dependencies — instantiating them via Activator yields
/// inert instances that silently register nothing.
/// Shared by the MCP get_code_actions tool and the LSP textDocument/codeAction handler.
/// </summary>
internal static class CodeFixCatalog
{
    /// <summary>System.Composition metadata view for [ExportCodeFixProvider]/[ExportCodeRefactoringProvider].</summary>
    public sealed class LanguagesMetadata
    {
        public string[]? Languages { get; set; }
    }

    private static readonly object s_lock = new();
    private static HostServices? s_cachedHost;
    private static IReadOnlyList<CodeFixProvider> s_fixProviders = Array.Empty<CodeFixProvider>();
    private static IReadOnlyList<CodeRefactoringProvider> s_refactoringProviders = Array.Empty<CodeRefactoringProvider>();

    public static IReadOnlyList<CodeFixProvider> GetCodeFixProviders(Workspace workspace)
    {
        EnsureLoaded(workspace);
        return s_fixProviders;
    }

    public static IReadOnlyList<CodeRefactoringProvider> GetRefactoringProviders(Workspace workspace)
    {
        EnsureLoaded(workspace);
        return s_refactoringProviders;
    }

    private static void EnsureLoaded(Workspace workspace)
    {
        var host = workspace.Services.HostServices;
        lock (s_lock)
        {
            if (ReferenceEquals(s_cachedHost, host))
                return;

            s_fixProviders = LoadExports<CodeFixProvider>(host);
            s_refactoringProviders = LoadExports<CodeRefactoringProvider>(host);
            s_cachedHost = host;
        }
    }

    private static IReadOnlyList<T> LoadExports<T>(HostServices host) where T : class
    {
        // IMefHostExportProvider is internal, so its generic GetExports<TExtension, TMetadata>
        // is invoked via reflection. This is the only supported way to obtain MEF-composed
        // provider instances (with [ImportingConstructor] dependencies satisfied) — Activator
        // instantiation yields inert providers that silently register nothing.
        var providers = new List<T>();
        try
        {
            var mefInterface = typeof(HostServices).Assembly
                .GetType("Microsoft.CodeAnalysis.Host.Mef.IMefHostExportProvider");
            if (mefInterface is null || !mefInterface.IsInstanceOfType(host))
                return providers;

            var getExports = mefInterface.GetMethods()
                .First(m => m.Name == "GetExports" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(typeof(T), typeof(LanguagesMetadata));

            var exports = (System.Collections.IEnumerable)getExports.Invoke(host, null)!;
            foreach (object lazy in exports)
            {
                var lazyType = lazy.GetType();
                var metadata = (LanguagesMetadata?)lazyType.GetProperty("Metadata")?.GetValue(lazy);
                if (metadata?.Languages?.Contains(LanguageNames.CSharp) != true)
                    continue;

                try
                {
                    if (lazyType.GetProperty("Value")?.GetValue(lazy) is T provider)
                        providers.Add(provider);
                }
                catch (TargetInvocationException)
                {
                    // Provider's own composition failed — skip it.
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeFixCatalog] Failed to load {typeof(T).Name} exports: {ex.Message}");
        }
        return providers;
    }
}
