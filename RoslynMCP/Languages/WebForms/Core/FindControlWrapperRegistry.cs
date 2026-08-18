using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// The discovered <c>FindControl</c> wrapper methods, readable synchronously.
/// </summary>
/// <remarks>
/// <see cref="IConfiguredStringLanguage.Detect"/> runs against every string literal on the
/// diagnostics pass and cannot await, but which wrapper names exist is only knowable by scanning
/// the project — an async question. So the scan's result is published here, keyed by assembly
/// name, and Detect reads the snapshot. A snapshot that is missing or stale costs one declined
/// claim — the async definition path re-validates against the authoritative list, so it can never
/// produce a wrong answer, only a late one.
/// </remarks>
internal static class FindControlWrapperRegistry
{
    private static readonly ConcurrentDictionary<
        string, ImmutableArray<(string MethodName, int ParamIndex, bool IsExtension)>> s_byAssembly =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> s_warming =
        new(StringComparer.Ordinal);

    /// <summary>Publishes the wrappers visible from <paramref name="assemblyName"/>'s project.</summary>
    public static void Publish(
        string assemblyName,
        IEnumerable<(string MethodName, int ParamIndex, bool IsExtension)> wrappers) =>
        s_byAssembly[assemblyName] = [.. wrappers];

    /// <summary>The last published snapshot for the compilation's assembly, or empty when no scan
    /// has completed yet.</summary>
    public static ImmutableArray<(string MethodName, int ParamIndex, bool IsExtension)> Snapshot(
        Compilation compilation) =>
        s_byAssembly.TryGetValue(compilation.Assembly.Name, out var wrappers) ? wrappers : [];

    /// <summary>
    /// Starts the wrapper scan for the compilation's project when no snapshot exists yet.
    /// Fire-and-forget by design: the caller is a synchronous detection pass that cannot wait,
    /// and the next pass reads whatever the scan published.
    /// </summary>
    public static void EnsureWarm(Compilation compilation)
    {
        string assemblyName = compilation.Assembly.Name;

        if (s_byAssembly.ContainsKey(assemblyName) || !s_warming.TryAdd(assemblyName, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var project = WorkspaceService.TryGetMostRecentSolution()?.Projects
                    .FirstOrDefault(p =>
                        p.Language == LanguageNames.CSharp
                        && string.Equals(p.AssemblyName, assemblyName, StringComparison.Ordinal));

                if (project is null)
                    return;

                // Publishes to this registry on completion.
                await ProjectIndexCacheService.GetFindControlWrappersAsync(project);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[FindControlWrapperRegistry] warm-up for '{assemblyName}' failed: {ex.Message}");
            }
            finally
            {
                // Cleared either way: on success the snapshot answers, and on failure the next
                // Detect may retry against a workspace that has loaded in the meantime.
                s_warming.TryRemove(assemblyName, out _);
            }
        });
    }
}
