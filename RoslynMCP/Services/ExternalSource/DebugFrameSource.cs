using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMCP.Services.ExternalSource;

/// <summary>Where a stopped external frame's source is. Line and column are 1-based, matching
/// what stack frames carry.</summary>
/// <param name="Origin">How the source was obtained: <c>embedded</c>, <c>source link</c>,
/// <c>reference source</c> or <c>decompiled</c>. Shown beside the location so a reader can judge
/// how exactly the line can be trusted.</param>
public sealed record FrameSourceResult(string FilePath, int Line, int Column, string Origin);

/// <summary>
/// Resolves a stack frame that has no source — a module, a method token and an IL offset — to a
/// file and a line, so stepping into a dependency lands in readable code instead of nowhere.
/// </summary>
/// <remarks>
/// <para>
/// The chain is the debug-time mirror of <see cref="ExternalSourceService"/>, tried in the same
/// trust order. Source the PDB carries or points at maps the IL offset through the PDB's own
/// sequence points, so the line is exact. The reference source has no offsets to map — the member
/// declaration is the closest honest answer. Decompilation always answers, and its line is exact
/// too: the decompiler emits sequence points for the text it just produced.
/// </para>
/// <para>
/// Unlike navigation, nothing here redirects to an implementation assembly: the module came from
/// the debuggee's own loader, and the token and offset are only meaningful against that exact
/// file.
/// </para>
/// </remarks>
internal static class DebugFrameSource
{
    /// <summary>Resolved frames, so stepping through the same method costs one resolution.</summary>
    private static readonly ConcurrentDictionary<
        (string Module, long Stamp, int Token, int Offset), FrameSourceResult> s_resolved = new();

    /// <summary>
    /// Methods the fetched lanes could not answer for. Keyed without the offset: a missing PDB or
    /// an absent reference file is a property of the method, not of where in it execution stopped.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Module, long Stamp, int Token), byte> s_unfetchable = new();

    /// <summary>MVIDs by module file, for engines that report modules by id rather than path.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Stamp), string?> s_mvids = new();

    /// <summary>The source position for one frame of a stopped stack, or null.</summary>
    /// <param name="methodToken">The frame's MethodDef token in <paramref name="modulePath"/>.</param>
    /// <param name="ilOffset">Where the IP is within that method's IL.</param>
    /// <param name="allowDecompile">
    /// Whether a cache miss may pay for a decompilation. The innermost frames of a stop are worth
    /// it — that is where the reader is looking — while a deep framework tail is not worth
    /// decompiling a dozen types for on every step.
    /// </param>
    public static async Task<FrameSourceResult?> TryResolveAsync(
        string modulePath, int methodToken, int ilOffset, bool allowDecompile, CancellationToken ct)
    {
        if (modulePath.Length == 0 || (methodToken >> 24) != 0x06 || (methodToken & 0xFFFFFF) == 0
            || ilOffset < 0 || !File.Exists(modulePath))
        {
            return null;
        }

        long stamp = Stamp(modulePath);
        if (s_resolved.TryGetValue((modulePath, stamp, methodToken, ilOffset), out var cached))
            return cached;

        try
        {
            var result = await ResolveCoreAsync(modulePath, stamp, methodToken, ilOffset, allowDecompile, ct)
                .ConfigureAwait(false);

            if (result is not null)
                s_resolved[(modulePath, stamp, methodToken, ilOffset)] = result;

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"External frame source lookup failed for {Path.GetFileName(modulePath)}: {ex.Message}",
                key: $"framesource:{modulePath}:{methodToken}");
            return null;
        }
    }

    private static async Task<FrameSourceResult?> ResolveCoreAsync(
        string modulePath, long stamp, int methodToken, int ilOffset, bool allowDecompile,
        CancellationToken ct)
    {
        string reflectionTypeName;
        string methodName;
        int parameterCount;
        FrameSourceResult? linked = null;

        using (var stream = File.OpenRead(modulePath))
        using (var peReader = new PEReader(stream))
        {
            var metadata = peReader.GetMetadataReader();
            var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken & 0xFFFFFF);
            if ((methodToken & 0xFFFFFF) > metadata.MethodDefinitions.Count)
                return null;

            var method = metadata.GetMethodDefinition(methodHandle);
            methodName = metadata.GetString(method.Name);
            reflectionTypeName = ReflectionTypeNameOf(metadata, method.GetDeclaringType());
            parameterCount = ParameterCountOf(metadata, method);

            if (!s_unfetchable.ContainsKey((modulePath, stamp, methodToken)))
            {
                linked = await FromPdbAsync(peReader, modulePath, methodHandle, ilOffset, ct)
                    .ConfigureAwait(false);
            }
        }

        if (linked is not null)
        {
            // The sidecar is what later lets a breakpoint set inside this file find its way
            // back to the assembly — navigation writes one for the files it fetches, and the
            // debug lane has to do the same for its own.
            ExternalSourceProject.Ensure(
                new ExternalSourceResult(
                    linked.Origin == "embedded"
                        ? ExternalSourceKind.Embedded
                        : ExternalSourceKind.SourceLink,
                    modulePath, linked.FilePath, [], Origin: null),
                reflectionTypeName);
            return linked;
        }

        if (!s_unfetchable.ContainsKey((modulePath, stamp, methodToken)))
        {
            var published = await FromReferenceSourceAsync(
                modulePath, reflectionTypeName, methodName, parameterCount, ct).ConfigureAwait(false);
            if (published is not null)
                return published;

            s_unfetchable[(modulePath, stamp, methodToken)] = 0;
        }

        if (!allowDecompile)
            return null;

        var decompiled = await DecompiledSourceService.TryDecompileFrameAsync(
            modulePath, reflectionTypeName, methodToken, ilOffset, ct).ConfigureAwait(false);

        return decompiled is not { } d
            ? null
            : new FrameSourceResult(d.FilePath, d.Line, d.Column, "decompiled");
    }

    /// <summary>
    /// The exact line, from the PDB's sequence points and the source the PDB carries or points at.
    /// </summary>
    private static async Task<FrameSourceResult?> FromPdbAsync(
        PEReader peReader, string modulePath, MethodDefinitionHandle methodHandle, int ilOffset,
        CancellationToken ct)
    {
        // No feature gate here: the PDB lookup and its embedded source are local data, exempt
        // from the network switches the same way navigation's embedded lane is. The switches are
        // honoured where the network happens — the symbol-server download inside PdbLocator, the
        // Source Link fetch inside TryResolveDocumentAsync.
        using var pdb = await PdbLocator.OpenAsync(peReader, modulePath, ct).ConfigureAwait(false);
        if (pdb is null)
            return null;

        var pdbReader = pdb.Provider.GetMetadataReader();
        int row = MetadataTokens.GetRowNumber(methodHandle);
        if (row > pdbReader.MethodDebugInformation.Count)
            return null;

        var debugInformation = pdbReader.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(row));

        var points = new List<SequencePoint>();
        foreach (var point in debugInformation.GetSequencePoints())
            points.Add(point);

        int picked = PickSequencePoint([.. points.Select(p => (p.Offset, p.IsHidden))], ilOffset);
        if (picked < 0)
            return null;

        var match = points[picked];
        var fetched = await SourceLinkService.TryResolveDocumentAsync(
            pdbReader, modulePath, match.Document, match.StartLine, ct).ConfigureAwait(false);

        if (fetched is null)
            return null;

        return new FrameSourceResult(
            fetched.FilePath, match.StartLine, Math.Max(1, match.StartColumn),
            fetched.Embedded ? "embedded" : "source link");
    }

    /// <summary>
    /// Which sequence point the IP sits in: the last non-hidden one at or before the offset, or
    /// the first non-hidden one when the IP is still ahead of all of them (a stop in a prologue).
    /// Returns an index into <paramref name="points"/>, or -1.
    /// </summary>
    internal static int PickSequencePoint(
        IReadOnlyList<(int Offset, bool IsHidden)> points, int ilOffset)
    {
        int best = -1, first = -1;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].IsHidden)
                continue;
            if (first < 0)
                first = i;
            if (points[i].Offset <= ilOffset)
                best = i;
        }
        return best >= 0 ? best : first;
    }

    /// <summary>
    /// The published snapshot, landed on the member rather than the type. There are no offsets to
    /// map — the snapshot was never compiled — so the declaration is the closest honest line.
    /// </summary>
    private static async Task<FrameSourceResult?> FromReferenceSourceAsync(
        string modulePath, string reflectionTypeName, string methodName, int parameterCount,
        CancellationToken ct)
    {
        var published = await ReferenceSourceService
            .TryResolveAsync(symbol: null, reflectionTypeName, modulePath, ct).ConfigureAwait(false);

        if (published is null)
            return null;

        ExternalSourceProject.Ensure(published, reflectionTypeName);

        var position = await MemberPositionAsync(
            published.FilePath, methodName, parameterCount, ct).ConfigureAwait(false)
            ?? (published.Primary.Line, published.Primary.Character);

        return new FrameSourceResult(
            published.FilePath, position.Line + 1, position.Column + 1, "reference source");
    }

    /// <summary>
    /// Where a file declares the member behind a metadata method name — the method itself, the
    /// constructor, or the property/event an accessor belongs to. 0-based; null when the name is
    /// compiler-generated or simply not in this file.
    /// </summary>
    internal static async Task<(int Line, int Column)?> MemberPositionAsync(
        string filePath, string methodName, int parameterCount, CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var root = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);

        var token = FindMemberToken(root, methodName, parameterCount);
        if (token is not { } found)
            return null;

        var position = found.GetLocation().GetLineSpan().StartLinePosition;
        return (position.Line, position.Character);
    }

    private static SyntaxToken? FindMemberToken(
        Microsoft.CodeAnalysis.SyntaxNode root, string methodName, int parameterCount)
    {
        // Compiler-generated names — lambdas, local functions, state machines — have no
        // declaration a reader could be pointed at.
        if (methodName.Contains('<') || methodName.Contains('$'))
            return null;

        static SyntaxToken? First(IEnumerable<SyntaxToken> tokens) =>
            tokens.Cast<SyntaxToken?>().FirstOrDefault();

        switch (methodName)
        {
            case ".ctor":
                return First(root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                    .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
                    .OrderByDescending(c => c.ParameterList.Parameters.Count == parameterCount)
                    .Select(c => c.Identifier));

            case ".cctor":
                return First(root.DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(SyntaxKind.StaticKeyword))
                    .Select(c => c.Identifier));

            case "Finalize":
                return First(root.DescendantNodes().OfType<DestructorDeclarationSyntax>()
                    .Select(d => d.Identifier));
        }

        if (Accessor(methodName) is { } accessor)
        {
            var (kind, memberName) = accessor;
            if (memberName == "Item")
            {
                return First(root.DescendantNodes().OfType<IndexerDeclarationSyntax>()
                    .Select(i => i.ThisKeyword));
            }

            return kind is "get" or "set"
                ? First(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Identifier.ValueText == memberName)
                    .Select(p => p.Identifier))
                : First(root.DescendantNodes().OfType<EventDeclarationSyntax>()
                        .Where(e => e.Identifier.ValueText == memberName)
                        .Select(e => e.Identifier))
                    ?? First(root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                        .Where(v => v.Parent?.Parent is EventFieldDeclarationSyntax
                                    && v.Identifier.ValueText == memberName)
                        .Select(v => v.Identifier));
        }

        return First(root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == methodName)
            .OrderByDescending(m => m.ParameterList.Parameters.Count == parameterCount)
            .Select(m => m.Identifier));
    }

    /// <summary>Splits an accessor name: <c>get_Length</c> → (<c>get</c>, <c>Length</c>).</summary>
    private static (string Kind, string Member)? Accessor(string methodName)
    {
        foreach (string prefix in (string[])["get_", "set_", "add_", "remove_"])
        {
            if (methodName.StartsWith(prefix, StringComparison.Ordinal)
                && methodName.Length > prefix.Length)
            {
                return (prefix[..^1], methodName[prefix.Length..]);
            }
        }
        return null;
    }

    /// <summary>The module's MVID, for engines that name modules by id rather than path.</summary>
    public static string? TryReadMvid(string modulePath)
    {
        if (modulePath.Length == 0 || !File.Exists(modulePath))
            return null;

        return s_mvids.GetOrAdd((modulePath, Stamp(modulePath)), key =>
        {
            try
            {
                using var stream = File.OpenRead(key.Path);
                using var peReader = new PEReader(stream);
                var metadata = peReader.GetMetadataReader();
                return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceLog.Warn(
                    $"Could not read MVID of {Path.GetFileName(key.Path)}: {ex.Message}",
                    key: $"mvid:{key.Path}");
                return null;
            }
        });
    }

    /// <summary>Metadata type name with nesting and namespace: <c>Ns.Outer+Inner</c>.</summary>
    internal static string ReflectionTypeNameOf(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var names = new Stack<string>();
        string @namespace = "";

        for (var current = handle; !current.IsNil;)
        {
            var definition = metadata.GetTypeDefinition(current);
            names.Push(metadata.GetString(definition.Name));

            var declaring = definition.GetDeclaringType();
            if (declaring.IsNil)
            {
                @namespace = metadata.GetString(definition.Namespace);
                break;
            }
            current = declaring;
        }

        string nested = string.Join('+', names);
        return @namespace.Length == 0 ? nested : $"{@namespace}.{nested}";
    }

    /// <summary>Declared parameter count, read from the signature blob rather than the Param
    /// table — unnamed parameters have no rows there.</summary>
    internal static int ParameterCountOf(MetadataReader metadata, MethodDefinition method)
    {
        try
        {
            var reader = metadata.GetBlobReader(method.Signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric)
                reader.ReadCompressedInteger();
            return reader.ReadCompressedInteger();
        }
        catch (BadImageFormatException)
        {
            return 0;
        }
    }

    private static long Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
