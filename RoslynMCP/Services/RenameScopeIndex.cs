using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.VisualBasic;

namespace RoslynMCP.Services;

/// <summary>Uses validated evaluated project data to narrow non-cascading member renames.</summary>
internal static class RenameScopeIndex
{
    internal static bool CanNarrow(ISymbol symbol)
    {
        if (symbol is IParameterSymbol parameter) symbol = parameter.ContainingSymbol;
        // A non-virtual member on a sealed type with no interfaces cannot acquire an
        // interface relationship through an unloaded derived class. Other hierarchies
        // retain the full solution search, including independent implementations.
        return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol
            && !symbol.IsVirtual && !symbol.IsOverride && !symbol.IsAbstract
            && symbol.ContainingType is { IsSealed: true } type
            && type.TypeKind is TypeKind.Class or TypeKind.Struct
            && type.AllInterfaces.IsEmpty;
    }

    public static IReadOnlyList<string>? TryNarrow(Project origin, ISymbol symbol,
        string solutionPath, IReadOnlyList<string> projects, CancellationToken ct)
    {
        if (!CanNarrow(symbol)) return null;
        try
        {
            var infos = new Dictionary<string, ImmutableArray<ProjectFileInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in projects)
            {
                ct.ThrowIfCancellationRequested();
                var properties = WorkspaceBuildProperties.Create(false, solutionPath).ToImmutableDictionary();
                if (!EvaluationCache.TryGet(path, properties, out var entries, out _))
                {
                    properties = WorkspaceBuildProperties.Create(true, solutionPath).ToImmutableDictionary();
                    if (!EvaluationCache.TryGet(path, properties, out entries, out _)) return null;
                }
                if (entries.IsDefaultOrEmpty || entries.Any(e => e.IsEmpty)) return null;
                infos[path] = entries;
            }
            return Select(origin, symbol, infos, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // Incomplete graph information must never imply no consumers.
        }
    }

    private static IReadOnlyList<string>? Select(Project origin, ISymbol symbol,
        Dictionary<string, ImmutableArray<ProjectFileInfo>> infos, CancellationToken ct)
    {
        var sourcePaths = symbol.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owners = infos.Where(pair => pair.Value.Any(info => !info.Documents.IsDefault
            && info.Documents.Any(d => sourcePaths.Contains(d.FilePath)))).Select(pair => pair.Key).ToArray();
        // Linked declarations can have different shapes under another project's defines.
        if (owners.Length != 1) return null;
        var outputs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entries) in infos)
        foreach (var info in entries)
        foreach (var output in new[] { info.OutputFilePath, info.OutputRefFilePath, info.IntermediateOutputFilePath })
        {
            if (string.IsNullOrEmpty(output)) continue;
            string name = Path.GetFileNameWithoutExtension(output);
            if (!outputs.TryGetValue(name, out var producers)) outputs[name] = producers = [];
            producers.Add(path);
        }
        var references = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entries) in infos)
        {
            ct.ThrowIfCancellationRequested();
            var dependencies = references[path] = new(StringComparer.OrdinalIgnoreCase);
            string directory = Path.GetDirectoryName(path)!;
            foreach (var info in entries)
            {
                if (!info.ProjectReferences.IsDefault)
                foreach (var reference in info.ProjectReferences)
                {
                    string target = Path.GetFullPath(reference.Path, directory);
                    if (!infos.ContainsKey(target)) return null;
                    dependencies.Add(target);
                }
                // Binary references to solution outputs must count too. Matching the assembly
                // file name conservatively includes copies in another output directory.
                var args = info.CommandLineArgs.IsDefault ? [] : info.CommandLineArgs;
                CommandLineArguments parsed;
                if (info.Language == LanguageNames.CSharp)
                    parsed = CSharpCommandLineParser.Default.Parse(args, directory, sdkDirectory: null);
                else if (info.Language == LanguageNames.VisualBasic)
                    parsed = VisualBasicCommandLineParser.Default.Parse(args, directory, sdkDirectory: null);
                else return null;
                if (parsed.Errors.Any(e => e.Severity == DiagnosticSeverity.Error)) return null;
                var metadata = parsed.MetadataReferences.Select(r => r.Reference);
                if (!info.MetadataReferences.IsDefault)
                    metadata = metadata.Concat(info.MetadataReferences.Select(r => r.Path));
                foreach (string reference in metadata)
                    if (outputs.TryGetValue(Path.GetFileNameWithoutExtension(reference), out var producers))
                        dependencies.UnionWith(producers);
            }
        }
        var reached = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var (path, dependencies) in references)
                if (dependencies.Overlaps(reached)) changed |= reached.Add(path);
        } while (changed);
        if (origin.FilePath is { } originPath) reached.Add(originPath);
        return infos.Keys.Where(reached.Contains).ToArray();
    }
}
