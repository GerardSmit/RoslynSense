using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace RoslynMCP.Services;

/// <summary>
/// Discovers Roslyn's built-in C# code fix and refactoring providers from the workspace's MEF
/// composition (which includes Microsoft.CodeAnalysis.CSharp.Features). MEF satisfies the
/// providers' [ImportingConstructor] dependencies — instantiating them via Activator yields
/// inert instances that silently register nothing. IMefHostExportProvider is internal in
/// Roslyn; accessed via Publicizer (see RoslynMCP.csproj).
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
    private static IReadOnlyList<IConfigurationFixProvider> s_configurationProviders = Array.Empty<IConfigurationFixProvider>();

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

    /// <summary>
    /// The providers behind "Suppress with #pragma", "Suppress in Suppressions.cs" and
    /// "Configure severity in .editorconfig".
    /// </summary>
    /// <remarks>
    /// A separate export from ordinary code fixes because these do not fix anything — they
    /// change what the analyzer reports. Without them there is no way out of a noisy rule from
    /// inside the editor, which is the state an unconfigured repo with StyleCop installed starts
    /// in.
    /// </remarks>
    public static IReadOnlyList<IConfigurationFixProvider> GetConfigurationFixProviders(Workspace workspace)
    {
        EnsureLoaded(workspace);
        return s_configurationProviders;
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
            s_configurationProviders = LoadExports<IConfigurationFixProvider>(host);
            s_cachedHost = host;
        }
    }

    private static IReadOnlyList<T> LoadExports<T>(HostServices host) where T : class
    {
        var providers = new List<T>();
        if (host is not IMefHostExportProvider mef)
            return providers;

        foreach (var lazy in mef.GetExports<T, LanguagesMetadata>())
        {
            if (lazy.Metadata.Languages?.Contains(LanguageNames.CSharp) != true)
                continue;

            try
            {
                providers.Add(lazy.Value);
            }
            catch (Exception)
            {
                // Provider's own composition failed — skip it.
            }
        }
        return providers;
    }
}
