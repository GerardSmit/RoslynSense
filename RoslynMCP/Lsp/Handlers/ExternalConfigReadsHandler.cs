using System.Text.Json.Serialization;
using RoslynMCP.Languages.AppSettings.Core;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using RoslynMCP.Services.MetadataConfiguration;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>Which settings entry the external reads were asked for.</summary>
public sealed record ExternalConfigReadsParams(
    [property: JsonPropertyName("textDocument")] Protocol.TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

/// <summary>
/// roslynSense/externalConfigReads (custom): where a setting is read from outside the solution's
/// source.
/// </summary>
/// <remarks>
/// Decompiling is deferred to this request rather than done while counting, which is the same
/// bargain the inheritance markers strike. The count comes from metadata and costs nothing; the
/// location costs a decompilation per assembly, and a settings file with thirty externally-read
/// keys would pay it thirty times over for lenses nobody clicked.
/// </remarks>
internal static class ExternalConfigReadsHandler
{
    /// <summary>Enough to fill a peek. A key read by more assemblies than this is a key whose
    /// exact list is not the question being asked.</summary>
    private const int MaxDecompilations = 10;

    public static async Task<LspLocation[]> ReadsAsync(
        ExternalConfigReadsParams p, CancellationToken ct)
    {
        string filePath = LspConverters.UriToPath(p.TextDocument.Uri);
        var position = new Protocol.Position(p.Line, p.Character);

        var reads = WebConfigFile.IsConfigPath(filePath)
            ? await WebConfigReadsAsync(filePath, position, ct)
            : await AppSettingsReadsAsync(filePath, position, ct);

        var locations = new List<LspLocation>();

        foreach (var read in reads.Take(MaxDecompilations))
        {
            ct.ThrowIfCancellationRequested();

            if (await DecompiledSourceService.TryDecompileTypeToFileAsync(
                    read.AssemblyPath, read.TypeName, ct) is not { } decompiled)
            {
                continue;
            }

            // The type declaration is where the decompiler puts you; the call is what was asked
            // for. The key is in the decompiled text verbatim, so the read can be found in it.
            var (line, character) = LiteralPosition(decompiled.FilePath, read, ct)
                ?? (decompiled.Line, decompiled.Character);

            var start = new Protocol.Position(line, character);

            locations.Add(new LspLocation(
                LspConverters.PathToUri(decompiled.FilePath),
                new Protocol.Range(start, start)));
        }

        return [.. locations];
    }

    /// <summary>The read's own line in the decompiled file, or null when it cannot be found
    /// there — an inlined or otherwise reshaped body still deserves to open somewhere.</summary>
    private static (int Line, int Character)? LiteralPosition(
        string filePath, MetadataConfigurationRead read, CancellationToken ct)
    {
        try
        {
            return SourceMemberLocator.FindLiteral(
                File.ReadAllText(filePath), read.Literal, read.MethodName, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<MetadataConfigurationRead>> AppSettingsReadsAsync(
        string filePath, Protocol.Position position, CancellationToken ct)
    {
        if (await AppSettingsWorkspace.GetAsync(filePath, ct) is not { Project: { } project } view)
            return [];

        int offset = LspConverters.ToOffset(view.Text, position);

        if (view.Document.KeyAt(offset) is not { } key)
            return [];

        var index = await MetadataConfigurationIndex.GetAsync(project, ct);
        return [.. index.ReadsFor(MetadataConfigurationKind.Path, key.Path)];
    }

    private static async Task<IReadOnlyList<MetadataConfigurationRead>> WebConfigReadsAsync(
        string filePath, Protocol.Position position, CancellationToken ct)
    {
        if (await WebConfigWorkspace.GetAsync(filePath, ct) is not { Project: { } project } view)
            return [];

        int offset = LspConverters.ToOffset(view.Text, position);

        if (view.Document.EntryAt(offset) is not { } entry)
            return [];

        var index = await MetadataConfigurationIndex.GetAsync(project, ct);
        return [.. index.ReadsFor(WebConfigMetadataReads.KindOf(entry.Section), entry.Name)];
    }
}
