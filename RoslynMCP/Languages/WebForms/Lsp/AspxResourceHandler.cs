using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebConfig.Core;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore.Models;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;
using CompletionList = RoslynMCP.Lsp.Protocol.CompletionList;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;
using Protocol = RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.WebForms.Lsp;

/// <summary>
/// The resource surface of markup: <c>&lt;%$ Resources: … %&gt;</c>, <c>&lt;%$ dnnLoc: … %&gt;</c>,
/// <c>meta:resourcekey</c> and DNN's unprefixed <c>resourcekey</c>, plus the two expression
/// builders that read <c>web.config</c> rather than a <c>.resx</c>.
/// </summary>
/// <remarks>
/// None of these positions is C#. The projection emits nothing for a builder, so the fallback every
/// other markup feature has — hand the offset to Roslyn and map the answer back — resolves to
/// nothing here and the answer has to come from the resource catalog instead.
/// </remarks>
internal static class AspxResourceHandler
{
    /// <summary>RSX0003 — "Resource key '{0}' is not defined in {1}".</summary>
    private const string MissingKey = "RSX0003";

    /// <summary>What the server calls itself in every diagnostic it publishes.</summary>
    private const string DiagnosticSource = "roslyn-sense";

    /// <summary>How many files a message names before it stops being readable.</summary>
    private const int MaxNamedFiles = 3;

    /// <summary>How much of a resource value fits on a list line.</summary>
    private const int MaxInlineValue = 120;

    /// <summary>Whether this caret is one the resource surface answers.</summary>
    public static bool Handles(AspxHitKind kind) =>
        kind is AspxHitKind.ExpressionBuilderPrefix
             or AspxHitKind.ExpressionBuilderArgument
             or AspxHitKind.ResourceKeyAttribute;

    /// <summary>Whether the <c>&lt;% … %&gt;</c> region starting here is an expression builder
    /// rather than code — the one <c>&lt;%</c> form the projection never sees.</summary>
    public static bool IsExpressionBuilder(string text, int start) =>
        start >= 0 && start + 2 < text.Length && text[start + 1] == '%' && text[start + 2] == '$';

    // ---- Navigation ------------------------------------------------------------------------

    /// <summary>
    /// Every file that defines the key, in probe order — the neutral file first, then translations
    /// and customizations. A prefix navigates to the files it reads rather than to a key.
    /// </summary>
    public static async Task<LspLocation[]> DefinitionAsync(
        AspxDocument document, AspxHit hit, CancellationToken ct)
    {
        var catalog = await AspxResourceService.CatalogAsync(document.Project, ct);

        if (AspxResourceService.Reference(document, catalog, hit) is not { } reference)
            return [];

        if (reference.Form is AspxResourceForm.AppSetting or AspxResourceForm.ConnectionString)
        {
            return Setting(document, reference) is { } setting
                ? [SettingLocation(setting.Setting)]
                : [];
        }

        if (hit.Kind is AspxHitKind.ExpressionBuilderPrefix)
        {
            var files = new List<LspLocation>();

            foreach (var family in reference.Families)
            {
                foreach (var file in family.Files)
                    files.Add(FileStart(file.FilePath));
            }

            return [.. files];
        }

        var locations = new List<LspLocation>();

        foreach (var match in AspxResourceService.Matches(reference))
        {
            if (EntryLocation(match) is { } location)
                locations.Add(location);
        }

        return [.. locations];
    }

    // ---- Hover -----------------------------------------------------------------------------

    public static async Task<Hover?> HoverAsync(AspxDocument document, AspxHit hit, CancellationToken ct)
    {
        var catalog = await AspxResourceService.CatalogAsync(document.Project, ct);

        if (AspxResourceService.Reference(document, catalog, hit) is not { } reference)
            return null;

        string? markdown = hit.Kind is AspxHitKind.ExpressionBuilderPrefix
            ? DescribePrefix(document, reference)
            : DescribeKey(document, reference);

        return markdown is null
            ? null
            : new Hover(new MarkupContent("markdown", markdown),
                AspxLanguageHandler.ToRange(document, hit.Span));
    }

    /// <summary>What the builder reads — the files, not a value, because the prefix names no
    /// key.</summary>
    private static string DescribePrefix(AspxDocument document, AspxResourceReference reference)
    {
        var markdown = new StringBuilder($"**{reference.Prefix}** — expression builder");

        if (reference.Form is AspxResourceForm.AppSetting or AspxResourceForm.ConnectionString)
        {
            string section = reference.Form is AspxResourceForm.AppSetting
                ? "appSettings"
                : "connectionStrings";
            return markdown.Append($"\n\nReads `<{section}>` from `web.config`.").ToString();
        }

        if (reference.Families.IsDefaultOrEmpty)
        {
            return markdown
                .Append($"\n\nNo resource file for this page — {Expected(document, reference)}.")
                .ToString();
        }

        markdown.Append("\n\nReads:");

        foreach (var family in reference.Families)
            markdown.Append($"\n- `{Relative(document, Path.Combine(family.Directory, family.BaseName))}.resx`");

        return markdown.ToString();
    }

    private static string DescribeKey(AspxDocument document, AspxResourceReference reference)
    {
        if (reference.Form is AspxResourceForm.AppSetting or AspxResourceForm.ConnectionString)
            return DescribeSetting(document, reference);

        // A builder with nothing written after the colon names no key, so what there is to say
        // about it is the same thing its prefix says: which files it would have read.
        if (!reference.HasKey)
            return DescribePrefix(document, reference);

        var matches = AspxResourceService.Matches(reference);

        if (matches.IsEmpty)
        {
            return reference.Families.IsDefaultOrEmpty
                ? $"**{reference.Key}** — no resource file for this page ({Expected(document, reference)})."
                : $"**{reference.Key}** — not defined in {Files(document, reference)}.";
        }

        var markdown = new StringBuilder($"**{reference.Key}** — `{Relative(document, matches[0].File.FilePath)}`");

        if (reference.Form is AspxResourceForm.ImplicitKey)
        {
            // A group's entries are what the runtime assigns, one per property, so the list is the
            // answer; a single value would be picking one of them arbitrarily.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            markdown.Append("\n");

            foreach (var match in matches)
            {
                if (seen.Add(match.Entry.Key))
                    markdown.Append($"\n- `{match.Entry.Key}` — {Inline(match.Entry.Value)}");
            }
        }
        else
        {
            markdown.Append(Fenced(matches[0].Entry.Value));
        }

        if (Elsewhere(matches) is { Length: > 0 } elsewhere)
            markdown.Append($"\n\nAlso in {elsewhere}.");

        return markdown.ToString();
    }

    private static string DescribeSetting(AspxDocument document, AspxResourceReference reference)
    {
        if (Setting(document, reference) is not { } match)
        {
            string section = reference.Form is AspxResourceForm.AppSetting
                ? "appSettings"
                : "connectionStrings";
            return $"**{reference.Key}** — no `<{section}>` entry in `web.config`.";
        }

        string? value = match.Provider ? match.Setting.Provider : match.Setting.Value;

        return $"**{reference.Key}** — `{Relative(document, match.Setting.FilePath)}`{Fenced(value)}";
    }

    /// <summary>The files that define the key besides the first, which is already named.</summary>
    private static string Elsewhere(ImmutableArray<AspxResourceMatch> matches)
    {
        var names = new List<string>();

        foreach (var match in matches)
        {
            string name = $"`{Path.GetFileName(match.File.FilePath)}`{Qualifier(match.File)}";

            if (!match.File.FilePath.Equals(matches[0].File.FilePath, StringComparison.OrdinalIgnoreCase)
                && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return string.Join(", ", names);
    }

    private static string Qualifier(ResourceFileIndex file) => (file.Culture, file.OverrideTag) switch
    {
        (null, null) => string.Empty,
        ({ } culture, null) => $" ({culture.Name})",
        (null, { } tag) => $" ({tag})",
        ({ } culture, { } tag) => $" ({culture.Name}, {tag})",
    };

    // ---- Completion ------------------------------------------------------------------------

    /// <summary>
    /// Inside a <c>&lt;%$ … %&gt;</c>: the builder prefixes before the colon, and after it the keys
    /// the prefix's own files declare.
    /// </summary>
    /// <remarks>
    /// Scanned out of the raw text rather than read off the tree, for the same reason
    /// <see cref="AspxCompletionContextScanner"/> exists: a builder being typed has no closing
    /// <c>%&gt;</c>, which is exactly the state the parser cannot represent.
    /// </remarks>
    public static async Task<CompletionList> BuilderKeysAsync(
        AspxDocument document, AspxCompletionContext context, int offset, CancellationToken ct)
    {
        if (ScanBuilder(document.Text, context.TagStart, offset) is not { } caret)
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, caret.ReplaceSpan);

        if (caret.InPrefix)
            return Prefixes(document, range);

        var catalog = await AspxResourceService.CatalogAsync(document.Project, ct);
        int comma = caret.Argument.LastIndexOf(',');

        // Past a comma the first argument has already named a global class, so only its own keys
        // are candidates; before one, either half of the two shapes is still being written.
        if (comma >= 0)
        {
            var reference = AspxResourceService.Builder(
                document, catalog, caret.Prefix, caret.Argument[..comma] + ",");

            return reference is null ? Empty : Keys(reference.Families, range, strip: false);
        }

        if (AspxResourceService.Builder(document, catalog, caret.Prefix, string.Empty) is not { } single)
            return Empty;

        var items = new List<CompletionItem>();

        if (single.Form is AspxResourceForm.AppSetting or AspxResourceForm.ConnectionString)
        {
            var settings = single.Form is AspxResourceForm.AppSetting
                ? Settings(document, WebConfigSection.AppSettings)
                : Settings(document, WebConfigSection.ConnectionStrings);

            foreach (var setting in settings)
                items.Add(Item(setting.Name, LspCompletionItemKind.Value, Inline(setting.Value), "0", range));

            return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
        }

        // A lone `Resources` argument may still become the class half of `Class, Key`, so the
        // classes are offered beside the local keys rather than instead of them.
        if (caret.Prefix.Equals("Resources", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var family in AspxResourceService.GlobalClasses(catalog))
            {
                items.Add(Item(
                    family.BaseName, LspCompletionItemKind.Class,
                    Relative(document, family.Directory), "0", range));
            }
        }

        items.AddRange(Keys(single.Families, range, strip: DnnKeys(single)).Items);

        return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// The base keys a <c>meta:resourcekey</c> may name: the groups the page's resource file
    /// already declares, filtered to the ones whose suffixes are properties this control has.
    /// </summary>
    public static async Task<CompletionList> ImplicitKeysAsync(
        AspxDocument document, AspxCompletionContext context,
        INamedTypeSymbol? control, CancellationToken ct)
    {
        var catalog = await AspxResourceService.CatalogAsync(document.Project, ct);

        var reference = AspxResourceService.Implicit(
            document, catalog, context.AttributeName ?? "meta:resourcekey", string.Empty, null);

        var properties = Properties(control);
        var suffixes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (string key in AspxResourceService.Keys(reference.Families))
        {
            int dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1)
                continue;

            string suffix = key[(dot + 1)..];
            if (properties is not null && !properties.Contains(suffix))
                continue;

            string group = key[..dot];

            if (!suffixes.TryGetValue(group, out var declared))
            {
                suffixes[group] = declared = [];
                order.Add(group);
            }

            declared.Add(suffix);
        }

        if (order.Count == 0)
            return Empty;

        var range = AspxLanguageHandler.ToRange(document, context.ReplaceSpan);
        var items = new List<CompletionItem>(order.Count);

        foreach (string group in order.Order(StringComparer.OrdinalIgnoreCase))
            items.Add(Item(group, LspCompletionItemKind.Value, string.Join(", ", suffixes[group]), "0", range));

        return new CompletionList(false, [.. items]);
    }

    /// <summary>The prefixes this project has a builder for.</summary>
    private static CompletionList Prefixes(AspxDocument document, LspRange range)
    {
        var items = new List<CompletionItem>
        {
            Item("Resources", LspCompletionItemKind.Module, "App_GlobalResources or the page's own file", "0", range),
            Item("AppSettings", LspCompletionItemKind.Module, "web.config <appSettings>", "1", range),
            Item("ConnectionStrings", LspCompletionItemKind.Module, "web.config <connectionStrings>", "1", range),
        };

        // Offered only where the builder is registered: `dnnLoc` in a stock ASP.NET project is a
        // runtime parser error, not an option.
        if (document.Compilation.GetTypeByMetadataName(
                "DotNetNuke.Services.Localization.LocalizationExpressionBuilder") is not null)
        {
            items.Insert(1, Item("dnnLoc", LspCompletionItemKind.Module, "the page's App_LocalResources file", "0", range));
        }

        return new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// Every key the families declare. <paramref name="strip"/> drops DNN's <c>.Text</c>, which the
    /// runtime appends rather than the author writing it.
    /// </summary>
    private static CompletionList Keys(
        ImmutableArray<ResourceFamily> families, LspRange range, bool strip)
    {
        const string textSuffix = ".Text";

        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in AspxResourceService.Keys(families))
        {
            string label = strip && key.EndsWith(textSuffix, StringComparison.Ordinal)
                ? key[..^textSuffix.Length]
                : key;

            if (label.Length > 0 && seen.Add(label))
                items.Add(Item(label, LspCompletionItemKind.Value, null, "1", range));
        }

        return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
    }

    private static bool DnnKeys(AspxResourceReference reference) =>
        reference.Prefix.Equals("dnnLoc", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string>? Properties(INamedTypeSymbol? control)
    {
        if (control is null)
            return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in AspxCatalog.WritableProperties(control))
            names.Add(property.Name);

        return names;
    }

    private static CompletionItem Item(
        string label, int kind, string? detail, string sort, LspRange range) =>
        new(label, kind, detail, sort + label, label, new TextEdit(range, label));

    private static readonly CompletionList Empty = new(false, []);

    /// <summary>Where a caret sits inside a half-typed <c>&lt;%$ … %&gt;</c>.</summary>
    /// <param name="Prefix">The prefix as written so far.</param>
    /// <param name="Argument">The argument text up to the caret.</param>
    /// <param name="ReplaceSpan">What a committed item replaces.</param>
    /// <param name="InPrefix">Whether the caret is still before the colon.</param>
    private readonly record struct BuilderCaret(
        string Prefix, string Argument, TextSpan ReplaceSpan, bool InPrefix);

    private static BuilderCaret? ScanBuilder(string text, int start, int offset)
    {
        // Still inside the `<%$` itself, where a committed item would land outside the range it
        // claimed to replace.
        if (start < 0 || offset < start + "<%$".Length)
            return null;

        int end = text.IndexOf("%>", start, StringComparison.Ordinal);
        if (end < 0)
            end = text.Length;
        if (offset > end)
            return null;

        int i = Math.Min(start + "<%$".Length, end);
        while (i < end && text[i] is ' ' or '\t')
            i++;

        int prefixStart = i;
        while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        int prefixEnd = i;
        string prefix = text[prefixStart..prefixEnd];

        while (i < end && text[i] is ' ' or '\t')
            i++;

        if (i >= end || text[i] != ':' || offset <= prefixEnd)
        {
            return new BuilderCaret(
                prefix, string.Empty, TextSpan.FromBounds(prefixStart, prefixEnd), InPrefix: true);
        }

        int argumentStart = i + 1;
        if (offset < argumentStart)
            return null;

        string argument = text[argumentStart..offset];

        // The token the caret is in, which is the whole argument for a one-argument builder and
        // the part after the last comma for `Class, Key`.
        int tokenStart = argumentStart + argument.LastIndexOf(',') + 1;
        while (tokenStart < offset && text[tokenStart] is ' ' or '\t')
            tokenStart++;

        int tokenEnd = offset;
        while (tokenEnd < end && text[tokenEnd] is not (',' or ' ' or '\t' or '\r' or '\n'))
            tokenEnd++;

        return new BuilderCaret(
            prefix, argument, TextSpan.FromBounds(tokenStart, Math.Max(tokenStart, tokenEnd)),
            InPrefix: false);
    }

    // ---- Diagnostics -----------------------------------------------------------------------

    /// <summary>
    /// Keys the file names that no <c>.resx</c> in their probe order defines.
    /// </summary>
    /// <remarks>
    /// Off unless <c>resources.missingKeyDiagnostic</c> asks for it, and then silent unless there is
    /// something to compare against. A page with no resource file beside it is not localized rather
    /// than broken, an unregistered builder prefix means nothing here, and a configuration builder
    /// has no family to be missing from — reporting any of those is the false positive that gets the
    /// whole rule switched off.
    /// </remarks>
    public static async Task<Protocol.Diagnostic[]> DiagnosticsAsync(
        AspxDocument document, CancellationToken ct)
    {
        if (!AspxResourceService.MissingKeyDiagnostic || document.Tree is not { } root)
            return [];

        var catalog = await AspxResourceService.CatalogAsync(document.Project, ct);
        if (catalog.Families.IsDefaultOrEmpty)
            return [];

        var diagnostics = new List<Protocol.Diagnostic>();

        foreach (var (prefix, argument, _) in AspxResourceService.Builders(root))
        {
            ct.ThrowIfCancellationRequested();

            Report(document, diagnostics,
                AspxResourceService.Builder(document, catalog, prefix.Value.Trim(), argument.Value.Trim()),
                AspxSymbolResolver.Span(argument.Range));
        }

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var (key, value) in element.RawAttributes)
            {
                if (value.Kind is not AttributeValueKind.Literal
                    || !AspxSymbolResolver.IsImplicitKeyAttribute(key.Value))
                {
                    continue;
                }

                Report(document, diagnostics,
                    AspxResourceService.Implicit(document, catalog, key.Value, value.Value, element),
                    AspxSymbolResolver.Span(value.Range));
            }
        }

        return [.. diagnostics];
    }

    private static void Report(
        AspxDocument document, List<Protocol.Diagnostic> diagnostics,
        AspxResourceReference? reference, TextSpan span)
    {
        if (reference is not { Form: AspxResourceForm.Key or AspxResourceForm.ImplicitKey } resource
            || !resource.HasKey
            || resource.Families.IsDefaultOrEmpty
            || !AspxResourceService.Matches(resource).IsEmpty)
        {
            return;
        }

        string message = resource.Form is AspxResourceForm.ImplicitKey
            ? $"No resource keys named '{resource.Key}.*' in {Files(document, resource)}."
            : $"Resource key '{resource.Key}' is not defined in {Files(document, resource)}.";

        diagnostics.Add(new Protocol.Diagnostic(
            AspxLanguageHandler.ToRange(document, span),
            LspConverters.ToLspSeverity(DiagnosticSeverity.Warning),
            MissingKey,
            DiagnosticSource,
            message));
    }

    // ---- Shared plumbing -------------------------------------------------------------------

    /// <summary>The merged section a markup file sees, from the config files above it.</summary>
    private static ImmutableArray<WebConfigEntry> Settings(
        AspxDocument document, WebConfigSection section) =>
        WebConfigSettings.Merged(document.FilePath, document.Project.FilePath, section);

    private static (WebConfigEntry Setting, bool Provider)? Setting(
        AspxDocument document, AspxResourceReference reference)
    {
        if (reference.Form is AspxResourceForm.ConnectionString)
        {
            return WebConfigSettings.ConnectionString(
                document.FilePath, document.Project.FilePath, reference.Key);
        }

        return WebConfigSettings.Find(
            Settings(document, WebConfigSection.AppSettings), reference.Key) is { } setting
                ? (setting, false)
                : null;
    }

    private static LspLocation SettingLocation(WebConfigEntry setting)
    {
        if (setting.NameSpan == default || ReadText(setting.FilePath) is not { } text)
            return FileStart(setting.FilePath);

        return new LspLocation(
            LspConverters.PathToUri(setting.FilePath),
            LspConverters.ToRange(text.Lines, Clamp(text, setting.NameSpan)));
    }

    /// <summary>The <c>name=</c> span of one entry, or the head of its file when the reader
    /// declined to span it.</summary>
    private static LspLocation? EntryLocation(AspxResourceMatch match)
    {
        if (ResourceCatalogService.Text(match.File.FilePath) is not { } text)
            return null;

        return match.Entry.KeySpan == default
            ? FileStart(match.File.FilePath)
            : new LspLocation(
                LspConverters.PathToUri(match.File.FilePath),
                LspConverters.ToRange(text.Lines, Clamp(text, match.Entry.KeySpan)));
    }

    private static LspLocation FileStart(string path) =>
        new(LspConverters.PathToUri(path), new LspRange(new Position(0, 0), new Position(0, 0)));

    private static TextSpan Clamp(SourceText text, TextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, text.Length);
        int end = Math.Clamp(span.End, start, text.Length);
        return TextSpan.FromBounds(start, end);
    }

    private static SourceText? ReadText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SourceText.From(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The files the key was looked for in, named the way the user wrote the path.</summary>
    private static string Files(AspxDocument document, AspxResourceReference reference)
    {
        var names = new List<string>();

        foreach (var family in reference.Families)
        {
            if (names.Count == MaxNamedFiles)
                return $"{string.Join(", ", names)} and {reference.Families.Length - MaxNamedFiles} more";

            names.Add($"`{Relative(document, Path.Combine(family.Directory, family.BaseName))}.resx`");
        }

        return string.Join(", ", names);
    }

    /// <summary>Where the file would have been, for a page that has none.</summary>
    private static string Expected(AspxDocument document, AspxResourceReference reference) =>
        reference.GlobalClass is { Length: > 0 } className
            ? $"no `App_GlobalResources/{className}.resx`"
            : $"no `App_LocalResources/{Path.GetFileName(document.FilePath)}.resx`";

    private static string Relative(AspxDocument document, string path)
    {
        if (Path.GetDirectoryName(document.Project.FilePath) is not { Length: > 0 } root)
            return path;

        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>A value on one line, for a list entry. Null means the entry is not a string —
    /// a <c>ResXFileRef</c> or a serialized object — which is a fact about the key, not a gap.</summary>
    private static string Inline(string? value)
    {
        if (value is null)
            return "_not a string_";
        if (value.Length == 0)
            return "_empty_";

        string collapsed = string.Join(
            ' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length <= MaxInlineValue ? collapsed : collapsed[..MaxInlineValue] + "…";
    }

    /// <summary>A value as its own block, because resource strings carry markup and newlines and
    /// a paragraph would render both.</summary>
    private static string Fenced(string? value) =>
        value is null ? "\n\n_not a string entry_" : $"\n\n```\n{value}\n```";
}
