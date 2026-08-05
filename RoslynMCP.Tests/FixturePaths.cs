namespace RoslynMCP.Tests;

/// <summary>
/// Resolves paths to the fixture project files shipped alongside the test assembly.
/// </summary>
internal static class FixturePaths
{
    private static readonly string s_fixturesRoot = FindFixturesRoot();

    public static string SampleProjectDir => Path.Combine(s_fixturesRoot, "SampleProject");
    public static string AlternateProjectFile => Path.Combine(SampleProjectDir, "Aardvark.Empty.csproj");
    public static string SampleProjectFile => Path.Combine(SampleProjectDir, "SampleProject.csproj");
    public static string CalculatorFile => Path.Combine(SampleProjectDir, "Calculator.cs");
    public static string ExternalReferencesFile => Path.Combine(SampleProjectDir, "ExternalReferences.cs");
    public static string FrameworkReferencesFile => Path.Combine(SampleProjectDir, "FrameworkReferences.cs");
    public static string ManyUsagesFile => Path.Combine(SampleProjectDir, "ManyUsages.cs");
    public static string ResultFile => Path.Combine(SampleProjectDir, "Models", "Result.cs");
    public static string ServicesFile => Path.Combine(SampleProjectDir, "Services.cs");
    public static string OutlineShowcaseFile => Path.Combine(SampleProjectDir, "OutlineShowcase.cs");
    public static string TextUtilitiesFile => Path.Combine(SampleProjectDir, "TextUtilities.cs");
    public static string WarningsFile => Path.Combine(SampleProjectDir, "Warnings.cs");
    public static string VarUsagesFile => Path.Combine(SampleProjectDir, "VarUsages.cs");
    public static string WorkspaceRefreshTargetFile => Path.Combine(SampleProjectDir, "WorkspaceRefreshTarget.cs");
    public static string BrokenProjectDir => Path.Combine(s_fixturesRoot, "BrokenProject");
    public static string BrokenProjectFile => Path.Combine(BrokenProjectDir, "BrokenProject.csproj");
    public static string BrokenSyntaxFile => Path.Combine(BrokenProjectDir, "BrokenSyntax.cs");
    public static string BrokenSemanticFile => Path.Combine(BrokenProjectDir, "BrokenSemantic.cs");

    public static string LegacyProjectDir => Path.Combine(s_fixturesRoot, "LegacyProject");
    public static string LegacyProjectFile => Path.Combine(LegacyProjectDir, "LegacyProject.csproj");
    public static string LegacyCalculatorFile => Path.Combine(LegacyProjectDir, "Calculator.cs");
    public static string LegacyCustomerFile => Path.Combine(LegacyProjectDir, "Models", "Customer.cs");

    public static string AspxProjectDir => Path.Combine(s_fixturesRoot, "AspxProject");
    public static string AspxProjectFile => Path.Combine(AspxProjectDir, "AspxProject.csproj");
    public static string DefaultAspxFile => Path.Combine(AspxProjectDir, "Default.aspx");
    public static string HeaderControlFile => Path.Combine(AspxProjectDir, "Controls", "HeaderControl.ascx");
    public static string OrderItemsAscxFile => Path.Combine(AspxProjectDir, "Controls", "OrderItems.ascx");
    public static string SiteMasterFile => Path.Combine(AspxProjectDir, "Site.master");
    public static string DataServiceFile => Path.Combine(AspxProjectDir, "DataService.asmx");
    public static string ImageHandlerFile => Path.Combine(AspxProjectDir, "ImageHandler.ashx");
    public static string AspxPageHelperFile => Path.Combine(AspxProjectDir, "PageHelper.cs");
    public static string AspxWebConfigFile => Path.Combine(AspxProjectDir, "web.config");
    public static string WebFormsSiteDir => Path.Combine(s_fixturesRoot, "WebFormsSite");
    public static string WebFormsSiteFile => Path.Combine(WebFormsSiteDir, "WebFormsSite.csproj");
    public static string DbmlProjectDir => Path.Combine(s_fixturesRoot, "DbmlProject");
    public static string ShopDbmlFile => Path.Combine(DbmlProjectDir, "Shop.dbml");
    public static string DesignerAspxFile => Path.Combine(AspxProjectDir, "Designer.aspx");
    public static string DesignerAspxDesignerFile => Path.Combine(AspxProjectDir, "Designer.aspx.designer.cs");
    public static string DesignerAspxCodeBehindFile => Path.Combine(AspxProjectDir, "Designer.aspx.cs");
    public static string EventWiringAspxFile => Path.Combine(AspxProjectDir, "EventWiring.aspx");
    public static string EventWiringCodeBehindFile => Path.Combine(AspxProjectDir, "EventWiring.aspx.cs");
    public static string RepeaterAspxFile => Path.Combine(AspxProjectDir, "Repeater.aspx");
    public static string RepeaterCodeBehindFile => Path.Combine(AspxProjectDir, "Repeater.aspx.cs");

    /// <summary>
    /// The localization fixture: expression builders in both shapes the parser produces, an
    /// implicit-localization key in each of its two spellings, and a control that really declares
    /// a <c>ResourceKey</c> property beside them.
    /// </summary>
    public static string LocalizedAspxFile => Path.Combine(AspxProjectDir, "Localized.aspx");
    public static string LocalizedCodeBehindFile => Path.Combine(AspxProjectDir, "Localized.aspx.cs");
    public static string DnnLocalizedAscxFile => Path.Combine(AspxProjectDir, "Controls", "DnnLocalized.ascx");
    public static string AspxResourceHelperFile => Path.Combine(AspxProjectDir, "ResourceHelper.cs");

    public static string GlobalResourcesDir => Path.Combine(AspxProjectDir, "App_GlobalResources");
    public static string GlobalStringsResxFile => Path.Combine(GlobalResourcesDir, "Strings.resx");
    public static string GlobalStringsDutchResxFile => Path.Combine(GlobalResourcesDir, "Strings.nl-NL.resx");
    public static string GlobalStringsDesignerFile => Path.Combine(GlobalResourcesDir, "Strings.Designer.cs");

    public static string LocalResourcesDir => Path.Combine(AspxProjectDir, "App_LocalResources");
    public static string DefaultAspxResxFile => Path.Combine(LocalResourcesDir, "Default.aspx.resx");
    public static string SharedResourcesResxFile => Path.Combine(LocalResourcesDir, "SharedResources.resx");

    /// <summary>The five-file family: the neutral file, one translation, DNN's two customization
    /// ranks, and the combination of a translation with the higher rank.</summary>
    public static string LocalizedResxFile => Path.Combine(LocalResourcesDir, "Localized.aspx.resx");
    public static string LocalizedDutchResxFile => Path.Combine(LocalResourcesDir, "Localized.aspx.nl-NL.resx");
    public static string LocalizedHostResxFile => Path.Combine(LocalResourcesDir, "Localized.aspx.Host.resx");
    public static string LocalizedPortalResxFile => Path.Combine(LocalResourcesDir, "Localized.aspx.Portal-3.resx");
    public static string LocalizedDutchPortalResxFile =>
        Path.Combine(LocalResourcesDir, "Localized.aspx.nl-NL.Portal-3.resx");

    public static string DnnLocalizedResxFile =>
        Path.Combine(AspxProjectDir, "Controls", "App_LocalResources", "DnnLocalized.ascx.resx");

    public static string BlazorProjectDir => Path.Combine(s_fixturesRoot, "BlazorProject");
    public static string BlazorProjectFile => Path.Combine(BlazorProjectDir, "BlazorProject.csproj");
    public static string BlazorAppHelperFile => Path.Combine(BlazorProjectDir, "AppHelper.cs");
    public static string CounterRazorFile => Path.Combine(BlazorProjectDir, "Counter.razor");
    public static string WeatherRazorFile => Path.Combine(BlazorProjectDir, "Weather.razor");

    public static string DebugTestProjectDir => Path.Combine(s_fixturesRoot, "DebugTestProject");
    public static string DebugTestProjectFile => Path.Combine(DebugTestProjectDir, "DebugTestProject.csproj");
    public static string DebugCalculatorFile => Path.Combine(DebugTestProjectDir, "Calculator.cs");
    public static string DebugCalculatorTestsFile => Path.Combine(DebugTestProjectDir, "CalculatorTests.cs");

    public static string MultiSolutionDir => Path.Combine(s_fixturesRoot, "MultiSolution");
    public static string MultiSolutionFile => Path.Combine(MultiSolutionDir, "MultiSolution.sln");
    public static string MultiProjectAFile => Path.Combine(MultiSolutionDir, "ProjectA", "ProjectA.csproj");
    public static string MultiProjectBFile => Path.Combine(MultiSolutionDir, "ProjectB", "ProjectB.csproj");

    /// <summary>Central Package Management. Never restored: the point is that versions resolve
    /// from the evaluated item model alone.</summary>
    public static string CpmSolutionDir => Path.Combine(s_fixturesRoot, "CpmSolution");
    public static string CpmDirectoryPackagesProps => Path.Combine(CpmSolutionDir, "Directory.Packages.props");
    public static string CpmManagedProjectFile => Path.Combine(CpmSolutionDir, "Managed", "Managed.csproj");
    public static string CpmOverriddenProjectFile => Path.Combine(CpmSolutionDir, "Overridden", "Overridden.csproj");
    public static string CpmMultiTfmProjectFile => Path.Combine(CpmSolutionDir, "MultiTfm", "MultiTfm.csproj");

    public static string SourceGenFixtureDir => Path.Combine(s_fixturesRoot, "SourceGenFixture");
    public static string SourceGenGeneratorProjectFile => Path.Combine(SourceGenFixtureDir, "Generator", "Generator.csproj");
    public static string SourceGenGeneratorSourceFile => Path.Combine(SourceGenFixtureDir, "Generator", "HelloGenerator.cs");
    public static string SourceGenGeneratorDll => Path.Combine(SourceGenFixtureDir, "Generator", "bin", "Debug", "netstandard2.0", "Generator.dll");
    public static string SourceGenConsumerProjectFile => Path.Combine(SourceGenFixtureDir, "Consumer", "Consumer.csproj");

    /// <summary>Protobuf fixture. The C# under Generated\ is real protoc + grpc_csharp_plugin
    /// output committed as ordinary source, mirroring the proto directory tree the way
    /// obj\Debug\&lt;tfm&gt;\ does, so the project builds without Grpc.Tools or protoc.</summary>
    public static string ProtoProjectDir => Path.Combine(s_fixturesRoot, "ProtoProject");
    public static string ProtoProjectFile => Path.Combine(ProtoProjectDir, "ProtoProject.csproj");
    public static string CommonTypesProtoFile => Path.Combine(ProtoProjectDir, "common", "types.proto");
    public static string WidgetTypesProtoFile => Path.Combine(ProtoProjectDir, "widgets", "types.proto");
    public static string WidgetsProtoFile => Path.Combine(ProtoProjectDir, "widgets", "widgets.proto");
    public static string ProtoGeneratedDir => Path.Combine(ProtoProjectDir, "Generated");
    public static string CommonTypesGeneratedFile => Path.Combine(ProtoGeneratedDir, "common", "Types.cs");
    public static string WidgetTypesGeneratedFile => Path.Combine(ProtoGeneratedDir, "widgets", "Types.cs");
    public static string WidgetsGeneratedFile => Path.Combine(ProtoGeneratedDir, "widgets", "Widgets.cs");
    public static string WidgetsGrpcGeneratedFile => Path.Combine(ProtoGeneratedDir, "widgets", "WidgetsGrpc.cs");

    /// <summary>Hand-written implementation of WidgetService, the go-to-implementation target.</summary>
    public static string WidgetGrpcServiceFile => Path.Combine(ProtoProjectDir, "WidgetGrpcService.cs");

    /// <summary>Hand-written consumer of the generated client, holding the find-usages call sites.</summary>
    public static string WidgetClientCallerFile => Path.Combine(ProtoProjectDir, "WidgetClientCaller.cs");

    /// <summary>A valid .proto that nothing imports and nothing generated C# for: the
    /// "never built" degraded path.</summary>
    public static string OrphanProtoFile => Path.Combine(ProtoProjectDir, "NoGenerated", "orphan.proto");

    /// <summary>A second protobuf fixture whose index is empty for the whole project: it
    /// references the runtime and lists its schema as a &lt;Protobuf&gt; item, but nothing has
    /// generated C# from it. ProtoProject cannot stand in for this — it has generated code for
    /// its other files, so a project-wide "nothing was built" is never true there.</summary>
    public static string ProtoNeverBuiltProjectDir => Path.Combine(s_fixturesRoot, "ProtoNeverBuiltProject");
    public static string ProtoNeverBuiltProjectFile => Path.Combine(ProtoNeverBuiltProjectDir, "ProtoNeverBuiltProject.csproj");
    public static string ContractsProtoFile => Path.Combine(ProtoNeverBuiltProjectDir, "contracts.proto");

    /// <summary>
    /// The mediator fixture. Both libraries are stubbed in-tree so it builds offline, but the
    /// generator emitting <c>SenderExtensions</c> is real — whether a reference search reaches into
    /// source-generated documents is the assumption the Zapto half of the pack rests on, and
    /// checked-in output would test nothing.
    /// </summary>
    public static string MediatorProjectDir => Path.Combine(s_fixturesRoot, "MediatorProject");
    public static string MediatorProjectFile => Path.Combine(MediatorProjectDir, "MediatorProject.csproj");

    /// <summary>The requests, their handlers, and the pipeline behaviour that must never be
    /// mistaken for one.</summary>
    public static string MediatorOrdersFile => Path.Combine(MediatorProjectDir, "Orders.cs");

    /// <summary>A notification with two handlers, because several is normal rather than
    /// ambiguous.</summary>
    public static string MediatorNotificationsFile => Path.Combine(MediatorProjectDir, "Notifications.cs");

    /// <summary>Every dispatch shape, one per line.</summary>
    public static string MediatorControllerFile => Path.Combine(MediatorProjectDir, "OrderController.cs");

    /// <summary>Things that look like a dispatch and are not.</summary>
    public static string MediatorDecoysFile => Path.Combine(MediatorProjectDir, "Decoys.cs");

    public static string MediatRStubsFile => Path.Combine(MediatorProjectDir, "MediatRStubs.cs");
    public static string ZaptoStubsFile => Path.Combine(MediatorProjectDir, "ZaptoStubs.cs");

    /// <summary>
    /// The multi-project protobuf fixture, laid out the way a real gRPC solution is: the
    /// <c>.proto</c> and its generated C# in Contracts, the implementation in Server, the callers in
    /// Client, and one project that references none of it. ProtoProject cannot stand in for this —
    /// it holds all four roles in one assembly, so a search that never left the owning project would
    /// still find every answer there and look like it worked.
    /// </summary>
    public static string ProtoSolutionDir => Path.Combine(s_fixturesRoot, "ProtoSolution");
    public static string ProtoSolutionFile => Path.Combine(ProtoSolutionDir, "ProtoSolution.sln");

    public static string ProtoContractsProjectDir => Path.Combine(ProtoSolutionDir, "Contracts");
    public static string ProtoContractsProjectFile => Path.Combine(ProtoContractsProjectDir, "Contracts.csproj");
    public static string ProtoSolutionWidgetsProtoFile => Path.Combine(ProtoContractsProjectDir, "widgets", "widgets.proto");
    public static string ProtoSolutionWidgetsGeneratedFile => Path.Combine(ProtoContractsProjectDir, "Generated", "widgets", "Widgets.cs");
    public static string ProtoSolutionWidgetsGrpcGeneratedFile => Path.Combine(ProtoContractsProjectDir, "Generated", "widgets", "WidgetsGrpc.cs");

    /// <summary>The implementation, in a project the <c>.proto</c>'s own project knows nothing
    /// about: it is reached only by walking ProjectReferences backwards.</summary>
    public static string ProtoServerProjectDir => Path.Combine(ProtoSolutionDir, "Server");
    public static string ProtoServerProjectFile => Path.Combine(ProtoServerProjectDir, "Server.csproj");
    public static string ProtoServerServiceFile => Path.Combine(ProtoServerProjectDir, "WidgetGrpcService.cs");

    /// <summary>The call sites, in a third project that references Contracts but not Server.</summary>
    public static string ProtoClientProjectDir => Path.Combine(ProtoSolutionDir, "Client");
    public static string ProtoClientProjectFile => Path.Combine(ProtoClientProjectDir, "Client.csproj");
    public static string ProtoClientCallerFile => Path.Combine(ProtoClientProjectDir, "WidgetCaller.cs");

    /// <summary>The control: in the solution, spelling the same names, referencing nothing.</summary>
    public static string ProtoUnrelatedProjectDir => Path.Combine(ProtoSolutionDir, "Unrelated");
    public static string ProtoUnrelatedProjectFile => Path.Combine(ProtoUnrelatedProjectDir, "Unrelated.csproj");
    public static string ProtoUnrelatedLookupFile => Path.Combine(ProtoUnrelatedProjectDir, "WidgetLookup.cs");

    /// <summary>
    /// The multi-project mediator fixture, laid out the way a modular solution is: the message in
    /// Contracts, one handler each in the sibling modules Inventory and Billing, and the dispatch
    /// in Api — which references only Contracts, so neither handler is in the dispatch project's
    /// dependency closure. MediatorProject cannot stand in for this: it holds every role in one
    /// assembly, so a search that never left the caret's own project still finds every answer there.
    /// </summary>
    public static string MediatorModulesDir => Path.Combine(s_fixturesRoot, "MediatorModules");
    public static string MediatorModulesSolutionFile => Path.Combine(MediatorModulesDir, "MediatorModules.sln");
    public static string MediatorModulesEndpointFile => Path.Combine(MediatorModulesDir, "Api", "CustomerEndpoint.cs");
    public static string MediatorModulesInventoryHandlerFile => Path.Combine(MediatorModulesDir, "Inventory", "InventorySyncHandler.cs");
    public static string MediatorModulesBillingHandlerFile => Path.Combine(MediatorModulesDir, "Billing", "BillingSyncHandler.cs");

    /// <summary>
    /// Two plain C# projects with nothing mediator- or proto-shaped about them: an extension
    /// method declared in Warehouse and called only from Storefront, the project that references
    /// it. The one direction lazy loading does not follow — a search started from the declaration
    /// must widen the solution with the projects that consume it, or answer "0 references".
    /// </summary>
    public static string LayeredAppDir => Path.Combine(s_fixturesRoot, "LayeredApp");
    public static string LayeredAppSolutionFile => Path.Combine(LayeredAppDir, "LayeredApp.sln");
    public static string LayeredAppWarehouseModuleFile => Path.Combine(LayeredAppDir, "Warehouse", "WarehouseModule.cs");
    public static string LayeredAppStartupFile => Path.Combine(LayeredAppDir, "Storefront", "Startup.cs");

    /// <summary>
    /// Walks up from the test assembly location to find the Fixtures directory.
    /// Prefer the source-tree fixtures so Roslyn can open the nested sample project
    /// with its real restore/build artifacts; fall back to copied output fixtures.
    /// </summary>
    private static string FindFixturesRoot()
    {
        string? copiedFixturesRoot = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solutionCandidate = Path.Combine(dir.FullName, "RoslynMCP.sln");
            var sourceCandidate = Path.Combine(dir.FullName, "RoslynMCP.Tests", "Fixtures");
            if (File.Exists(solutionCandidate) && Directory.Exists(sourceCandidate))
                return sourceCandidate;

            var copiedCandidate = Path.Combine(dir.FullName, "Fixtures");
            if (copiedFixturesRoot is null && Directory.Exists(copiedCandidate))
                copiedFixturesRoot = copiedCandidate;

            dir = dir.Parent;
        }

        if (copiedFixturesRoot is not null)
            return copiedFixturesRoot;

        throw new InvalidOperationException(
            "Could not locate the Fixtures directory. Ensure the test project copies fixture files to the output directory.");
    }
}
