using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RoslynMCP.Config;

/// <summary>
/// A JSON Schema for <c>roslynsense.json</c>, generated from <see cref="RoslynSenseConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two consumers, one document. The editor registers it for <c>roslynsense.json</c> so the file
/// gets completion and validation while it is being typed, and the settings page builds its form
/// from it — which is what stops the form from having to be extended by hand every time a setting
/// is added.
/// </para>
/// <para>
/// The shape comes from the type; the words come from the table below. The XML
/// docs would have been the obvious source and are deliberately not used: they explain the
/// implementation to someone reading the code, at a length no form field can show. A label in a
/// settings page is a different piece of writing about the same setting.
/// </para>
/// </remarks>
public static class ConfigSchema
{
    /// <summary>The <c>$id</c> the editor and the settings page both refer to it by.</summary>
    public const string Id = "https://roslyn-sense.dev/schemas/roslynsense.schema.json";

    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The exporter reflects over the type graph and refuses to run without a resolver, which
        // the parameterless options do not carry.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// One line per setting, keyed by its path in the file. The key is the JSON spelling, so
    /// <c>tools.webForms</c> rather than <c>Tools.WebForms</c>.
    /// </summary>
    /// <remarks>
    /// Missing entries are a test failure rather than a blank field: a setting nobody wrote a
    /// sentence for is a setting nobody can use from the settings page.
    /// </remarks>
    private static readonly Dictionary<string, (string Title, string Description)> s_descriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tools"] = ("Language packs", "Which parts of RoslynSense are switched on. Turning one off removes its MCP tools and stops it answering editor requests."),
            ["tools.webForms"] = ("WebForms", "ASPX, ASCX and master pages: controls, properties and event handlers as C# symbols."),
            ["tools.razor"] = ("Razor", "Razor and Blazor views."),
            ["tools.proto"] = ("Protobuf", "`.proto` files joined to the C# they generate."),
            ["tools.mediator"] = ("Mediator", "Request and handler types joined across a mediator dispatch."),
            ["tools.resources"] = ("Resources", "`.resx` files: which file a resource key lives in, from C# and from markup."),
            ["tools.msBuild"] = ("Project files", "`.csproj`, `.props` and `.targets`."),
            ["tools.dbml"] = ("LINQ to SQL", "`.dbml` designer files."),
            ["tools.appSettings"] = ("appsettings.json", "`appsettings*.json` keys joined to the configuration reads that name them."),
            ["tools.webConfig"] = ("web.config", "`<appSettings>` and `<connectionStrings>` joined to the C# and markup that read them."),
            ["tools.dotSettings"] = ("ReSharper settings", "Whether a committed `.DotSettings` narrows inferred namespaces, search results and coverage."),
            ["tools.debugger"] = ("Debugger", "Launch, breakpoints, stepping and evaluation."),
            ["tools.profiling"] = ("Profiling", "CPU sampling, memory snapshots and coverage."),
            ["tools.database"] = ("Database", "Querying and describing the databases the solution connects to."),

            ["webConfig"] = ("web.config files", "Which files the web.config pack treats as Framework configuration."),
            ["webConfig.additionalFiles"] = ("Additional files", "File names claimed alongside `web.config` and `app.config` — for frameworks that keep their own, such as DotNetNuke's `release.config` and `development.config`. Names only, not paths or globs."),

            ["resources"] = ("Resource lookups", "How `.resx` files are found and which call shapes carry a resource key."),
            ["resources.preset"] = ("Preset", "A built-in lookup set to start from: `webforms`, `dnn`, `dotnet`, or `none`. Omitted merges all three, which is the recommended setting."),
            ["resources.include"] = ("Include", "Globs relative to the project directory. Empty means every `.resx` outside `bin` and `obj`."),
            ["resources.exclude"] = ("Exclude", "Globs removed from what Include found, applied first."),
            ["resources.overrides"] = ("Overrides", "Customization segments beside a base file, such as DNN's `Portal-*`. Replaces the preset's set rather than adding to it."),
            ["resources.conventions"] = ("Conventions", "Named ways of turning a call-site file into a resx base name. Merged into the preset's by id."),
            ["resources.lookups"] = ("Lookups", "Call shapes that carry a resource key. Appended to the preset's."),
            ["resources.missingKeyDiagnostic"] = ("Report missing keys", "Report a key that no file of its family declares. Off by default: a false report on a key that resolves fine at runtime is what gets the feature switched off wholesale."),

            ["database"] = ("Databases", "Which databases the solution can be queried against."),
            ["database.autoDiscovery"] = ("Auto-discovery", "Scan the tree for connection strings. Omitted runs the scan only when nothing is registered explicitly. Production-flavoured environment names are never loaded."),
            ["database.connections"] = ("Connections", "Explicit connections by alias. Either `provider:connectionString` or an object with `provider` and `connectionString`."),

            ["debugger"] = ("Debugger view", "Which `System.Diagnostics` attributes the debug engines honour while inspecting and stepping."),
            ["debugger.debuggerDisplay"] = ("DebuggerDisplay", "Format values using their type's `DebuggerDisplayAttribute`."),
            ["debugger.typeProxy"] = ("DebuggerTypeProxy", "Expand values through their type's `DebuggerTypeProxyAttribute`."),
            ["debugger.browsable"] = ("DebuggerBrowsable", "Honour `DebuggerBrowsableAttribute` when listing members."),
            ["debugger.justMyCode"] = ("Just My Code", "Step past `DebuggerStepThrough`, `DebuggerHidden`, `DebuggerNonUserCode`, and code with no symbols."),
            ["debugger.rawView"] = ("Raw View", "Offer a Raw View child whenever a proxy or a hidden member means the listed children are not the object's own fields."),
            ["debugger.maxChildren"] = ("Max children", "How many children of one value to list before truncating. Defaults to 100."),

            ["tableFormat"] = ("Table format", "How tabular tool output is rendered: `markdown` (default) or `toon`."),
            ["preload"] = ("Preload", "Solutions or projects to load on startup. Omitted auto-discovers the first solution in the working directory; an empty list disables preloading."),
            ["sharedHost"] = ("Shared host", "Load each solution once per machine and share it across every editor window and chat, instead of once per client."),
            ["hostIdleMinutes"] = ("Host idle timeout", "Minutes the shared host stays alive after its last client disconnects."),
            ["maxWorkspaces"] = ("Cached workspaces", "How many loaded solutions one process keeps before evicting the least recently used."),
        };

    /// <summary>The schema as a JSON node.</summary>
    public static JsonNode Generate()
    {
        var schema = s_options.GetJsonSchemaAsNode(
            typeof(RoslynSenseConfig),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = false,
                TransformSchemaNode = static (context, node) => Annotate(context, node),
            });

        if (schema is JsonObject root)
        {
            // Written in this order so the head of the file reads as what it is. JsonObject keeps
            // insertion order, and the properties the exporter produced are already in it, so the
            // three below go in first and the rest follow.
            var reordered = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["$id"] = Id,
                ["title"] = "RoslynSense configuration",
                ["description"] =
                    "Settings for RoslynSense. Merged from every layer that applies: the global file in "
                    + "~/.roslynsense, every roslynsense.json from the filesystem root down to the working "
                    + "directory, each one's roslynsense.local.json sibling, and the personal file "
                    + "~/.roslynsense keeps for this directory. Nearest wins, field by field.",
            };

            foreach (var (key, value) in root.ToList())
            {
                root.Remove(key);
                reordered[key] = value;
            }

            return reordered;
        }

        return schema;
    }

    /// <summary>The schema as text, formatted the way the checked-in copy is.</summary>
    public static string GenerateText() =>
        Generate().ToJsonString(s_writeOptions) + Environment.NewLine;

    /// <summary>
    /// Relaxed escaping because the file is read by people as often as by parsers: the
    /// descriptions are full of backticks and angle brackets, and a page of <c>`</c> is not
    /// a document anyone wants to check for accuracy.
    /// </summary>
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Every path the descriptions table knows about, for the test that checks it is complete.</summary>
    internal static IReadOnlyCollection<string> DescribedPaths => s_descriptions.Keys;

    private static JsonNode Annotate(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject obj || PathOf(context) is not { Length: > 0 } path)
            return node;

        if (s_descriptions.TryGetValue(path, out var text))
        {
            obj["title"] = text.Title;
            obj["description"] = text.Description;
        }

        return obj;
    }

    /// <summary>
    /// The dotted path of the property being exported — <c>tools.webForms</c> — or empty for the
    /// root and for anything that is not a named property.
    /// </summary>
    /// <remarks>
    /// The exporter reports the path as JSON Pointer segments that include the <c>properties</c>
    /// keyword between levels; dropping those leaves the path a person would write.
    /// </remarks>
    private static string PathOf(JsonSchemaExporterContext context)
    {
        var segments = new List<string>();

        // A span, so no LINQ: the exporter hands the path out without allocating it.
        foreach (string segment in context.Path)
        {
            // Inside a collection or dictionary value. The element carries the same path as the
            // property holding it, and annotating both puts the property's sentence on every item
            // of the array as well — so the element is left alone and only the property is named.
            if (segment is "items" or "additionalProperties")
                return string.Empty;

            if (segment is "$" or "properties" or "anyOf" or "oneOf" or "$defs")
                continue;

            if (int.TryParse(segment, out _))
                continue;

            segments.Add(segment);
        }

        return string.Join(".", segments);
    }
}
