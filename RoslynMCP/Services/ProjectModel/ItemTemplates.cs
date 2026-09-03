using System.Diagnostics;
using System.Text;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>What a template can be asked for on.</summary>
public enum ItemTemplateScope
{
    /// <summary>The solution, or a solution folder — the files that sit beside a .sln.</summary>
    Solution,

    /// <summary>A project, or a folder inside one.</summary>
    Project,
}

/// <summary>One offer in the New menu.</summary>
/// <param name="Group">The heading it sits under, which is also how the picker is ordered.</param>
/// <param name="DefaultName">What the name box starts with — the file name Visual Studio would
/// have suggested, extension and all.</param>
/// <param name="Detail">What it produces, when that is more than the one file.</param>
/// <param name="Fixed">True when the name is the template — a <c>Web.config</c> is not a
/// <c>Web.config</c> under another name, so the picker does not ask.</param>
public sealed record ItemTemplate(
    string Id,
    string Label,
    string Group,
    string DefaultName,
    string? Detail = null,
    bool Fixed = false);

/// <summary>The result of creating one, with every file it made.</summary>
public sealed record ItemTemplateCreation(
    bool Ok, string Message, IReadOnlyList<string> Paths);

/// <summary>
/// What can be added where, and how to add it.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is filtered by what the project actually is, the way Visual Studio's dialog and
/// Rider's Add menu both are: a Form is offered where WinForms is switched on, a Web Form where
/// the project is a legacy System.Web site, a Razor Page where the Razor SDK is in play. Showing
/// everything everywhere would be shorter to write and would put a <c>.aspx</c> in a console app.
/// </para>
/// <para>
/// The code items are scaffolded here rather than shelled out to <c>dotnet new</c>, for one
/// reason: <c>dotnet new class</c> binds its namespace to <c>RootNamespace</c> and nothing else,
/// so a class made in <c>Models/</c> comes out in the project's root namespace. This server
/// already knows the folder-derived answer, including the folders a team's .DotSettings says do
/// not count. The whole-repository singletons — .gitignore, .editorconfig, global.json — go the
/// other way and are left to <c>dotnet new</c>, because their content belongs to the SDK and
/// changes with it.
/// </para>
/// </remarks>
public static class ItemTemplates
{
    /// <summary>
    /// The templates that apply to one node: a solution, a project, or a folder in a project.
    /// </summary>
    public static async Task<IReadOnlyList<ItemTemplate>> ForAsync(
        string targetPath, CancellationToken ct = default)
    {
        if (ScopeOf(targetPath) == ItemTemplateScope.Solution)
            return SolutionTemplates(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);

        if (OwningProject(targetPath) is not { } projectPath)
            return [];

        var traits = await TraitsAsync(projectPath, ct);
        bool atProjectRoot = IsProjectRoot(projectPath, targetPath);

        var templates = new List<ItemTemplate>(CodeTemplates);

        if (traits.Razor)
            templates.AddRange(RazorTemplates);

        if (traits.AspNetCore)
            templates.AddRange(AspNetCoreTemplates);

        if (traits.WinForms)
            templates.AddRange(WinFormsTemplates);

        if (traits.Wpf)
            templates.AddRange(WpfTemplates);

        if (traits.WebForms)
            templates.AddRange(WebFormsTemplates);

        if (traits.TestFramework is not null)
        {
            templates.Add(new ItemTemplate(
                "testClass", "Test Class", "Test", "UnitTest.cs",
                $"A {traits.TestFramework} test class."));
        }

        // Files a project has one of. Offered where they go, which is beside the project file.
        if (atProjectRoot)
        {
            if (traits.AspNetCore)
                templates.Add(new ItemTemplate(
                    "appSettings", "App Settings File", "ASP.NET Core", "appsettings.json",
                    Fixed: true));

            if (traits.WebForms)
                templates.Add(new ItemTemplate(
                    "webConfig", "Web Configuration File", "Web Forms", "Web.config", Fixed: true));

            if (traits.Legacy)
            {
                templates.Add(new ItemTemplate(
                    "appConfig", "Application Configuration File", "C#", "App.config",
                    Fixed: true));
                templates.Add(new ItemTemplate(
                    "assemblyInfo", "Assembly Information File", "C#",
                    "Properties\\AssemblyInfo.cs", Fixed: true));
            }
        }

        return templates;
    }

    /// <summary>Creates one, and returns the files it made.</summary>
    /// <param name="name">What the person typed, extension included or not. Ignored for a
    /// template whose name is fixed.</param>
    public static async Task<ItemTemplateCreation> CreateAsync(
        string templateId, string targetPath, string name, CancellationToken ct = default)
    {
        var template = await FindAsync(templateId, targetPath, ct);

        if (template is null)
            return new ItemTemplateCreation(false, "That template does not apply here.", []);

        if (ScopeOf(targetPath) == ItemTemplateScope.Solution)
            return await CreateSolutionItemAsync(template, targetPath, ct);

        if (OwningProject(targetPath) is not { } projectPath)
            return new ItemTemplateCreation(false, "No project claims this folder.", []);

        string directory = Directory.Exists(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetDirectoryName(Path.GetFullPath(targetPath))!;

        string fileName = template.Fixed
            ? template.DefaultName
            : WithExtension(NameFor(template, name.Trim()), Path.GetExtension(template.DefaultName));

        if (fileName.Length == 0)
            return new ItemTemplateCreation(false, "A name is needed.", []);

        // A fixed name may carry a folder of its own — Properties\AssemblyInfo.cs belongs beside
        // the project whatever folder the menu was opened on.
        string full = template.Fixed && template.DefaultName.Contains(Path.DirectorySeparatorChar)
            ? Path.Combine(Path.GetDirectoryName(projectPath)!, fileName)
            : Path.Combine(directory, fileName);

        var traits = await TraitsAsync(projectPath, ct);
        var files = Build(template, projectPath, full, traits.TestFramework ?? "MSTest");

        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        var result = await ProjectMutationService.AddGeneratedFilesAsync(
            projectPath,
            [.. files.Select(file => file with
            {
                RelativePath = Path.GetRelativePath(projectDirectory, file.RelativePath),
            })],
            ct);

        return new ItemTemplateCreation(
            result.Ok,
            result.Message,
            result.Ok ? [.. files.Select(file => file.RelativePath)] : []);
    }

    private static async Task<ItemTemplate?> FindAsync(
        string templateId, string targetPath, CancellationToken ct) =>
        (await ForAsync(targetPath, ct)).FirstOrDefault(
            template => template.Id == templateId);

    // --- Which templates exist ---

    private static readonly ItemTemplate[] CodeTemplates =
    [
        new("class", "Class", "C#", "Class.cs"),
        new("interface", "Interface", "C#", "Interface.cs"),
        new("record", "Record", "C#", "Record.cs"),
        new("struct", "Struct", "C#", "Struct.cs"),
        new("enum", "Enum", "C#", "Enum.cs"),
        new("file", "Empty C# File", "C#", "File.cs"),
        new("resx", "Resources File", "C#", "Resource.resx"),
        new("textFile", "Text File", "C#", "File.txt"),
        new("jsonFile", "JSON File", "C#", "File.json"),
        new("xmlFile", "XML File", "C#", "File.xml"),
    ];

    private static readonly ItemTemplate[] RazorTemplates =
    [
        new("razorComponent", "Razor Component", "ASP.NET Core", "Component.razor"),
        new("razorView", "Razor View", "ASP.NET Core", "Index.cshtml"),
        new("razorPage", "Razor Page", "ASP.NET Core", "Index.cshtml",
            "The page and its model."),
        new("razorLayout", "Razor Layout", "ASP.NET Core", "_Layout.cshtml"),
        new("viewImports", "Razor View Imports", "ASP.NET Core", "_ViewImports.cshtml",
            Fixed: true),
        new("viewStart", "Razor View Start", "ASP.NET Core", "_ViewStart.cshtml", Fixed: true),
        new("tagHelper", "Tag Helper Class", "ASP.NET Core", "TagHelper.cs"),
    ];

    private static readonly ItemTemplate[] AspNetCoreTemplates =
    [
        new("mvcController", "MVC Controller", "ASP.NET Core", "HomeController.cs"),
        new("apiController", "API Controller", "ASP.NET Core", "ValuesController.cs"),
        new("middleware", "Middleware Class", "ASP.NET Core", "Middleware.cs",
            "The middleware and the extension method that adds it."),
    ];

    private static readonly ItemTemplate[] WinFormsTemplates =
    [
        new("winForm", "Form", "Windows Forms", "Form.cs", "The form and its designer file."),
        new("winUserControl", "User Control", "Windows Forms", "UserControl.cs",
            "The control and its designer file."),
        new("component", "Component Class", "Windows Forms", "Component.cs",
            "The component and its designer file."),
    ];

    private static readonly ItemTemplate[] WpfTemplates =
    [
        new("wpfWindow", "Window", "WPF", "Window.xaml", "The XAML and its code-behind."),
        new("wpfUserControl", "User Control", "WPF", "UserControl.xaml",
            "The XAML and its code-behind."),
        new("wpfPage", "Page", "WPF", "Page.xaml", "The XAML and its code-behind."),
        new("wpfResourceDictionary", "Resource Dictionary", "WPF", "Dictionary.xaml"),
    ];

    private static readonly ItemTemplate[] WebFormsTemplates =
    [
        new("webForm", "Web Form", "Web Forms", "WebForm.aspx",
            "The markup, its code-behind and its designer file."),
        new("webUserControl", "Web User Control", "Web Forms", "WebUserControl.ascx",
            "The markup, its code-behind and its designer file."),
        new("masterPage", "Master Page", "Web Forms", "Site.Master",
            "The markup, its code-behind and its designer file."),
        new("globalAsax", "Global Application Class", "Web Forms", "Global.asax",
            "Global.asax and its code-behind.", Fixed: true),
        new("handler", "Generic Handler", "Web Forms", "Handler.ashx",
            "The handler and its code-behind."),
        new("webService", "Web Service", "Web Forms", "WebService.asmx",
            "The service and its code-behind."),
        new("siteMap", "Site Map", "Web Forms", "Web.sitemap", Fixed: true),
    ];

    /// <summary>
    /// What belongs beside a solution rather than inside a project.
    /// </summary>
    /// <remarks>
    /// Every one of these is a singleton, so one that is already there is not offered again —
    /// a second <c>Directory.Build.props</c> in the same folder is not a thing anybody wants,
    /// and offering it is how you get one.
    /// </remarks>
    private static ItemTemplate[] SolutionTemplates(string directory)
    {
        ItemTemplate[] all =
        [
            new("editorconfig", "EditorConfig File", "Solution", ".editorconfig", Fixed: true),
            new("gitignore", ".gitignore File", "Solution", ".gitignore", Fixed: true),
            new("gitattributes", ".gitattributes File", "Solution", ".gitattributes", Fixed: true),
            new("globaljson", "global.json", "Solution", "global.json", Fixed: true),
            new("buildprops", "Directory.Build.props", "Solution", "Directory.Build.props",
                Fixed: true),
            new("buildtargets", "Directory.Build.targets", "Solution", "Directory.Build.targets",
                Fixed: true),
            new("packagesprops", "Directory.Packages.props", "Solution",
                "Directory.Packages.props", "Central package management.", Fixed: true),
            new("nugetconfig", "NuGet Config", "Solution", "nuget.config", Fixed: true),
            new("tool-manifest", "Local Tool Manifest", "Solution", ".config\\dotnet-tools.json",
                Fixed: true),
            new("readme", "Markdown File", "Solution", "README.md"),
        ];

        return [.. all.Where(template =>
            !File.Exists(Path.Combine(directory, template.DefaultName)))];
    }

    // --- What a project is ---

    private sealed record Traits(
        bool Legacy, bool AspNetCore, bool Razor, bool Blazor, bool Wpf, bool WinForms,
        bool WebForms, string? TestFramework);

    private static async Task<Traits> TraitsAsync(string projectPath, CancellationToken ct)
    {
        var classification = ProjectClassifier.Classify(projectPath);
        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);

        // The evaluation is the better answer — it sees what Directory.Build.props injected — but
        // it is also the one that goes missing when a project will not evaluate, which is exactly
        // the state a legacy or half-restored project is in. The file itself is the fallback.
        bool True(string property) =>
            (evaluation is not null
                && evaluation.Properties.TryGetValue(property, out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            || SaysTrue(projectPath, property);

        bool References(string prefix) =>
            evaluation is not null
            && (evaluation.PackageReferences.Any(package =>
                    package.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || evaluation.AssemblyReferences.Any(reference =>
                    reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        string? sdk = PathHelper.ReadProjectSdk(projectPath);
        bool razorSdk = sdk is not null
            && (sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase)
                || sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase)
                || sdk.Contains("Blazor", StringComparison.OrdinalIgnoreCase));

        // The framework is asked for by name rather than guessed from the project's own name,
        // because "Tests" in a project name says nothing about which attribute a test carries.
        string? testFramework = !classification.IsTestProject
            ? null
            : References("xunit") ? "xUnit"
            : References("NUnit") ? "NUnit"
            : "MSTest";

        return new Traits(
            Legacy: classification.Style == ProjectStyle.Legacy,
            AspNetCore: classification.Kind == AppKind.AspNetCore || razorSdk,
            Razor: razorSdk,
            Blazor: References("Microsoft.AspNetCore.Components"),
            Wpf: True("UseWPF") || HasProjectTypeGuid(projectPath, WpfProjectTypeGuid),
            WinForms: True("UseWindowsForms") || References("System.Windows.Forms"),
            WebForms: classification.Style == ProjectStyle.Legacy
                && classification.Kind == AppKind.AspNetClassic,
            TestFramework: testFramework);
    }

    /// <summary>Whether the project file itself sets a boolean property to true.</summary>
    private static bool SaysTrue(string projectPath, string property)
    {
        try
        {
            return File.ReadAllText(projectPath)
                .Replace(" ", "", StringComparison.Ordinal)
                .Contains($"<{property}>true<", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const string WpfProjectTypeGuid = "60dc8134-eba5-43b8-bcc9-bb4bc16c2548";

    private static bool HasProjectTypeGuid(string projectPath, string guid)
    {
        try
        {
            return File.ReadAllText(projectPath).Contains(guid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- Where the template was asked for ---

    private static ItemTemplateScope ScopeOf(string targetPath) =>
        Path.GetExtension(targetPath).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf"
            ? ItemTemplateScope.Solution
            : ItemTemplateScope.Project;

    private static string? OwningProject(string targetPath)
    {
        string full = Path.GetFullPath(targetPath);

        if (Path.GetExtension(full).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            return full;

        // FindOwningProject searches upwards from a file's folder, so a folder handed to it
        // starts one level too high — and a project's own root folder is exactly the case the
        // New menu is opened on most.
        return ProjectMutationService.FindOwningProject(
            Directory.Exists(full) ? Path.Combine(full, "_") : full);
    }

    private static bool IsProjectRoot(string projectPath, string targetPath)
    {
        string full = Path.GetFullPath(targetPath);
        string directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;

        return string.Equals(
            directory.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file name a template makes of what was typed.
    /// </summary>
    /// <remarks>
    /// Only the interface changes it, and only to add the I the type is going to have anyway —
    /// a file called Basket.cs holding an IBasket is a mismatch somebody has to fix by hand.
    /// </remarks>
    private static string NameFor(ItemTemplate template, string name) =>
        template.Id == "interface" && name.Length > 0 && !name.StartsWith('I')
            ? "I" + name
            : name;

    private static string WithExtension(string name, string extension) =>
        name.Length == 0 || Path.GetExtension(name).Length > 0
            ? name
            : name + extension;

    // --- Solution-level items, which the SDK owns ---

    /// <summary>
    /// Runs <c>dotnet new</c> in the solution's folder.
    /// </summary>
    /// <remarks>
    /// These files are the SDK's, not ours: a .gitignore is five hundred lines that change with
    /// every SDK, and an .editorconfig full of dotnet_diagnostic rules is a document nobody wants
    /// a second, staler copy of. Only the ones the SDK has no template for are written here.
    /// </remarks>
    private static async Task<ItemTemplateCreation> CreateSolutionItemAsync(
        ItemTemplate template, string solutionPath, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        if (template.Id == "readme")
        {
            string readme = Path.Combine(directory, "README.md");

            if (File.Exists(readme))
                return new ItemTemplateCreation(false, "README.md already exists.", []);

            await File.WriteAllTextAsync(
                readme,
                $"# {Path.GetFileNameWithoutExtension(solutionPath)}{Environment.NewLine}",
                ct);

            return new ItemTemplateCreation(true, "Created README.md.", [readme]);
        }

        var (exitCode, output) = await RunDotNetNewAsync(template.Id, directory, ct);

        if (exitCode != 0)
        {
            return new ItemTemplateCreation(
                false,
                output.Length > 0 ? output : $"`dotnet new {template.Id}` failed.",
                []);
        }

        string created = Path.Combine(directory, template.DefaultName);

        return new ItemTemplateCreation(
            true,
            $"Created {template.DefaultName}.",
            File.Exists(created) ? [created] : []);
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetNewAsync(
        string shortName, string directory, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add(shortName);
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
                return (1, "Could not start dotnet.");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);

            string output = (await stdout + Environment.NewLine + await stderr).Trim();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (1, $"Could not run `dotnet new {shortName}`: {ex.Message}");
        }
    }

    // --- What each template writes ---

    private static IReadOnlyList<ProjectMutationService.GeneratedFile> Build(
        ItemTemplate template, string projectPath, string fullPath, string testFramework)
    {
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string @namespace = ProjectMutationService.InferNamespace(
            projectPath, projectDirectory, fullPath);
        string name = Path.GetFileNameWithoutExtension(fullPath);

        // A .cshtml page's model is named after the page, and a Web Form's designer after its
        // markup — the base name for both is the file without any of its extensions.
        string bare = name.Contains('.') ? name[..name.IndexOf('.')] : name;

        ProjectMutationService.GeneratedFile File(
            string path, string contents, string? itemType = null,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            new(path, contents, itemType, metadata);

        static Dictionary<string, string> Meta(params (string Key, string Value)[] pairs) =>
            pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        string codeBehind = fullPath + ".cs";
        string designer = fullPath + ".designer.cs";
        string markupName = Path.GetFileName(fullPath);

        return template.Id switch
        {
            "class" or "interface" or "record" or "struct" or "enum" or "file" =>
                [File(fullPath, Types.Code(template.Id, @namespace, name))],

            "resx" => [File(fullPath, Resources.Empty, "EmbeddedResource")],
            "textFile" => [File(fullPath, "")],
            "jsonFile" => [File(fullPath, "{}" + Environment.NewLine)],
            "xmlFile" => [File(fullPath, Xml.Empty(bare))],

            "mvcController" => [File(fullPath, AspNet.MvcController(@namespace, name))],
            "apiController" => [File(fullPath, AspNet.ApiController(@namespace, name))],
            "middleware" => [File(fullPath, AspNet.Middleware(@namespace, name))],
            "tagHelper" => [File(fullPath, AspNet.TagHelper(@namespace, name))],
            "razorComponent" => [File(fullPath, AspNet.RazorComponent(name))],
            "razorView" => [File(fullPath, AspNet.RazorView(bare))],
            "razorLayout" => [File(fullPath, AspNet.RazorLayout())],
            "viewImports" => [File(fullPath, AspNet.ViewImports(RootNamespace(projectPath)))],
            "viewStart" => [File(fullPath, AspNet.ViewStart())],
            "razorPage" =>
            [
                File(fullPath, AspNet.RazorPage(@namespace, bare)),
                File(codeBehind, AspNet.RazorPageModel(@namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName))),
            ],
            "appSettings" => [File(fullPath, AspNet.AppSettings())],

            "winForm" =>
            [
                File(fullPath, WinForms.Form(@namespace, name), "Compile",
                    Meta(("SubType", "Form"))),
                File(DesignerFor(fullPath), WinForms.FormDesigner(@namespace, name), "Compile",
                    Meta(("DependentUpon", Path.GetFileName(fullPath)))),
            ],
            "winUserControl" =>
            [
                File(fullPath, WinForms.UserControl(@namespace, name), "Compile",
                    Meta(("SubType", "UserControl"))),
                File(DesignerFor(fullPath), WinForms.ControlDesigner(@namespace, name), "Compile",
                    Meta(("DependentUpon", Path.GetFileName(fullPath)))),
            ],
            "component" =>
            [
                File(fullPath, WinForms.Component(@namespace, name), "Compile",
                    Meta(("SubType", "Component"))),
                File(DesignerFor(fullPath), WinForms.ComponentDesigner(@namespace, name), "Compile",
                    Meta(("DependentUpon", Path.GetFileName(fullPath)))),
            ],

            "wpfWindow" or "wpfPage" or "wpfUserControl" =>
            [
                File(fullPath, Wpf.Markup(template.Id, @namespace, bare), "Page",
                    Meta(("Generator", "MSBuild:Compile"), ("SubType", "Designer"))),
                File(codeBehind, Wpf.CodeBehind(template.Id, @namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName), ("SubType", "Code"))),
            ],
            "wpfResourceDictionary" =>
            [
                File(fullPath, Wpf.ResourceDictionary(), "Page",
                    Meta(("Generator", "MSBuild:Compile"), ("SubType", "Designer"))),
            ],

            "webForm" or "webUserControl" or "masterPage" =>
            [
                File(fullPath, WebForms.Markup(template.Id, @namespace, bare), "Content"),
                File(codeBehind, WebForms.CodeBehind(template.Id, @namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName), ("SubType", "ASPXCodeBehind"))),
                File(designer, WebForms.Designer(@namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName))),
            ],
            "globalAsax" =>
            [
                File(fullPath, WebForms.GlobalAsax(@namespace), "Content"),
                File(codeBehind, WebForms.GlobalAsaxCode(@namespace), "Compile",
                    Meta(("DependentUpon", markupName))),
            ],
            "handler" =>
            [
                File(fullPath, WebForms.Handler(@namespace, bare), "Content"),
                File(codeBehind, WebForms.HandlerCode(@namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName))),
            ],
            "webService" =>
            [
                File(fullPath, WebForms.Service(@namespace, bare), "Content"),
                File(codeBehind, WebForms.ServiceCode(@namespace, bare), "Compile",
                    Meta(("DependentUpon", markupName))),
            ],
            "webConfig" => [File(fullPath, WebForms.WebConfig(), "Content")],
            "siteMap" => [File(fullPath, WebForms.SiteMap(), "Content")],

            "appConfig" => [File(fullPath, Xml.AppConfig(), "None")],
            "assemblyInfo" => [File(fullPath, Types.AssemblyInfo(RootNamespace(projectPath)))],

            "testClass" => [File(fullPath, Tests.Class(@namespace, name, testFramework))],

            _ => [File(fullPath, "")],
        };
    }

    /// <summary>The designer half of a WinForms file: <c>Form1.Designer.cs</c> beside its own.</summary>
    private static string DesignerFor(string fullPath) =>
        Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            Path.GetFileNameWithoutExtension(fullPath) + ".Designer.cs");

    private static string RootNamespace(string projectPath) =>
        ProjectMutationService.InferNamespace(
            projectPath,
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "File.cs"));

    // --- The content itself ---

    private static class Types
    {
        public static string Code(string id, string @namespace, string name)
        {
            if (id == "file")
                return Header(@namespace);

            string keyword = id switch
            {
                "interface" => "interface",
                "record" => "record",
                "struct" => "struct",
                "enum" => "enum",
                _ => "class",
            };

            // An interface named Foo is almost never wanted; IFoo is.
            if (id == "interface" && !name.StartsWith('I'))
                name = "I" + name;

            var sb = new StringBuilder(Header(@namespace));
            sb.AppendLine($"public {keyword} {name}");
            sb.AppendLine("{");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string AssemblyInfo(string @namespace) =>
            $"""
            using System.Reflection;
            using System.Runtime.InteropServices;

            [assembly: AssemblyTitle("{@namespace}")]
            [assembly: AssemblyProduct("{@namespace}")]
            [assembly: ComVisible(false)]
            [assembly: AssemblyVersion("1.0.0.0")]
            [assembly: AssemblyFileVersion("1.0.0.0")]

            """;

        public static string Header(string @namespace) =>
            @namespace.Length == 0
                ? ""
                : $"namespace {@namespace};{Environment.NewLine}{Environment.NewLine}";
    }

    private static class Tests
    {
        public static string Class(string @namespace, string name, string framework) =>
            framework switch
            {
                "xUnit" =>
                    $$"""
                    using Xunit;

                    namespace {{@namespace}};

                    public class {{name}}
                    {
                        [Fact]
                        public void Test1()
                        {
                        }
                    }

                    """,
                "NUnit" =>
                    $$"""
                    using NUnit.Framework;

                    namespace {{@namespace}};

                    [TestFixture]
                    public class {{name}}
                    {
                        [Test]
                        public void Test1()
                        {
                        }
                    }

                    """,
                _ =>
                    $$"""
                    using Microsoft.VisualStudio.TestTools.UnitTesting;

                    namespace {{@namespace}};

                    [TestClass]
                    public class {{name}}
                    {
                        [TestMethod]
                        public void TestMethod1()
                        {
                        }
                    }

                    """,
            };
    }

    private static class AspNet
    {
        public static string MvcController(string @namespace, string name) =>
            $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace {{@namespace}};

            public class {{name}} : Controller
            {
                public IActionResult Index()
                {
                    return View();
                }
            }

            """;

        public static string ApiController(string @namespace, string name) =>
            $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace {{@namespace}};

            [ApiController]
            [Route("[controller]")]
            public class {{name}} : ControllerBase
            {
            }

            """;

        public static string Middleware(string @namespace, string name) =>
            $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;

            namespace {{@namespace}};

            public class {{name}}
            {
                private readonly RequestDelegate _next;

                public {{name}}(RequestDelegate next)
                {
                    _next = next;
                }

                public Task InvokeAsync(HttpContext context)
                {
                    return _next(context);
                }
            }

            /// <summary>Adds <see cref="{{name}}"/> to the pipeline.</summary>
            public static class {{name}}Extensions
            {
                public static IApplicationBuilder Use{{name}}(this IApplicationBuilder builder)
                {
                    return builder.UseMiddleware<{{name}}>();
                }
            }

            """;

        public static string TagHelper(string @namespace, string name) =>
            $$"""
            using Microsoft.AspNetCore.Razor.TagHelpers;

            namespace {{@namespace}};

            public class {{name}} : TagHelper
            {
                public override void Process(TagHelperContext context, TagHelperOutput output)
                {
                }
            }

            """;

        public static string RazorComponent(string name) =>
            $"<h3>{name}</h3>{Environment.NewLine}{Environment.NewLine}@code {{{Environment.NewLine}}}{Environment.NewLine}";

        public static string RazorView(string name) =>
            """
            @{
                ViewData["Title"] = "$name";
            }

            <h1>@ViewData["Title"]</h1>

            """.Replace("$name", name, StringComparison.Ordinal);

        public static string RazorLayout() =>
            """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>@ViewData["Title"]</title>
            </head>
            <body>
                @RenderBody()
                @await RenderSectionAsync("Scripts", required: false)
            </body>
            </html>

            """;

        public static string ViewImports(string rootNamespace) =>
            $"""
            @using {rootNamespace}
            @addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers

            """;

        public static string ViewStart() =>
            """
            @{
                Layout = "_Layout";
            }

            """;

        public static string RazorPage(string @namespace, string name) =>
            """
            @page
            @model $ns.$nameModel
            @{
                ViewData["Title"] = "$name";
            }

            <h1>@ViewData["Title"]</h1>

            """
            .Replace("$ns", @namespace, StringComparison.Ordinal)
            .Replace("$name", name, StringComparison.Ordinal);

        public static string RazorPageModel(string @namespace, string name) =>
            $$"""
            using Microsoft.AspNetCore.Mvc.RazorPages;

            namespace {{@namespace}};

            public class {{name}}Model : PageModel
            {
                public void OnGet()
                {
                }
            }

            """;

        public static string AppSettings() =>
            """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning"
                }
              }
            }

            """;
    }

    private static class WinForms
    {
        public static string Form(string @namespace, string name) =>
            Partial(@namespace, name, "System.Windows.Forms.Form", initialize: true);

        public static string UserControl(string @namespace, string name) =>
            Partial(@namespace, name, "System.Windows.Forms.UserControl", initialize: true);

        public static string Component(string @namespace, string name) =>
            Partial(@namespace, name, "System.ComponentModel.Component", initialize: true);

        private static string Partial(
            string @namespace, string name, string baseType, bool initialize) =>
            $$"""
            namespace {{@namespace}};

            public partial class {{name}} : {{baseType}}
            {
                public {{name}}()
                {
                    {{(initialize ? "InitializeComponent();" : "")}}
                }
            }

            """;

        public static string FormDesigner(string @namespace, string name) =>
            Designer(@namespace, name, """
                        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
                        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
                        this.ClientSize = new System.Drawing.Size(800, 450);
                        this.Text = "{{name}}";
            """);

        public static string ControlDesigner(string @namespace, string name) =>
            Designer(@namespace, name, """
                        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
                        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            """);

        public static string ComponentDesigner(string @namespace, string name) =>
            Designer(@namespace, name, "");

        /// <summary>
        /// The half the designer owns.
        /// </summary>
        /// <remarks>
        /// Written the way the Windows Forms designer writes it, because the designer is what
        /// edits it next: the <c>components</c> field, the region, and <c>InitializeComponent</c>
        /// between <c>SuspendLayout</c> and <c>ResumeLayout</c> are what it looks for, and a file
        /// missing them is one it refuses to open.
        /// </remarks>
        private static string Designer(string @namespace, string name, string body) =>
            $$"""
            namespace {{@namespace}};

            partial class {{name}}
            {
                /// <summary>Required designer variable.</summary>
                private System.ComponentModel.IContainer components = null;

                /// <summary>Clean up any resources being used.</summary>
                protected override void Dispose(bool disposing)
                {
                    if (disposing && (components != null))
                    {
                        components.Dispose();
                    }
                    base.Dispose(disposing);
                }

                #region Component Designer generated code

                /// <summary>
                /// Required method for Designer support - do not modify
                /// the contents of this method with the code editor.
                /// </summary>
                private void InitializeComponent()
                {
                    this.SuspendLayout();
            {{body.Replace("{{name}}", name, StringComparison.Ordinal)}}
                    this.ResumeLayout(false);
                }

                #endregion
            }

            """;
    }

    private static class Wpf
    {
        private static string RootElement(string id) => id switch
        {
            "wpfWindow" => "Window",
            "wpfPage" => "Page",
            _ => "UserControl",
        };

        public static string Markup(string id, string @namespace, string name)
        {
            string root = RootElement(id);
            string size = root == "UserControl"
                ? @"d:DesignHeight=""450"" d:DesignWidth=""800"""
                : $@"Title=""{name}"" Height=""450"" Width=""800""";

            return $"""
                <{root} x:Class="{@namespace}.{name}"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                        xmlns:local="clr-namespace:{@namespace}"
                        mc:Ignorable="d"
                        {size}>
                    <Grid>
                    </Grid>
                </{root}>

                """;
        }

        public static string CodeBehind(string id, string @namespace, string name)
        {
            string root = RootElement(id);

            return $$"""
                using System.Windows.Controls;

                namespace {{@namespace}};

                public partial class {{name}} : System.Windows.{{(root == "Window" ? "Window" : "Controls." + root)}}
                {
                    public {{name}}()
                    {
                        InitializeComponent();
                    }
                }

                """;
        }

        public static string ResourceDictionary() =>
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            </ResourceDictionary>

            """;
    }

    private static class WebForms
    {
        public static string Markup(string id, string @namespace, string name) => id switch
        {
            "webUserControl" =>
                $"""
                <%@ Control Language="C#" AutoEventWireup="true" CodeBehind="{name}.ascx.cs" Inherits="{@namespace}.{name}" %>

                """,
            "masterPage" =>
                $"""
                <%@ Master Language="C#" AutoEventWireup="true" CodeBehind="{name}.Master.cs" Inherits="{@namespace}.{name}" %>

                <!DOCTYPE html>
                <html>
                <head runat="server">
                    <title></title>
                    <asp:ContentPlaceHolder ID="head" runat="server" />
                </head>
                <body>
                    <form id="form1" runat="server">
                        <asp:ContentPlaceHolder ID="MainContent" runat="server" />
                    </form>
                </body>
                </html>

                """,
            _ =>
                $"""
                <%@ Page Language="C#" AutoEventWireup="true" CodeBehind="{name}.aspx.cs" Inherits="{@namespace}.{name}" %>

                <!DOCTYPE html>
                <html>
                <head runat="server">
                    <title></title>
                </head>
                <body>
                    <form id="form1" runat="server">
                        <div>
                        </div>
                    </form>
                </body>
                </html>

                """,
        };

        public static string CodeBehind(string id, string @namespace, string name)
        {
            string baseType = id switch
            {
                "webUserControl" => "System.Web.UI.UserControl",
                "masterPage" => "System.Web.UI.MasterPage",
                _ => "System.Web.UI.Page",
            };

            return $$"""
                using System;

                namespace {{@namespace}}
                {
                    public partial class {{name}} : {{baseType}}
                    {
                        protected void Page_Load(object sender, EventArgs e)
                        {
                        }
                    }
                }

                """;
        }

        /// <summary>
        /// The designer half, left empty on purpose.
        /// </summary>
        /// <remarks>
        /// Every control in the markup gets a field here, and the server already regenerates that
        /// from the markup — writing a stale guess would only be something to overwrite.
        /// </remarks>
        public static string Designer(string @namespace, string name) =>
            $$"""
            //------------------------------------------------------------------------------
            // <auto-generated>
            //     This code was generated by a tool.
            // </auto-generated>
            //------------------------------------------------------------------------------

            namespace {{@namespace}}
            {
                public partial class {{name}}
                {
                }
            }

            """;

        public static string GlobalAsax(string @namespace) =>
            $"""
            <%@ Application Codebehind="Global.asax.cs" Inherits="{@namespace}.Global" Language="C#" %>

            """;

        public static string GlobalAsaxCode(string @namespace) =>
            $$"""
            using System;

            namespace {{@namespace}}
            {
                public class Global : System.Web.HttpApplication
                {
                    protected void Application_Start(object sender, EventArgs e)
                    {
                    }
                }
            }

            """;

        public static string Handler(string @namespace, string name) =>
            $"""
            <%@ WebHandler Language="C#" CodeBehind="{name}.ashx.cs" Class="{@namespace}.{name}" %>

            """;

        public static string HandlerCode(string @namespace, string name) =>
            $$"""
            using System.Web;

            namespace {{@namespace}}
            {
                public class {{name}} : IHttpHandler
                {
                    public bool IsReusable => false;

                    public void ProcessRequest(HttpContext context)
                    {
                    }
                }
            }

            """;

        public static string Service(string @namespace, string name) =>
            $"""
            <%@ WebService Language="C#" CodeBehind="{name}.asmx.cs" Class="{@namespace}.{name}" %>

            """;

        public static string ServiceCode(string @namespace, string name) =>
            $$"""
            using System.Web.Services;

            namespace {{@namespace}}
            {
                [WebService(Namespace = "http://tempuri.org/")]
                [WebServiceBinding(ConformsTo = WsiProfiles.BasicProfile1_1)]
                public class {{name}} : WebService
                {
                    [WebMethod]
                    public string HelloWorld()
                    {
                        return "Hello World";
                    }
                }
            }

            """;

        public static string WebConfig() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <system.web>
                <compilation debug="true" targetFramework="4.8" />
                <httpRuntime targetFramework="4.8" />
              </system.web>
            </configuration>

            """;

        public static string SiteMap() =>
            """
            <?xml version="1.0" encoding="utf-8" ?>
            <siteMap xmlns="http://schemas.microsoft.com/AspNet/SiteMap-File-1.0">
              <siteMapNode url="" title="" description="">
              </siteMapNode>
            </siteMap>

            """;
    }

    private static class Xml
    {
        public static string Empty(string root) =>
            $"""
            <?xml version="1.0" encoding="utf-8" ?>
            <{root}>
            </{root}>

            """;

        public static string AppConfig() =>
            """
            <?xml version="1.0" encoding="utf-8" ?>
            <configuration>
            </configuration>

            """;
    }

    private static class Resources
    {
        /// <summary>
        /// An empty .resx, schema and all.
        /// </summary>
        /// <remarks>
        /// The header block is not decoration: the resx reader looks for the resmimetype and
        /// version values and refuses a file without them, so an "empty" .resx that is really an
        /// empty file is one Visual Studio, Rider and MSBuild all reject.
        /// </remarks>
        public const string Empty =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
                <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
                <xsd:element name="root" msdata:IsDataSet="true">
                  <xsd:complexType>
                    <xsd:choice maxOccurs="unbounded">
                      <xsd:element name="data">
                        <xsd:complexType>
                          <xsd:sequence>
                            <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                            <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
                          </xsd:sequence>
                          <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
                          <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
                          <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
                          <xsd:attribute ref="xml:space" />
                        </xsd:complexType>
                      </xsd:element>
                      <xsd:element name="resheader">
                        <xsd:complexType>
                          <xsd:sequence>
                            <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                          </xsd:sequence>
                          <xsd:attribute name="name" type="xsd:string" use="required" />
                        </xsd:complexType>
                      </xsd:element>
                    </xsd:choice>
                  </xsd:complexType>
                </xsd:element>
              </xsd:schema>
              <resheader name="resmimetype">
                <value>text/microsoft-resx</value>
              </resheader>
              <resheader name="version">
                <value>2.0</value>
              </resheader>
              <resheader name="reader">
                <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
              </resheader>
              <resheader name="writer">
                <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
              </resheader>
            </root>

            """;
    }
}
