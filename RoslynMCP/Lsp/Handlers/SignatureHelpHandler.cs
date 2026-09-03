using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.SignatureHelp;
using RoslynMCP.Lsp.Protocol;
using LspSignatureHelp = RoslynMCP.Lsp.Protocol.SignatureHelp;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// textDocument/signatureHelp via Roslyn's internal SignatureHelpService (reached through
/// Publicizer — the whole feature is internal in Roslyn). The service instance is a MEF
/// export on the workspace host, cached per host like CodeFixCatalog's providers.
/// </summary>
internal static class SignatureHelpHandler
{
    private static readonly object s_lock = new();
    private static Microsoft.CodeAnalysis.Host.HostServices? s_cachedHost;
    private static SignatureHelpService? s_service;

    public static async Task<LspSignatureHelp?> SignatureHelpAsync(
        SignatureHelpParams p, CancellationToken ct)
    {
        var resolved = await HandlerHelpers.ResolveAsync(p.TextDocument, p.Position, ct);
        if (resolved is not var (document, _, offset))
            return null;

        return await SignatureHelpAsync(document, offset, p.Context, ct);
    }

    /// <summary>The signature-help pass over an arbitrary document and offset. Markup files go
    /// through here with the document and offset of their C# projection.</summary>
    public static async Task<LspSignatureHelp?> SignatureHelpAsync(
        Document document, int offset, SignatureHelpContext? triggerContext, CancellationToken ct)
    {
        var service = GetService(document.Project.Solution.Workspace);
        if (service is null)
            return null;

        document = await document.FreezeAsync(ct);

        var triggerInfo = triggerContext is { TriggerKind: 2, TriggerCharacter.Length: > 0 } typed
            ? new SignatureHelpTriggerInfo(
                SignatureHelpTriggerReason.TypeCharCommand, typed.TriggerCharacter[0])
            : new SignatureHelpTriggerInfo(SignatureHelpTriggerReason.InvokeSignatureHelpCommand);

        var (_, items) = await service.GetSignatureHelpAsync(document, offset, triggerInfo, ct);
        if (items is null || items.Items.Count == 0)
            return null;

        var signatures = items.Items
            .Select(item => new SignatureInformation(
                SignatureLabel(item),
                Documentation(item.DocumentationFactory, ct),
                item.Parameters
                    .Select(parameter => new ParameterInformation(
                        Text(parameter.DisplayParts),
                        Documentation(parameter.DocumentationFactory, ct)))
                    .ToArray()))
            .ToArray();

        int activeSignature = items.SelectedItemIndex is { } selected
            && selected >= 0 && selected < signatures.Length ? selected : 0;
        return new LspSignatureHelp(
            signatures,
            activeSignature,
            Math.Max(0, items.SemanticParameterIndex));
    }

    private static SignatureHelpService? GetService(Workspace workspace)
    {
        var host = workspace.Services.HostServices;
        lock (s_lock)
        {
            if (!ReferenceEquals(s_cachedHost, host))
            {
                s_service = BuildService(host);
                s_cachedHost = host;
            }
            return s_service;
        }
    }

    /// <summary>Composes the service by hand instead of exporting it from MEF: the catalog
    /// contains PythiaSignatureHelpProvider, whose IPythiaSignatureHelpProviderImplementation
    /// import only exists inside Visual Studio — resolving the service export fails outright.
    /// Materializing each provider individually lets the broken ones be skipped.</summary>
    private static SignatureHelpService? BuildService(Microsoft.CodeAnalysis.Host.HostServices host)
    {
        if (host is not IMefHostExportProvider mef)
            return null;

        // Preferred: the real MEF export (works when the Pythia stub exports are in the
        // catalog — see PythiaStubExports).
        try
        {
            if (mef.GetExports<SignatureHelpService>().FirstOrDefault()?.Value is { } exported)
                return exported;
        }
        catch (Exception)
        {
        }

        // Fallback for hosts without the stubs: compose by hand, skipping broken parts.
        try
        {
            var providers = new List<Lazy<ISignatureHelpProvider, OrderableLanguageMetadata>>();
            foreach (var lazy in mef.GetExports<ISignatureHelpProvider, OrderableLanguageMetadata>())
            {
                try
                {
                    _ = lazy.Value; // force composition now; VS-only providers throw here
                    providers.Add(lazy);
                }
                catch (Exception)
                {
                }
            }
            // The ctor is [Obsolete(error: true)] "use MEF" — MEF resolution is exactly what
            // fails here, so invoke it reflectively.
            return (SignatureHelpService?)Activator.CreateInstance(
                typeof(SignatureHelpService),
                [(IEnumerable<Lazy<ISignatureHelpProvider, OrderableLanguageMetadata>>)providers]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SignatureHelp] Service composition failed: {ex.Message}");
            return null;
        }
    }

    private static string SignatureLabel(SignatureHelpItem item)
    {
        string separator = Text(item.SeparatorDisplayParts);
        return Text(item.PrefixDisplayParts)
            + string.Join(separator, item.Parameters.Select(pa => Text(pa.DisplayParts)))
            + Text(item.SuffixDisplayParts);
    }

    private static MarkupContent? Documentation(
        Func<CancellationToken, IEnumerable<TaggedText>>? factory, CancellationToken ct)
    {
        if (factory is null)
            return null;
        try
        {
            string text = string.Concat(factory(ct).Select(t => t.Text));
            return text.Length == 0 ? null : new MarkupContent("markdown", text);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private static string Text(IEnumerable<TaggedText> parts) =>
        string.Concat(parts.Select(t => t.Text));
}
