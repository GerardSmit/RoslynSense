using System.Collections.Immutable;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// The shipped lookup sets. Nobody should have to write a <see cref="ResourceLookup"/> by hand to
/// get navigation working in a DNN module or an <c>IStringLocalizer</c> app.
/// </summary>
/// <remarks>
/// Presets merge rather than compete, and merging all of them is the default. Every lookup names
/// a fully-qualified containing type, so the DNN set is inert in a solution with no DNN reference
/// and the <c>IStringLocalizer</c> set is inert in a WebForms one — the cost of an unused preset
/// is the failed metadata lookup its containing type causes once per compilation.
/// </remarks>
internal static class ResourcePresets
{
    public const string None = "none";

    /// <summary>The preset named, or every built-in when the name is null or empty. An unknown
    /// name warns and falls back to everything, because silently answering nothing looks
    /// identical to the feature being broken.</summary>
    public static ResourcePreset Named(string? name, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name))
            return All;

        if (name.Equals(None, StringComparison.OrdinalIgnoreCase))
            return Empty;
        if (name.Equals("webforms", StringComparison.OrdinalIgnoreCase))
            return WebForms;
        if (name.Equals("dnn", StringComparison.OrdinalIgnoreCase))
            return Dnn;
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return DotNet;

        warnings.Add(
            $"resources.preset '{name}' is not one of webforms, dnn, dotnet, none; using all built-ins.");
        return All;
    }

    /// <summary>
    /// The shapes a page-wide localizer produces, which between them cover almost every key in an
    /// <c>App_LocalResources</c> file that no call site ever mentions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written out rather than parsed from the configured form, because a preset that failed to
    /// parse would fail in a static initializer and take the process with it. The configured form
    /// of these four is <c>[Control.ID].Text</c>, <c>[Control.ID].ToolTip</c>,
    /// <c>Header[Control.UniqueName].Text</c> and <c>Header[Control.Name].Text</c>; a test holds
    /// the two spellings to each other.
    /// </para>
    /// <para>
    /// A control is asked for by its <c>ID</c>, so <c>litStock.Text</c> belongs to
    /// <c>ID="litStock"</c>. A grid column is not a control and has no ID, so a heading is asked
    /// for under a prefix and its <c>UniqueName</c> — <c>HeaderAmount.Text</c> belongs to
    /// <c>UniqueName="Amount"</c>. <c>Name</c> is the same rule for the column kinds that carry
    /// that attribute instead. Anything else a codebase composes is a line of configuration.
    /// </para>
    /// </remarks>
    private static ImmutableArray<ResourceMarkupBinding> ControlAndColumnHeadings { get; } =
    [
        new ResourceMarkupBinding { Attribute = "ID", Suffix = ".Text" },
        new ResourceMarkupBinding { Attribute = "ID", Suffix = ".ToolTip" },
        new ResourceMarkupBinding { Prefix = "Header", Attribute = "UniqueName", Suffix = ".Text" },
        new ResourceMarkupBinding { Prefix = "Header", Attribute = "Name", Suffix = ".Text" },
    ];

    /// <summary>
    /// The two more DNN's own controls compose, on top of <see cref="ControlAndColumnHeadings"/>.
    /// </summary>
    /// <remarks>
    /// <c>&lt;dnn:label&gt;</c> asks for two keys under its own <c>ID</c> — the caption and the
    /// tooltip body beside it — so a settings page with thirty labels has thirty <c>.Help</c> keys
    /// no call site mentions. <c>&lt;dnn:textcolumn&gt;</c> and its siblings have no
    /// <c>UniqueName</c>: a bound column is asked for under the field it binds, which is why this
    /// one reads a <c>DataField</c> and ends in <c>.Header</c> rather than starting with it.
    /// Configured form: <c>[Control.ID].Help</c> and <c>[Control.DataField].Header</c>.
    /// </remarks>
    private static ImmutableArray<ResourceMarkupBinding> DnnLabelsAndBoundColumns { get; } =
    [
        new ResourceMarkupBinding { Attribute = "ID", Suffix = ".Help" },
        new ResourceMarkupBinding { Attribute = "DataField", Suffix = ".Header" },
    ];

    private static ResourcePreset Empty { get; } = new([], []);

    /// <summary>
    /// Stock ASP.NET: <c>App_LocalResources</c> beside the page, <c>App_GlobalResources</c> at the
    /// application root, and the two <c>Get*ResourceObject</c> families that read them.
    /// </summary>
    private static ResourcePreset WebForms { get; } = new(
        Conventions:
        [
            new ResourceRootConvention { Id = "local", SiblingFolder = "App_LocalResources" },
        ],
        Lookups:
        [
            new ResourceLookup
            {
                ContainingType = "System.Web.HttpContext",
                MethodName = "GetGlobalResourceObject",
                ParameterTypes = Signature("string", "string"),
                KeyIndex = 1,
                RootIndex = 0,
                RootSource = RootSource.Argument,
                RootInterpretation = RootInterpretation.GlobalClassName,
            },
            new ResourceLookup
            {
                ContainingType = "System.Web.HttpContext",
                MethodName = "GetLocalResourceObject",
                ParameterTypes = Signature("string", "string"),
                KeyIndex = 1,
                RootIndex = 0,
                RootSource = RootSource.Argument,
                RootInterpretation = RootInterpretation.VirtualPath,
            },
            new ResourceLookup
            {
                ContainingType = "System.Web.UI.TemplateControl",
                MethodName = "GetGlobalResourceObject",
                ParameterTypes = Signature("string", "string"),
                KeyIndex = 1,
                RootIndex = 0,
                RootSource = RootSource.Argument,
                RootInterpretation = RootInterpretation.GlobalClassName,
            },

            // The single-argument overload reads the page's own local file. In a code-behind the
            // containing *file* is the .cs, so the root is the containing type — which the markup
            // index maps back to the .aspx through its Inherits attribute.
            new ResourceLookup
            {
                ContainingType = "System.Web.UI.TemplateControl",
                MethodName = "GetLocalResourceObject",
                ParameterTypes = Signature("string"),
                KeyIndex = 0,
                RootSource = RootSource.ContainingType,
                RootInterpretation = RootInterpretation.VirtualPath,
                Fallbacks = ["local"],
            },
        ])
    {
        MarkupBindings = ControlAndColumnHeadings,
    };

    /// <summary>
    /// DNN. The three <c>local</c> → <c>localShared</c> → <c>global</c> conventions are the inner
    /// half of the cascade <c>LocalizationProvider</c> walks, and every key gets <c>.Text</c>
    /// appended when it carries no dot of its own.
    /// </summary>
    /// <remarks>
    /// <c>Localization.GetString</c> is why <see cref="ResourceLookup.ParameterTypes"/> exists.
    /// Three of its overloads take two arguments — <c>(string, string)</c>,
    /// <c>(string, Control)</c> and <c>(string, PortalSettings)</c> — and only the first puts a
    /// root at index 1; matching on name and arity would bind all three and resolve two of them to
    /// garbage. Everything past index 1 is a language, a portal or a flag in every overload, so
    /// the longer root-at-1 shapes are spelled with trailing wildcards rather than repeated per
    /// signature.
    /// </remarks>
    private static ResourcePreset Dnn { get; } = new(
        Conventions:
        [
            new ResourceRootConvention { Id = "local", SiblingFolder = "App_LocalResources" },
            new ResourceRootConvention
            {
                Id = "localShared",
                SiblingFolder = "App_LocalResources",
                FixedName = "SharedResources",
            },
            new ResourceRootConvention
            {
                Id = "global",
                RootFolder = "App_GlobalResources",
                FixedName = "SharedResources",
            },
        ],
        Lookups:
        [
            RootAtOne("string", "string"),
            RootAtOne("string", "string", "*"),
            RootAtOne("string", "string", "*", "*"),
            RootAtOne("string", "string", "*", "*", "*"),

            // GetString(key, ctrl) walks the control tree by reflection looking for an
            // IModuleControl, so the value is unreachable by construction. DNN ends up at the
            // control's LocalResourceFile, which ModuleControlFactory sets from ControlSrc — the
            // markup path — so take the same route from the containing type instead of chasing it.
            new ResourceLookup
            {
                ContainingType = DnnLocalization,
                MethodName = "GetString",
                ParameterTypes = Signature("string", "System.Web.UI.Control"),
                KeyIndex = 0,
                RootSource = RootSource.ContainingType,
                RootInterpretation = RootInterpretation.VirtualPath,
                DefaultKeySuffix = TextSuffix,
                Fallbacks = ["localShared", "global"],
            },
            new ResourceLookup
            {
                ContainingType = DnnLocalization,
                MethodName = "GetString",
                ParameterTypes = Signature("string", "DotNetNuke.Entities.Portals.PortalSettings"),
                KeyIndex = 0,
                RootSource = RootSource.Constant,
                RootConstant = SharedResources,
                RootInterpretation = RootInterpretation.GlobalClassName,
                DefaultKeySuffix = TextSuffix,
            },
            new ResourceLookup
            {
                ContainingType = DnnLocalization,
                MethodName = "GetString",
                ParameterTypes = Signature("string"),
                KeyIndex = 0,
                RootSource = RootSource.Constant,
                RootConstant = SharedResources,
                RootInterpretation = RootInterpretation.GlobalClassName,
                DefaultKeySuffix = TextSuffix,
            },

            LocalizeHelper("DotNetNuke.Entities.Modules.PortalModuleBase", "LocalizeText"),
            LocalizeHelper("DotNetNuke.Entities.Modules.PortalModuleBase", "LocalizeString"),
            LocalizeHelper("DotNetNuke.UI.Modules.ModuleUserControlBase", "LocalizeString"),
        ])
    {
        MarkupBindings = [.. ControlAndColumnHeadings, .. DnnLabelsAndBoundColumns],
    };

    /// <summary>
    /// Modern .NET: <c>IStringLocalizer</c> and the <c>ResourceManager</c> a
    /// <c>*.Designer.cs</c> wraps.
    /// </summary>
    private static ResourcePreset DotNet { get; } = new(
        Conventions:
        [
            new ResourceRootConvention { Id = "resources", RootFolder = "Resources" },
        ],
        Lookups:
        [
            // Both members are declared on the non-generic interface; the type argument comes off
            // the receiver's IStringLocalizer<T>, which is what RootSource.TypeArgument reads.
            new ResourceLookup
            {
                ContainingType = StringLocalizer,
                MethodName = "GetString",
                ParameterTypes = Signature("string"),
                KeyIndex = 0,
                RootSource = RootSource.TypeArgument,
                RootInterpretation = RootInterpretation.TypeName,
                Fallbacks = ["resources"],
            },
            new ResourceLookup
            {
                ContainingType = StringLocalizer,
                MethodName = "Item",
                ParameterTypes = Signature("string"),
                KeyIndex = 0,
                RootSource = RootSource.TypeArgument,
                RootInterpretation = RootInterpretation.TypeName,
                Fallbacks = ["resources"],
            },
            new ResourceLookup
            {
                ContainingType = "System.Resources.ResourceManager",
                MethodName = "GetString",
                ParameterTypes = Signature("string"),
                KeyIndex = 0,
                RootSource = RootSource.ContainingType,
                RootInterpretation = RootInterpretation.TypeName,
            },
        ]);

    private static ResourcePreset All { get; } = Merge(Merge(WebForms, Dnn), DotNet);

    private const string DnnLocalization = "DotNetNuke.Services.Localization.Localization";
    private const string StringLocalizer = "Microsoft.Extensions.Localization.IStringLocalizer";
    private const string SharedResources = "SharedResources";

    /// <summary>DNN appends this when <c>key.IndexOf('.') &lt; 1</c>, so a leading dot gets it
    /// too — the condition the model carries rather than the presets.</summary>
    private const string TextSuffix = ".Text";

    /// <summary>A positional signature. <c>"*"</c> stands for one parameter of any type.</summary>
    private static ImmutableArray<string> Signature(params string[] parameterTypes) =>
        [.. parameterTypes];

    private static ResourceLookup RootAtOne(params string[] parameterTypes) => new()
    {
        ContainingType = DnnLocalization,
        MethodName = "GetString",
        ParameterTypes = Signature(parameterTypes),
        KeyIndex = 0,
        RootIndex = 1,
        RootSource = RootSource.Argument,
        RootInterpretation = RootInterpretation.VirtualPath,
        DefaultKeySuffix = TextSuffix,
        Fallbacks = ["localShared", "global"],
    };

    /// <summary>A protected helper on a module base class: no root argument at all, because the
    /// base reads its own <c>LocalResourceFile</c>.</summary>
    private static ResourceLookup LocalizeHelper(string containingType, string methodName) => new()
    {
        ContainingType = containingType,
        MethodName = methodName,
        ParameterTypes = Signature("string"),
        KeyIndex = 0,
        RootSource = RootSource.ContainingType,
        RootInterpretation = RootInterpretation.VirtualPath,
        DefaultKeySuffix = TextSuffix,
        Fallbacks = ["localShared", "global"],
    };

    /// <summary>Conventions merge by id with the later definition winning; lookups only ever
    /// append, since a lookup is identified by nothing; markup bindings append and dedupe, since
    /// one is identified by everything it holds.</summary>
    public static ResourcePreset Merge(ResourcePreset first, ResourcePreset second)
    {
        var conventions = new List<ResourceRootConvention>(first.Conventions);

        foreach (var convention in second.Conventions)
        {
            int existing = conventions.FindIndex(
                c => c.Id.Equals(convention.Id, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                conventions[existing] = convention;
            else
                conventions.Add(convention);
        }

        return new ResourcePreset([.. conventions], [.. first.Lookups, .. second.Lookups])
        {
            // Appended and then deduplicated: the presets overlap — webforms and dnn both declare
            // the ID rule — and a duplicate would report the same attribute twice.
            MarkupBindings = [.. first.MarkupBindings.Concat(second.MarkupBindings).Distinct()],
        };
    }
}

/// <summary>One named set of conventions, lookups and markup bindings, before any user
/// configuration.</summary>
internal sealed record ResourcePreset(
    ImmutableArray<ResourceRootConvention> Conventions,
    ImmutableArray<ResourceLookup> Lookups)
{
    public ImmutableArray<ResourceMarkupBinding> MarkupBindings { get; init; } = [];
}
