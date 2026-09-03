using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using WebFormsCore.Models;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>What a resource-carrying position in markup names.</summary>
internal enum AspxResourceForm
{
    /// <summary>One key, looked up in a family and whatever the prefix falls back to.</summary>
    Key,

    /// <summary>An implicit-localization key: a prefix whose real keys are
    /// <c>{key}.{property}</c>, one per localizable property of the control it is written on.</summary>
    ImplicitKey,

    /// <summary>An <c>&lt;appSettings&gt;</c> entry, which lives in <c>web.config</c> and not in a
    /// <c>.resx</c> at all.</summary>
    AppSetting,

    /// <summary>A <c>&lt;connectionStrings&gt;</c> entry.</summary>
    ConnectionString,
}

/// <summary>What a markup position asks a resource file for, and which files answer.</summary>
/// <param name="Form">Whether the key is a key, a group of them, or a configuration entry.</param>
/// <param name="Prefix">The builder prefix or attribute name as written.</param>
/// <param name="Written">The argument as written — what a completion item replaces.</param>
/// <param name="Key">The key the runtime looks up, with DNN's <c>.Text</c> already applied where
/// the prefix applies it.</param>
/// <param name="Families">The families probed, in the order the runtime probes them.</param>
/// <param name="GlobalClass">The global resource class the first argument named, when there was
/// one.</param>
/// <param name="Control">The control an implicit key localizes; its settable properties are the
/// suffixes the key's real entries carry.</param>
internal sealed record AspxResourceReference(
    AspxResourceForm Form,
    string Prefix,
    string Written,
    string Key,
    ImmutableArray<ResourceFamily> Families,
    string? GlobalClass = null,
    INamedTypeSymbol? Control = null)
{
    /// <summary>False for a half-written builder — <c>&lt;%$ Resources: %&gt;</c> — which is a
    /// keystroke rather than a mistake.</summary>
    public bool HasKey => Written.Length > 0 && Key.Length > 0;
}

/// <summary>One file of one family that defines the key.</summary>
internal readonly record struct AspxResourceMatch(ResourceFileIndex File, ResourceEntry Entry);

/// <summary>
/// The resource files a markup position reads: the expression builders <c>&lt;%$ Resources: … %&gt;</c>
/// and <c>&lt;%$ dnnLoc: … %&gt;</c>, and the implicit-localization attributes
/// <c>meta:resourcekey</c> and DNN's unprefixed <c>resourcekey</c>.
/// </summary>
/// <remarks>
/// The whole family answers, never one winner. Which <c>.resx</c> a request lands in is a function
/// of the portal id, the thread culture and a database-configured fallback locale, and none of the
/// three exists in an editor — so navigation offers every file that defines the key in precedence
/// order, hover says which ones those are, and a diagnostic fires only when none of them does.
/// </remarks>
internal static class AspxResourceService
{
    /// <summary>ASP.NET's per-page resource folder, beside the markup file.</summary>
    private const string LocalFolder = "App_LocalResources";

    /// <summary>ASP.NET's application-wide resource folder, at the application root — which for a
    /// project-based workspace is the directory holding the project file.</summary>
    private const string GlobalFolder = "App_GlobalResources";

    /// <summary>The file DNN falls back to, both beside the page and at the application root.</summary>
    private const string SharedBaseName = "SharedResources";

    private const string ResourcesPrefix = "Resources";
    private const string DnnPrefix = "dnnLoc";
    private const string AppSettingsPrefix = "AppSettings";
    private const string ConnectionStringsPrefix = "ConnectionStrings";

    /// <summary>
    /// The project's resource families, or an empty catalog when the resources pack is not
    /// registered — <c>--no-resources</c> has to switch this surface off along with the pack, and
    /// pack identity is the only thing that carries that decision.
    /// </summary>
    public static Task<ResourceCatalog> CatalogAsync(Project project, CancellationToken ct) =>
        Pack() is { } resources
            ? resources.CatalogAsync(project, ct)
            : Task.FromResult(ResourceCatalog.Empty);

    /// <summary>
    /// Whether the missing-key rule is switched on. It ships off, and markup answers to the same
    /// switch the C# surface does: one false "this key does not exist" on a key that resolves fine
    /// at runtime is what gets a rule turned off wholesale.
    /// </summary>
    public static bool MissingKeyDiagnostic => Pack()?.Settings.MissingKeyDiagnostic ?? false;

    private static ResourcesLanguage? Pack()
    {
        foreach (var pack in LanguageRegistry.Current.Packs)
        {
            if (pack is ResourcesLanguage resources)
                return resources;
        }

        return null;
    }

    /// <summary>The resource a resolved caret names, or <c>null</c> when it names none.</summary>
    public static AspxResourceReference? Reference(
        AspxDocument document, ResourceCatalog catalog, AspxHit hit)
    {
        if (document.Tree is not { } root)
            return null;

        if (hit.Kind is AspxHitKind.ResourceKeyAttribute)
        {
            return ImplicitKeyAttributeName(hit) is { } attribute
                ? Implicit(document, catalog, attribute, hit.Name ?? string.Empty, hit.Element)
                : null;
        }

        if (hit.Kind is not (AspxHitKind.ExpressionBuilderPrefix or AspxHitKind.ExpressionBuilderArgument))
            return null;

        // The hit carries one half of the builder, and both halves are needed: the prefix decides
        // how the argument is read, and the argument decides which files the prefix reaches.
        foreach (var (prefix, argument, _) in Builders(root))
        {
            var half = hit.Kind is AspxHitKind.ExpressionBuilderPrefix ? prefix : argument;

            if (AspxSymbolResolver.Span(half.Range) == hit.Span)
                return Builder(document, catalog, prefix.Value.Trim(), argument.Value.Trim());
        }

        return null;
    }

    /// <summary>What a <c>&lt;%$ Prefix: Argument %&gt;</c> reads, or <c>null</c> for a prefix
    /// nothing here knows — an unregistered builder is silence, never a guess.</summary>
    public static AspxResourceReference? Builder(
        AspxDocument document, ResourceCatalog catalog, string prefix, string argument)
    {
        if (prefix.Equals(ResourcesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Two arguments name a class under App_GlobalResources; one is the page's own local
            // file. That is the whole difference between the global and the local stock form.
            int comma = argument.IndexOf(',');

            if (comma < 0)
            {
                return new AspxResourceReference(
                    AspxResourceForm.Key, prefix, argument, argument, Local(document, catalog));
            }

            string className = argument[..comma].Trim();
            string key = argument[(comma + 1)..].Trim();

            return new AspxResourceReference(
                AspxResourceForm.Key, prefix, argument, key,
                Global(document, catalog, className), GlobalClass: className);
        }

        if (prefix.Equals(DnnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // The builder hands the key straight to Localization.GetString against the containing
            // file's own local resource file, so it inherits both of that method's habits: the
            // default ".Text" property and the fall through to the shared files.
            return new AspxResourceReference(
                AspxResourceForm.Key, prefix, argument, WithDefaultSuffix(argument),
                LocalChain(document, catalog));
        }

        if (prefix.Equals(AppSettingsPrefix, StringComparison.OrdinalIgnoreCase))
            return new AspxResourceReference(AspxResourceForm.AppSetting, prefix, argument, argument, []);

        if (prefix.Equals(ConnectionStringsPrefix, StringComparison.OrdinalIgnoreCase))
            return new AspxResourceReference(AspxResourceForm.ConnectionString, prefix, argument, argument, []);

        return null;
    }

    /// <summary>What a <c>meta:resourcekey</c> or <c>resourcekey</c> attribute names.</summary>
    public static AspxResourceReference Implicit(
        AspxDocument document, ResourceCatalog catalog,
        string attributeName, string key, ElementNode? element)
    {
        // DNN's unprefixed spelling goes through Localization.GetString, which is why the shared
        // files join the chain; ASP.NET's meta:resourcekey only ever reads the page's own file.
        bool dnn = attributeName.IndexOf(':') < 0;

        return new AspxResourceReference(
            AspxResourceForm.ImplicitKey, attributeName, key.Trim(), key.Trim(),
            dnn ? LocalChain(document, catalog) : Local(document, catalog),
            Control: (element as ControlNode)?.ControlType);
    }

    /// <summary>
    /// Every file of every probed family that defines the key, in probe order — the neutral file
    /// of the nearest family first, then its translations and customizations, then the fallbacks.
    /// </summary>
    /// <remarks>
    /// A key defined only in a translation or a customization is a match like any other.
    /// <c>TryGetFromResourceFile</c> reads each file directly and never requires the neutral one to
    /// carry the key, so treating its absence there as missing would report a key that resolves
    /// perfectly well at runtime.
    /// </remarks>
    public static ImmutableArray<AspxResourceMatch> Matches(AspxResourceReference reference)
    {
        if (reference.Form is AspxResourceForm.AppSetting or AspxResourceForm.ConnectionString
            || !reference.HasKey
            || reference.Families.IsDefaultOrEmpty)
        {
            return [];
        }

        var matches = ImmutableArray.CreateBuilder<AspxResourceMatch>();
        string groupPrefix = reference.Key + ".";

        foreach (var family in reference.Families)
        {
            foreach (var file in ResourceCatalogService.Load(family).Files)
            {
                if (reference.Form is AspxResourceForm.ImplicitKey)
                {
                    // The key itself counts alongside the group. DNN's spelling reaches
                    // Localization.GetString, whose ".Text" default only applies when the key has
                    // no dot of its own — so `resourcekey="cmdEdit.Text"` names one entry outright.
                    foreach (string key in file.Entries.Keys.Order(StringComparer.Ordinal))
                    {
                        if (key.StartsWith(groupPrefix, StringComparison.Ordinal)
                            || key.Equals(reference.Key, StringComparison.Ordinal))
                        {
                            matches.Add(new AspxResourceMatch(file, file.Entries[key]));
                        }
                    }
                }
                else if (file.Entries.TryGetValue(reference.Key, out var entry))
                {
                    matches.Add(new AspxResourceMatch(file, entry));
                }
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>The union of the keys the probed families declare — completion's source.</summary>
    public static ImmutableArray<string> Keys(ImmutableArray<ResourceFamily> families)
    {
        if (families.IsDefaultOrEmpty)
            return [];

        var keys = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var family in families)
        {
            foreach (string key in ResourceCatalogService.Load(family).AllKeys)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        return keys.ToImmutable();
    }

    /// <summary>
    /// Every <c>&lt;%$ … %&gt;</c> in the file, in both shapes the parser produces: a node of its
    /// own in element content, and an attribute value with no node at all — the control it is
    /// written on has not been pushed when the value is read.
    /// </summary>
    public static IEnumerable<(TokenString Prefix, TokenString Argument, ElementNode? Element)> Builders(
        RootNode root)
    {
        foreach (var node in AspxSymbolResolver.EnumerateNodes(root))
        {
            if (node is ExpressionBuilderNode builder)
                yield return (builder.Prefix, builder.Argument, null);
        }

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            foreach (var (_, value) in element.RawAttributes)
            {
                if (value.Kind is AttributeValueKind.ExpressionBuilder)
                    yield return (value.Prefix, value.Token, element);
            }
        }
    }

    /// <summary>The global resource classes the project declares — the first argument of the
    /// two-argument <c>Resources</c> form.</summary>
    public static ImmutableArray<ResourceFamily> GlobalClasses(ResourceCatalog catalog)
    {
        if (catalog.Families.IsDefaultOrEmpty)
            return [];

        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (var family in catalog.Families)
        {
            if (IsGlobalFolder(family.Directory))
                families.Add(family);
        }

        return families.ToImmutable();
    }

    /// <summary>The page's own local resource file — <c>App_LocalResources/Default.aspx.resx</c>
    /// beside <c>Default.aspx</c>.</summary>
    public static ImmutableArray<ResourceFamily> Local(AspxDocument document, ResourceCatalog catalog)
    {
        if (Path.GetDirectoryName(document.FilePath) is not { Length: > 0 } directory)
            return [];

        return catalog.Find(Path.Combine(directory, LocalFolder), Path.GetFileName(document.FilePath))
            is { } family
            ? [family]
            : [];
    }

    /// <summary>The page's own file, then the shared file beside it, then the application-wide
    /// one — the inner half of the cascade <c>LocalizationProvider</c> walks.</summary>
    private static ImmutableArray<ResourceFamily> LocalChain(AspxDocument document, ResourceCatalog catalog)
    {
        var families = ImmutableArray.CreateBuilder<ResourceFamily>(3);
        families.AddRange(Local(document, catalog));

        if (Path.GetDirectoryName(document.FilePath) is { Length: > 0 } directory
            && catalog.Find(Path.Combine(directory, LocalFolder), SharedBaseName) is { } shared)
        {
            families.Add(shared);
        }

        families.AddRange(Global(document, catalog, SharedBaseName));

        return families.ToImmutable();
    }

    /// <summary>The named class under <c>App_GlobalResources</c>.</summary>
    private static ImmutableArray<ResourceFamily> Global(
        AspxDocument document, ResourceCatalog catalog, string className)
    {
        if (className.Length == 0)
            return [];

        if (Path.GetDirectoryName(document.Project.FilePath) is { Length: > 0 } root
            && catalog.Find(Path.Combine(root, GlobalFolder), className) is { } atRoot)
        {
            return [atRoot];
        }

        // A web site whose application root is not the project directory still has exactly one
        // App_GlobalResources; finding it by name beats declaring the class missing.
        var families = ImmutableArray.CreateBuilder<ResourceFamily>();

        foreach (var family in catalog.Named(className))
        {
            if (IsGlobalFolder(family.Directory))
                families.Add(family);
        }

        return families.ToImmutable();
    }

    private static bool IsGlobalFolder(string directory) =>
        Path.GetFileName(directory).Equals(GlobalFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>DNN's default translation property. The condition is <c>IndexOf('.') &lt; 1</c>, so
    /// a key that starts with a dot gets the suffix too.</summary>
    private static string WithDefaultSuffix(string key) =>
        key.IndexOf('.', StringComparison.Ordinal) < 1 ? key + ".Text" : key;

    /// <summary>
    /// The attribute an implicit-localization hit was written on. The hit carries the value, and
    /// the two spellings differ in where they fall back to, so the name has to come back off the
    /// element by range.
    /// </summary>
    private static string? ImplicitKeyAttributeName(AspxHit hit)
    {
        if (hit.Element is null)
            return null;

        foreach (var (key, value) in hit.Element.RawAttributes)
        {
            if (AspxSymbolResolver.IsImplicitKeyAttribute(key.Value)
                && AspxSymbolResolver.Span(value.Range) == hit.Span)
                return key.Value;
        }

        return null;
    }
}
