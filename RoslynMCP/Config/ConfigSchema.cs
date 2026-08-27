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
            ["tools.logging"] = ("Logging templates", "The `{Placeholder}` of a logging message joined to the value it prints. Microsoft.Extensions.Logging, Serilog and NLog."),
            ["tools.formatting"] = ("Format strings", "The `{0:dd-MM-yyyy}` of a composite format string and the `yyyyMMdd` of an interpolation, coloured a component at a time and hovered with a worked example."),
            ["tools.valueSets"] = ("Allowed string values", "For a string that has to be one of a short list — an order status of `\"SHIPPED\"`, a document type, a country code. Completes the literal from the list, says on hover what the code means, and reports one the list does not have."),
            ["tools.cron"] = ("Scheduled jobs", "The `\"0 22 * * 1-6\"` handed to Hangfire or Quartz, coloured a field at a time and hovered with what it means and when it next runs. Also lists every scheduled job in the solution."),
            ["tools.debugger"] = ("Debugger", "Launch, breakpoints, stepping and evaluation."),
            ["tools.profiling"] = ("Profiling", "CPU sampling, memory snapshots and coverage."),
            ["tools.database"] = ("Database", "Querying and describing the databases the solution connects to."),

            ["webConfig"] = ("web.config files", "Which files the web.config pack treats as Framework configuration."),
            ["webConfig.additionalFiles"] = ("Additional files", "File names claimed alongside `web.config` and `app.config` — for frameworks that keep their own, such as DotNetNuke's `release.config` and `development.config`. Names only, not paths or globs."),

            ["logging"] = ("Logging templates", "Which of the message-template rules report. Two of them restate what the `[LoggerMessage]` source generator says as SYSLIB1014 and SYSLIB1015, at a better location — turn those off where the generator runs."),
            ["logging.templateSyntax"] = ("Malformed template", "LOG0001 — an unclosed brace or a placeholder naming nothing. Microsoft.Extensions.Logging throws on the first of those rather than rendering it oddly."),
            ["logging.unknownPlaceholder"] = ("Placeholder names no parameter", "LOG0002 — a placeholder in a `[LoggerMessage]` message that matches no parameter, so it prints as literal text. SYSLIB1014's claim, reported on the placeholder."),
            ["logging.valueCount"] = ("Wrong number of values", "LOG0003 — the placeholders and the values disagree in count. Placeholders bind by position, so this shifts every placeholder after the mistake onto the wrong value."),
            ["logging.unusedValue"] = ("Value never printed", "LOG0004 — a parameter or argument no placeholder renders. SYSLIB1015's claim for a generated method, reported on the parameter."),
            ["logging.exceptionPosition"] = ("Exception in the wrong place", "LOG0005 — an exception passed as a rendered value rather than as the first argument, which loses the stack trace and the sink's exception handling."),

            ["webForms"] = ("WebForms markup", "Attributes whose value names a member of the bound item, the way `Eval(\"...\")` does. Nothing is read this way until it is listed here, because which attributes behave so comes from the control library rather than from the framework."),
            ["webForms.dataExpressions"] = ("Data expressions", "The attributes to read as a path from the bound item. A grid's `SortExpression` and `DataField` are the usual ones."),
            ["webForms.dataExpressions[]"] = ("Attribute", "One attribute, and how its value reads."),
            ["webForms.dataExpressions[].tag"] = ("Tag", "The tag it is written on — `telerik:GridBoundColumn` — or `*` for any. Matched on the tag as written rather than on the control's type, so it keeps working in a site whose vendor assembly does not resolve."),
            ["webForms.dataExpressions[].attribute"] = ("Attribute", "The attribute name. Matched without regard to casing, as markup attributes are."),
            ["webForms.dataExpressions[].kind"] = ("Value is", "A member path from the bound item, or a composite format string."),
            ["webForms.dataExpressions[].source"] = ("Formats the value of", "For a format string, which sibling attribute names the value being formatted — `[ItemType].[Control.DataField]` reads this tag's `DataField` and resolves it against the bound item. That is what tells a `{0:dd-MM-yyyy}` it is formatting a date."),
            ["webForms.unknownMemberDiagnostic"] = ("Report unknown members", "WFB0001 — a name the bound item type does not have. Only ever reported when that type is known: a container with no `ItemType` whose `DataSource` cannot be traced says nothing rather than something wrong."),
            ["webForms.severity"] = ("Report them as", "How loudly WFB0001 is reported. A warning by default, because a path is resolved case-insensitively through `TypeDescriptor` and can be satisfied at runtime by a type this tool never sees."),
            ["cron"] = ("Scheduled jobs", "A crontab expression is five or six numbers that decide when something runs on a server nobody is watching, and nothing in C# checks it — a transposed field is a job that quietly runs on the wrong day. Hangfire and Quartz are recognised already, and so is any parameter named `cronExpression` or close to it, so this section is only needed for an in-house scheduler whose method nobody could have guessed the name of."),
            ["cron.parameterNames"] = ("Parameter names", "Extra parameter names that mean a string argument is a schedule. `cronExpression`, `cron`, `cronSchedule`, `crontab` and `cronString` are recognised already, whoever declares them."),
            ["cron.expressionDiagnostic"] = ("Report bad schedules", "CRON0001 — an expression the library reading it would reject. Worth having because it is otherwise found at run time, by the job never running."),
            ["cron.severity"] = ("Report them as", "How loudly CRON0001 is reported. A warning by default rather than an error: the string is read by a library at a version RoslynSense cannot see, so being wrong is possible and a red squiggle under working code is worse than a yellow one."),
            ["cron.bindings"] = ("Methods", "The methods of your own that take a schedule — a `Scheduler.AddJob(name, cron, work)` wrapper, say. Every literal written at the call is then coloured, hovered and checked."),
            ["cron.bindings[]"] = ("Method", "One method that takes a crontab expression."),
            ["cron.bindings[].containingType"] = ("Class", "The full name of the class or interface declaring the member. Leave empty to match the member on any class."),
            ["cron.bindings[].memberName"] = ("Member", "The method name."),
            ["cron.bindings[].parameterTypes"] = ("Parameters", "One type name per parameter, `*` for a parameter of any type. Leave empty to match every overload."),
            ["cron.bindings[].cronIndex"] = ("Schedule is parameter", "Which parameter carries the expression, counted from 0. Leave empty to find it by name instead, which is what makes one entry cover every overload."),
            ["cron.bindings[].idIndex"] = ("Job name is parameter", "Which parameter names the job, counted from 0. Used to label it in the Cron Jobs list."),
            ["cron.bindings[].methodIndex"] = ("Work is parameter", "Which parameter says what to run, counted from 0."),
            ["cron.bindings[].dialect"] = ("Read as", "`hangfire`, `quartz` or `standard`. Leave empty to let the project's own references decide, which is right whenever it references only one. It matters: Quartz numbers Sunday 1 and everyone else numbers it 0, so the same expression names days a day apart."),
            ["valueSets"] = ("Allowed string values", "Some strings are really codes: an order status, a document type, a country. The list of codes is in a database table or a spreadsheet somewhere, and nothing in C# knows it, so `\"SHIPED\"` compiles and fails at run time. Say where the list comes from under Sets, then name the methods and the properties that carry a code — those literals then complete from the list, hover with what each code means, and are reported when the list does not have them."),
            ["valueSets.unknownValueDiagnostic"] = ("Report unknown values", "VAL0001 — a string that is not one of its set's values. Only ever reported for a set that loaded completely: an unreachable database says nothing rather than something wrong."),
            ["valueSets.severity"] = ("Report them as", "How loudly VAL0001 is reported. An error by default, because a code the table does not have is a branch that can never be taken. Soften it while a codebase catches up."),
            ["valueSets.sets"] = ("Sets", "Where the lists of codes come from. Each set is a query against a configured database connection, or the codes written out here."),
            ["valueSets.sets[]"] = ("Set", "One list of codes, and where it comes from."),
            ["valueSets.sets[].id"] = ("Name", "What to call this list, so the settings below can point at it. `orderStatus`, say."),
            ["valueSets.sets[].connection"] = ("Connection", "Which configured database connection to run the query against. Leave empty when only one is configured."),
            ["valueSets.sets[].query"] = ("Query", "The query producing the values — `SELECT [Code] FROM Shop_OrderStatus ORDER BY [Code]`. The first column is the value; a second column, if there is one, is shown beside it as a label."),
            ["valueSets.sets[].values"] = ("Values", "The values written out, for a set with no database behind it. Ignored when a query is set."),
            ["valueSets.sets[].caseSensitive"] = ("Match casing exactly", "Off by default, because the comparison the code does usually is case-insensitive too."),
            ["valueSets.bindings"] = ("Methods", "The methods that carry a code — the one that takes it as an argument, the one that returns it. Every literal written at the call is checked against the list."),
            ["valueSets.bindings[]"] = ("Method", "One method, and the set its values come from. Give a parameter position and the value is that argument; leave it empty and the method returns a code, so what is checked is every literal its result is compared against."),
            ["valueSets.bindings[].set"] = ("Set", "Which list above this member's codes come from."),
            ["valueSets.bindings[].containingType"] = ("Class", "The full name of the class or interface declaring the member. Leave empty to match the member on any class."),
            ["valueSets.bindings[].memberName"] = ("Member", "The method, property or field name, or `Item` for an indexer."),
            ["valueSets.bindings[].parameterTypes"] = ("Parameters", "One type name per parameter, `*` for a parameter of any type. Leave empty to match every overload."),
            ["valueSets.bindings[].valueIndex"] = ("Value is parameter", "Which parameter carries the value, counted from 0. Leave empty for a property, a field, or a method whose return value is one of the set."),
            ["valueSets.properties"] = ("Properties", "The properties and fields that hold a code — an order's `Status.Code`, a document's `TypeCode`. Every literal compared or assigned to one of them is checked against the list."),
            ["valueSets.properties[]"] = ("Property", "One property or field, and the set its values come from. `order.Status.Code == \"SHIPPED\"` is the shape this is for: nothing is called, so every literal the member is compared or assigned is what gets checked."),
            ["valueSets.properties[].set"] = ("Set", "Which list above this member's codes come from."),
            ["valueSets.properties[].containingType"] = ("Class", "The full name of the class or interface declaring the property or field. Leave empty to match the member on any class, which is the escape hatch for a code carried by a `Code` property on a dozen entities."),
            ["valueSets.properties[].memberName"] = ("Property", "The property or field name."),

            ["resources"] = ("Resource lookups", "How `.resx` files are found and which call shapes carry a resource key."),
            ["resources.preset"] = ("Preset", "A built-in lookup set to start from: `webforms`, `dnn`, `dotnet`, or `none`. Omitted merges all three, which is the recommended setting."),
            ["resources.include"] = ("Include", "Globs relative to the project directory. Empty means every `.resx` outside `bin` and `obj`."),
            ["resources.exclude"] = ("Exclude", "Globs removed from what Include found, applied first."),
            ["resources.overrides"] = ("Overrides", "Customization segments beside a base file, such as DNN's `Portal-*`. Replaces the preset's set rather than adding to it."),
            ["resources.conventions"] = ("Conventions", "Named ways of turning a call-site file into a resx base name. Merged into the preset's by id."),
            ["resources.lookups"] = ("Lookups", "Call shapes that carry a resource key. Appended to the preset's."),
            ["resources.markupBindings"] = ("Markup bindings", "Key shapes an application composes from a markup attribute rather than any call site writing them out, each written as the key it produces: `[Control.ID].Text`, `Header[Control.UniqueName].Text`, `[Control.ID].Header`. Appended to the preset's."),
            ["resources.missingKeyDiagnostic"] = ("Report missing keys", "Report a key that no file of its family declares. Off by default: a false report on a key that resolves fine at runtime is what gets the feature switched off wholesale."),

            ["resources.overrides[]"] = ("Override", "One customization segment and how strongly it wins."),
            ["resources.overrides[].pattern"] = ("Segment", "The segment as it appears in the file name, `*` allowed — DNN's `Host` and `Portal-*`."),
            ["resources.overrides[].rank"] = ("Wins over", "Higher wins. The uncustomized file is 0, so `Host` is 1 and `Portal-*` is 2. Explicit because sorting `Portal-*` and `Host` alphabetically gets the precedence backwards."),

            ["resources.conventions[]"] = ("Convention", "One way of getting from a call site to the resx file it reads."),
            ["resources.conventions[].id"] = ("Name", "What a lookup's Fallbacks refers to this by. Reusing the name of a built-in convention replaces it and leaves the rest alone."),
            ["resources.conventions[].siblingFolder"] = ("Folder beside the file", "Looked for next to the calling file — `App_LocalResources`. Use this or Folder at the project root, not both."),
            ["resources.conventions[].rootFolder"] = ("Folder at the project root", "Looked for from the project root down — `App_GlobalResources`."),
            ["resources.conventions[].fixedName"] = ("Always this file", "One file name every call site shares, such as `SharedResources`. Leave empty to name the file after the calling file."),
            ["resources.conventions[].suffix"] = ("File endings", "What the resx name ends with. `.ascx.resx` for a user control's local file; defaults to `.resx`."),

            ["resources.lookups[]"] = ("Lookup", "One method that takes a resource key, and where the file it reads comes from."),
            ["resources.lookups[].containingType"] = ("Class", "The full name of the class or interface declaring the method — `DotNetNuke.Services.Localization.Localization`. Leave empty to match the method on any class, which is the escape hatch for a codebase where each module wraps localization in its own helper."),
            ["resources.lookups[].methodName"] = ("Method", "The method name, or `Item` for an indexer."),
            ["resources.lookups[].parameterTypes"] = ("Parameters", "One type name per parameter, `*` for a parameter of any type. Leave empty to match every overload — which is wrong wherever overloads disagree about where the file name sits."),
            ["resources.lookups[].keyIndex"] = ("Key is parameter", "Which parameter carries the resource key, counted from 0."),
            ["resources.lookups[].rootSource"] = ("File name comes from", "Where the call site says which resx file to read."),
            ["resources.lookups[].rootInterpretation"] = ("Read that as", "What kind of name that value is, which decides how it becomes a resx file."),
            ["resources.lookups[].rootIndex"] = ("File name is parameter", "Which parameter carries it, counted from 0. Only used when the file name comes from an argument."),
            ["resources.lookups[].rootConstant"] = ("Fixed name", "The file name itself, for a helper that always reads one file. Only used when the file name is fixed."),
            ["resources.lookups[].defaultKeySuffix"] = ("Add to keys without a dot", "Appended to a key that contains no `.` — DNN's `.Text`, so `GetString(\"Submit\")` reads `Submit.Text`."),
            ["resources.lookups[].fallbacks"] = ("Then also look in", "Tried in order when the key is not in the first file. The list is what the preset defines plus any convention this file adds."),

            ["database"] = ("Databases", "Which databases the solution can be queried against."),
            ["database.autoDiscovery"] = ("Auto-discovery", "Scan the tree for connection strings. Omitted runs the scan only when nothing is registered explicitly. Production-flavoured environment names are never loaded."),
            ["database.connections"] = ("Connections", "Explicit connections by alias. Either `provider:connectionString` or an object with `provider` and `connectionString`."),

            ["debugger"] = ("Debugger", "Which `System.Diagnostics` attributes the debug engines honour while inspecting and stepping, and which engine debugs a CoreCLR target."),
            ["debugger.debuggerDisplay"] = ("DebuggerDisplay", "Format values using their type's `DebuggerDisplayAttribute`."),
            ["debugger.typeProxy"] = ("DebuggerTypeProxy", "Expand values through their type's `DebuggerTypeProxyAttribute`."),
            ["debugger.browsable"] = ("DebuggerBrowsable", "Honour `DebuggerBrowsableAttribute` when listing members."),
            ["debugger.callToString"] = ("Call ToString", "Show values through their own `ToString` override when no attribute claims them — VS's \"call string-conversion function on objects in variables windows\"."),
            ["debugger.justMyCode"] = ("Just My Code", "Step past `DebuggerStepThrough`, `DebuggerHidden`, `DebuggerNonUserCode`, and code with no symbols."),
            ["debugger.rawView"] = ("Raw View", "Offer a Raw View child whenever a proxy or a hidden member means the listed children are not the object's own fields."),
            ["debugger.maxChildren"] = ("Max children", "How many children of one value to list before truncating. Defaults to 100."),
            ["debugger.symbolInclude"] = ("Load symbols only for", "Globs for the only modules whose symbols load, when the list is non-empty. A glob without a path separator matches the module's file name (`MyCompany.*.dll`); with one, its full path (`**\\bin\\**`). Empty loads symbols for every module not excluded."),
            ["debugger.symbolExclude"] = ("Never load symbols for", "Globs for modules whose symbols never load, taking precedence over the include list. A module without symbols cannot bind source breakpoints — the same trade VS's \"Load all modules, unless excluded\" makes. ASP.NET's generated `App_Web_*.dll` page assemblies are already skipped without any configuration."),
            ["debugger.coreClrEngine"] = ("CoreCLR engine", "Which engine debugs .NET (CoreCLR) targets: `netcoredbg` (default) or `icordebug`. .NET Framework always uses `icordebug`, which is the only engine that can attach to it. `icordebug` additionally brings Just My Code through the runtime, breakpoints that bind against binaries built elsewhere, method return values after a step, and stepping through decompiled code — but it runs on Windows only and is newer on this runtime. Takes effect the next time debugging starts."),

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

    /// <summary>
    /// The values a setting accepts, where the type behind it is a string rather than an enum.
    /// </summary>
    /// <remarks>
    /// The parser takes these as strings so that an unknown one warns and falls back rather than
    /// failing the load, which is right for a config file and useless for a form: a text box for a
    /// six-value choice is a text box you have to already know the answer to. Spelled here so the
    /// settings page draws a dropdown and the JSON editor completes them.
    /// </remarks>
    private static readonly Dictionary<string, string[]> s_choices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resources.preset"] = ["webforms", "dnn", "dotnet", "none"],
            ["resources.lookups[].rootSource"] =
                ["argument", "typeArgument", "containingType", "containingFile", "constant", "none"],
            ["resources.lookups[].rootInterpretation"] =
                ["virtualPath", "globalClassName", "typeName", "relativePath", "baseName"],
            ["debugger.coreClrEngine"] = ["netcoredbg", "icordebug"],
            ["tableFormat"] = ["markdown", "toon"],
            ["valueSets.severity"] = ["error", "warning", "information"],
            ["webForms.severity"] = ["error", "warning", "information", "hidden"],
            ["webForms.dataExpressions[].kind"] = ["member", "format"],
        };

    /// <summary>
    /// Settings whose values only the running solution knows, so the page asks the server for them
    /// rather than reading a list from here.
    /// </summary>
    /// <remarks>
    /// A lookup's fallbacks name root conventions, and which conventions exist is the preset plus
    /// whatever the file declared — an answer per solution. Marked rather than probed so the page
    /// knows to draw a multiple-choice control before the answer arrives.
    /// </remarks>
    private static readonly HashSet<string> s_askTheServer =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "resources.lookups[].fallbacks",
            "valueSets.bindings[].set",
            "valueSets.properties[].set",
            "valueSets.sets[].connection",
        };

    /// <summary>
    /// Groups of fields that together name a call shape, so the page can offer one editor over all
    /// of them instead of five text boxes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared here rather than hard-coded in the page because the shape is not specific to
    /// resource lookups. "A class, a member on it, and which parameter carries what" is what every
    /// setting naming a call shape needs, and the next one should get the same editor by adding a
    /// line here.
    /// </para>
    /// <para>
    /// <c>label</c> is what the row calls the thing being named and <c>memberKinds</c> is what the
    /// server should offer for it, which are the two halves of the same fact: a shape asking for a
    /// property should say "Property" and be answered with properties. <c>parameters</c> and
    /// <c>positions</c> are absent for a member that is not called, since a property has neither.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, JsonObject> s_shapes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resources.lookups[]"] = new JsonObject
            {
                ["kind"] = "member",
                ["type"] = "containingType",
                ["member"] = "methodName",
                ["parameters"] = "parameterTypes",
                ["positions"] = new JsonObject
                {
                    ["keyIndex"] = "key",
                    ["rootIndex"] = "file name",
                },
                ["label"] = "Method",
                ["memberKinds"] = new JsonArray("method", "indexer"),
            },

            // The second one, which is what the note above was written in anticipation of: a value
            // binding is the same class-member-signature triple, with one position instead of two.
            ["valueSets.bindings[]"] = new JsonObject
            {
                ["kind"] = "member",
                ["type"] = "containingType",
                ["member"] = "memberName",
                ["parameters"] = "parameterTypes",
                ["positions"] = new JsonObject
                {
                    ["valueIndex"] = "value",
                },
                ["label"] = "Method",
                ["memberKinds"] = new JsonArray("method", "indexer"),
            },

            // And the third, which is the same triple with the call taken out of it: a property is
            // named by class and name alone, so the shape carries neither a signature nor a
            // position and the editor draws two fields rather than four.
            ["valueSets.properties[]"] = new JsonObject
            {
                ["kind"] = "member",
                ["type"] = "containingType",
                ["member"] = "memberName",
                ["label"] = "Property",
                ["memberKinds"] = new JsonArray("property", "field"),
            },
        };

    /// <summary>
    /// Fields of one list item that are alternatives to each other, so the page offers a choice
    /// between them rather than a form with two halves that must not both be filled in.
    /// </summary>
    /// <remarks>
    /// A value set is a query or a written-out list, never both; a convention looks beside the file
    /// or down from the project root, never both. Nothing in JSON Schema says so — <c>oneOf</c>
    /// would, but it says it as a validation rule rather than as a thing to draw, and a form built
    /// from a failed validation is a form that tells someone off for a state it walked them into.
    /// </remarks>
    private static readonly Dictionary<string, JsonArray> s_exclusive =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["valueSets.sets[]"] = new JsonArray
            {
                new JsonObject
                {
                    ["alternatives"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["title"] = "From a database query",
                            ["fields"] = new JsonArray("connection", "query"),
                        },
                        new JsonObject
                        {
                            ["title"] = "Values written here",
                            ["fields"] = new JsonArray("values"),
                        },
                    },
                },
            },

            ["resources.conventions[]"] = new JsonArray
            {
                new JsonObject
                {
                    ["alternatives"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["title"] = "Folder beside the file",
                            ["fields"] = new JsonArray("siblingFolder"),
                        },
                        new JsonObject
                        {
                            ["title"] = "Folder at the project root",
                            ["fields"] = new JsonArray("rootFolder"),
                        },
                    },
                },
            },
        };

    /// <summary>
    /// Fields that only apply once a sibling field says a particular thing, so the page can leave
    /// them out until they mean something.
    /// </summary>
    /// <remarks>
    /// A lookup's file name comes from an argument, a type argument, the containing file or a
    /// constant, and each of those answers makes exactly one of the remaining fields relevant. Both
    /// shown at once is two fields where one of them can only ever be ignored — which is how they
    /// get filled in and then quietly do nothing.
    /// </remarks>
    private static readonly Dictionary<string, JsonObject> s_when =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resources.lookups[].rootIndex"] = new JsonObject
            {
                ["field"] = "rootSource",
                ["equals"] = new JsonArray("argument"),
            },

            ["resources.lookups[].rootConstant"] = new JsonObject
            {
                ["field"] = "rootSource",
                ["equals"] = new JsonArray("constant"),
            },
        };

    /// <summary>
    /// Which fields, in order, say what a collapsed list item is — the one line a closed row shows.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than guessed from the first string field, because the field that
    /// identifies an item is not reliably the first one and a row labelled with the wrong half of
    /// itself is a list nobody can scan. An item with an <see cref="s_shapes"/> entry needs none of
    /// this: the call form it composes is already the sentence that names it.
    /// </remarks>
    private static readonly Dictionary<string, string[]> s_summaries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["valueSets.sets[]"] = ["id", "query", "values"],
            ["resources.conventions[]"] = ["id", "siblingFolder", "rootFolder", "fixedName"],
            ["resources.overrides[]"] = ["pattern", "rank"],
            ["webForms.dataExpressions[]"] = ["tag", "attribute"],
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

        if (s_askTheServer.Contains(path))
            obj["x-choices"] = "server";

        if (s_choices.TryGetValue(path, out var choices))
            obj["enum"] = new JsonArray([.. choices.Select(choice => (JsonNode)JsonValue.Create(choice))]);

        // Not a JSON Schema keyword: a validator ignores it and the settings page reads it. The
        // alternative was a second document to keep in step with this one.
        if (s_shapes.TryGetValue(path, out var shape))
            obj["x-shape"] = shape.DeepClone();

        if (s_exclusive.TryGetValue(path, out var exclusive))
            obj["x-exclusive"] = exclusive.DeepClone();

        if (s_when.TryGetValue(path, out var when))
            obj["x-when"] = when.DeepClone();

        if (s_summaries.TryGetValue(path, out var summary))
            obj["x-summary"] = new JsonArray([.. summary.Select(field => (JsonNode)JsonValue.Create(field))]);

        return obj;
    }

    /// <summary>
    /// The dotted path of the property being exported — <c>tools.webForms</c> — or empty for the
    /// root and for anything that is not a named property.
    /// </summary>
    /// <remarks>
    /// The exporter reports the path as JSON Pointer segments that include the <c>properties</c>
    /// keyword between levels; dropping those leaves the path a person would write. An array
    /// element keeps its property's path with <c>[]</c> on the end, so the list and one item's
    /// fields can be described separately — <c>resources.lookups</c> is a list of call shapes, and
    /// <c>resources.lookups[].keyIndex</c> is which parameter carries the key.
    /// </remarks>
    /// <remarks>
    /// Which segments are keywords cannot be decided by reading them, because a setting is allowed
    /// to be called <c>properties</c> — <c>valueSets.properties</c> is one — and its name arrives
    /// as the same word as the keyword that introduces it. What separates them is position: the
    /// segment after a <c>properties</c> keyword is always a name, whatever it says. Read by the
    /// word alone, <c>valueSets.properties</c> came out as <c>valueSets</c>, which silently gave a
    /// whole section the wrong title and left every field in it undescribed.
    /// </remarks>
    private static string PathOf(JsonSchemaExporterContext context)
    {
        var segments = new List<string>();
        bool nameNext = false;

        // A span, so no LINQ: the exporter hands the path out without allocating it.
        foreach (string segment in context.Path)
        {
            if (nameNext)
            {
                segments.Add(segment);
                nameNext = false;
                continue;
            }

            // A dictionary's value. The keys are the person's own, so there is nothing to name.
            if (segment is "additionalProperties")
                return string.Empty;

            if (segment is "items")
            {
                if (segments.Count == 0)
                    return string.Empty;

                segments[^1] += "[]";
                continue;
            }

            if (segment is "properties")
            {
                nameNext = true;
                continue;
            }

            if (segment is "$" or "anyOf" or "oneOf" or "$defs")
                continue;

            if (int.TryParse(segment, out _))
                continue;

            segments.Add(segment);
        }

        return string.Join(".", segments);
    }
}
