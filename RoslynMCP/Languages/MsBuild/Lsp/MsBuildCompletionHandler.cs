using RoslynMCP.Languages.MsBuild.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services.Packages;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.MsBuild.Lsp;

/// <summary>
/// Completion in a project file: what can be written where the caret is.
/// </summary>
/// <remarks>
/// <para>
/// One dispatch, on what the caret is on. Every arm is independent, and that is deliberate: the
/// NuGet arms reach a feed and the rest read tables in memory, so an arm that fails or times out
/// must not be able to take the others down with it. Returning nothing at all because one feed was
/// slow is how a completion list stops being trusted.
/// </para>
/// <para>
/// <c>null</c>, never an empty list, when there is nothing to offer. An empty list makes VS Code
/// fall back to word-based completion — every identifier it can scrape out of the buffer — which is
/// worse than no list at all, because a project file's words are XML tag names and version numbers.
/// </para>
/// </remarks>
internal static class MsBuildCompletionHandler
{
    /// <summary>
    /// How long a feed gets before the list is served without it.
    /// </summary>
    /// <remarks>
    /// Completion is a gesture the user is waiting on, so this is longer than anything on the
    /// diagnostics path would tolerate — but it is bounded, because the alternative is a menu that
    /// hangs until a socket gives up. The upstream implementation this pack was measured against
    /// awaits its feed with no timeout at all, and a slow feed there means a frozen menu.
    /// </remarks>
    private static readonly TimeSpan FeedBudget = TimeSpan.FromSeconds(3);

    /// <summary>Past this many results the client is told the list is partial and re-queries as
    /// the user keeps typing. Below it, filtering client-side is cheaper than another round trip.</summary>
    private const int IncompleteThreshold = 10;

    public static async Task<CompletionList?> CompleteAsync(CompletionParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (MsBuildDocumentCache.Get(path) is not { } document)
            return null;

        var text = document.Text;
        int offset = LspConverters.ToOffset(text, p.Position);
        var context = MsBuildContextResolver.Resolve(document, offset);

        if (context.Flags is MsBuildLocationFlags.None or MsBuildLocationFlags.Comment)
            return null;

        var range = LspConverters.ToRange(text.Lines, context.ReplaceSpan);
        var items = await ItemsAsync(document, context, range, ct);

        return items is { Count: > 0 }
            ? new CompletionList(items.Count >= IncompleteThreshold && context.IsPackageId(), [.. items])
            : null;
    }

    private static async Task<List<CompletionItem>?> ItemsAsync(
        MsBuildDocument document, MsBuildContext context, Range range, CancellationToken ct)
    {
        // NuGet first: these are the arms that can be slow, and they are the ones a project file is
        // most often opened to edit.
        if (context.IsPackageId())
            return await PackageIdsAsync(context, range, ct);

        if (context.IsPackageVersion())
            return await VersionsAsync(context, range, ct);

        if (MsBuildValueCompletion.For(document, context) is { Count: > 0 } values)
            return values.Select(v => Item(v, range)).ToList();

        if (MsBuildNameCompletion.For(document, context) is { Count: > 0 } names)
            return names.Select(v => Item(v, range)).ToList();

        return null;
    }

    private static async Task<List<CompletionItem>?> PackageIdsAsync(
        MsBuildContext context, Range range, CancellationToken ct)
    {
        string prefix = Typed(context);
        if (prefix.Length == 0)
            return null;

        var found = await Bounded(
            token => NuGetService.SearchAsync(prefix, includePrerelease: false, 0, 30, null, token),
            ct);

        if (found is null)
            return null;

        return [.. found.Results.Select((package, index) => new CompletionItem(
            package.Id,
            LspCompletionItemKind.Module,
            package.Version,
            Order(index),
            package.Id,
            new TextEdit(range, package.Id))
        {
            Documentation = Markup(package.Description),
        })];
    }

    private static async Task<List<CompletionItem>?> VersionsAsync(
        MsBuildContext context, Range range, CancellationToken ct)
    {
        // The package this version belongs to, from the same tag — which is why the element node is
        // carried rather than a flattened view of it. On a tag with no closing bracket yet there is
        // nowhere else to read it from. `Update=` as well as `Include=`, because that is how a
        // Directory.Packages.props overrides a version it did not declare.
        if ((context.Sibling("Include") ?? context.Sibling("Update")) is not { Length: > 0 } id)
            return null;

        var found = await Bounded(
            token => NuGetService.VersionsAsync(id, includePrerelease: true, refresh: false, token),
            ct);

        if (found is null)
            return null;

        // Newest first. The client sorts by sortText, so the feed's order has to be encoded there
        // or an alphabetic sort puts 1.10.0 above 1.9.0 and 2.0.0 in the middle.
        var versions = found.Results.Reverse().ToList();

        return [.. versions.Select((version, index) => new CompletionItem(
            version,
            LspCompletionItemKind.Value,
            index == 0 ? "latest" : null,
            Order(index),
            version,
            new TextEdit(range, version)))];
    }

    /// <summary>
    /// Runs a feed call under a deadline, and turns every way it can fail into "no answer".
    /// </summary>
    /// <remarks>
    /// The distinction that matters is between this and the diagnostics path. Here, not knowing
    /// means offering nothing, which costs the user a list. There, not knowing must never be
    /// reported as "this package does not exist", which would cost them a red squiggle on a valid
    /// reference — so that path reads feed health explicitly rather than collapsing it like this.
    /// </remarks>
    private static async Task<FeedResults<T>?> Bounded<T>(
        Func<CancellationToken, Task<FeedResults<T>>> call, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(FeedBudget);

        try
        {
            return await call(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ours, not the client's: the feed took too long.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[MsBuild] Package completion failed: {ex.Message}");
            return null;
        }
    }

    private static CompletionItem Item(MsBuildValue value, Range range) =>
        new(value.Value,
            LspCompletionItemKind.Value,
            value.Detail,
            value.Value,
            value.Value,
            new TextEdit(range, value.Value))
        {
            Documentation = Markup(value.Documentation),
        };

    private static MarkupContent? Markup(string? text) =>
        text is { Length: > 0 } ? new MarkupContent("markdown", text) : null;

    /// <summary>
    /// Preserves a server-side order against a client that sorts lexically.
    /// </summary>
    /// <remarks>
    /// Four digits, not three. A package with more than a thousand versions is not hypothetical —
    /// and at three digits <c>1000</c> sorts above <c>999</c>, which puts the oldest release at the
    /// top of the list exactly for the packages with the most history.
    /// </remarks>
    private static string Order(int index) => index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    private static string Typed(MsBuildContext context) =>
        context.Attribute?.Value is { Length: > 0 } value ? value : string.Empty;
}
