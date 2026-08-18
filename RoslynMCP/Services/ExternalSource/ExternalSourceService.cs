using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>How a dependency's source was obtained, best first.</summary>
public enum ExternalSourceKind
{
    /// <summary>Unpacked from the PDB, which carried the source itself.</summary>
    Embedded,

    /// <summary>Downloaded from where the PDB's Source Link map pointed, and checksum-verified.</summary>
    SourceLink,

    /// <summary>Read from the reference-source snapshot matching the .NET Framework version.</summary>
    ReferenceSource,

    /// <summary>Reconstructed from IL, because none of the above was available.</summary>
    Decompiled,
}

/// <summary>Where a metadata symbol's source is, and how much it can be trusted.</summary>
/// <param name="Positions">0-based. The first is where navigation should land.</param>
/// <param name="Origin">A URL or a repository and commit; null for decompiled output.</param>
public sealed record ExternalSourceResult(
    ExternalSourceKind Kind,
    string AssemblyPath,
    string FilePath,
    IReadOnlyList<LinePosition> Positions,
    string? Origin)
{
    /// <summary>Where navigation should land.</summary>
    public LinePosition Primary => Positions.Count > 0 ? Positions[0] : default;

    /// <summary>What to call this in a heading shown to a reader.</summary>
    public string Title => Kind switch
    {
        ExternalSourceKind.Embedded => "Embedded Source",
        ExternalSourceKind.SourceLink => "Source Link",
        ExternalSourceKind.ReferenceSource => "Reference Source",
        _ => "Decompiled Source",
    };

    /// <summary>
    /// How the source was established, in the terms a reader needs to judge it. The three fetched
    /// kinds are not equally strong and the wording says so: two are verified against a checksum
    /// the assembly carries, the reference source is only known to declare the symbol.
    /// </summary>
    public string Provenance => Kind switch
    {
        ExternalSourceKind.Embedded => "embedded in the assembly's PDB, checksum verified",
        ExternalSourceKind.SourceLink => $"Source Link, checksum verified — {Origin}",
        ExternalSourceKind.ReferenceSource => $"reference source, not checksum verified — {Origin}",
        _ => "auto-decompiled",
    };
}

/// <summary>
/// One answer to "where is this dependency's source", tried best-first and always answering.
/// </summary>
/// <remarks>
/// <para>
/// The order is by how much the result can be trusted, not by how cheap it is: source the PDB
/// carries, then source the PDB points at, then the published snapshot for the framework version,
/// then a decompilation. Every step is allowed to fail for its own reasons — no PDB, no map, an
/// unreachable host, a checksum that disagrees, a version with no published snapshot — and each
/// failure simply moves to the next, so navigation always lands somewhere.
/// </para>
/// <para>
/// Callers get a file path and a line rather than a Roslyn <c>Document</c>. Producing a document
/// means standing up a workspace with a full reference set, which is the expensive part of the
/// decompile path and is pure waste for a caller that only wants to show a few lines of text.
/// </para>
/// </remarks>
public static class ExternalSourceService
{
    /// <summary>The source for a symbol that has none in the solution.</summary>
    public static async Task<ExternalSourceResult?> TryResolveAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        if (SourceMemberLocator.GetOwningType(symbol) is not { } owningType)
            return null;

        string reflectionTypeName = SourceMemberLocator.GetReflectionTypeName(owningType);
        string? assemblyPath =
            await SourceMemberLocator.AssemblyPathAsync(symbol, project, ct).ConfigureAwait(false);

        if (assemblyPath is not null
            && await FetchedAsync(assemblyPath, symbol, reflectionTypeName, project, ct).ConfigureAwait(false)
                is { } fetched)
        {
            return fetched;
        }

        return await DecompiledAsync(symbol, project, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The source for a type named in an assembly, for callers that never had a symbol — the
    /// search panel resolving a metadata hit, and the editor opening a metadata document.
    /// </summary>
    public static async Task<ExternalSourceResult?> TryResolveTypeAsync(
        string assemblyPath, string reflectionTypeName, CancellationToken ct)
    {
        if (!File.Exists(assemblyPath))
            return null;

        if (await FetchedAsync(assemblyPath, symbol: null, reflectionTypeName, project: null, ct)
                .ConfigureAwait(false) is { } fetched)
        {
            return fetched;
        }

        var decompiled = await DecompiledSourceService.TryDecompileTypeToFileAsync(
            assemblyPath, reflectionTypeName, ct).ConfigureAwait(false);

        return decompiled is not { } d
            ? null
            : new ExternalSourceResult(
                ExternalSourceKind.Decompiled,
                assemblyPath,
                d.FilePath,
                [new LinePosition(d.Line, d.Character)],
                Origin: null);
    }

    /// <summary>Everything that reads real source rather than reconstructing it.</summary>
    private static async Task<ExternalSourceResult?> FetchedAsync(
        string assemblyPath,
        ISymbol? symbol,
        string reflectionTypeName,
        Project? project,
        CancellationToken ct)
    {
        var linked = symbol is not null && project is not null
            ? await SourceLinkService.TryResolveAsync(symbol, project, ct).ConfigureAwait(false)
            : await SourceLinkService.TryResolveForAssemblyAsync(assemblyPath, reflectionTypeName, ct)
                .ConfigureAwait(false);

        if (linked is not null)
        {
            return new ExternalSourceResult(
                linked.Embedded ? ExternalSourceKind.Embedded : ExternalSourceKind.SourceLink,
                assemblyPath,
                linked.FilePath,
                // Sequence points count from one; everything above the LSP counts from zero.
                [new LinePosition(Math.Max(0, linked.Line - 1), 0)],
                linked.Url);
        }

        return await ReferenceSourceService
            .TryResolveAsync(symbol, reflectionTypeName, assemblyPath, ct)
            .ConfigureAwait(false);
    }

    private static async Task<ExternalSourceResult?> DecompiledAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        var decompiled = await DecompiledSourceService
            .TryDecompileSymbolAsync(symbol, project, ct).ConfigureAwait(false);

        if (decompiled is null)
            return null;

        var positions = decompiled.Locations
            .Where(location => location.IsInSource)
            .Select(location => location.GetLineSpan().StartLinePosition)
            .ToList();

        if (positions.Count == 0)
            positions.Add(new LinePosition(0, 0));

        return new ExternalSourceResult(
            ExternalSourceKind.Decompiled,
            decompiled.AssemblyPath,
            decompiled.SourceFilePath,
            positions,
            Origin: null);
    }
}
