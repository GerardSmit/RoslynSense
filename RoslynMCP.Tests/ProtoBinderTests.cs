using System.Runtime.ExceptionServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMCP.Languages.Proto.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The bindings between a <c>.proto</c> declaration and the C# protoc generated for it, against a
/// real loaded workspace.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the pack — go-to-definition, find-usages, find-implementations, hover, the
/// MCP tools — is plain <c>SymbolFinder</c> once a declaration has become an <see cref="ISymbol"/>.
/// This is the file that proves the declaration becomes the right one, so a failure here is the
/// only kind that makes every other proto feature quietly point somewhere plausible and wrong.
/// </para>
/// <para>
/// The assertions read protoc's own anchors back out of the generated C# — the descriptor index
/// expression, the <c>…FieldNumber</c> constant, the <c>OriginalName</c> attribute — rather than
/// restating the names the binder happened to produce. Asserting the name alone would pass just as
/// well for a binder that guessed it from protoc's naming rules, which is exactly the design this
/// pack rejected.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoBinderTests
{
    /// <summary>Namespace-qualified for a type, and containing-type-qualified for a member, so an
    /// assertion failure names the symbol the binder actually chose.</summary>
    private static readonly SymbolDisplayFormat s_qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    // ---- Services and rpcs ----------------------------------------------------------------------

    [Fact]
    public async Task AServiceBindsToItsHolderClassItsBaseAndItsClient()
    {
        var bound = await BindAsync();
        var service = Declaration<ProtoService>(bound.Widgets, "widgets.WidgetService");

        Assert.Equal("ProtoFixture.Widgets.WidgetService", Name(bound.Index.ServiceTypeFor(service)));
        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceBase",
            Name(bound.Index.ServiceBaseFor(service)));
        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceClient",
            Name(bound.Index.ServiceClientFor(service)));

        // The base is what find-implementations searches derived classes of, so the hand-written
        // server has to derive from the very type bound here rather than from one that merely reads
        // the same way.
        var implementation = NamedType(bound.Compilation, "ProtoFixture.WidgetGrpcService");
        Assert.Equal(Name(implementation.BaseType), Name(bound.Index.ServiceBaseFor(service)));

        // and the client is the type a caller holds, which is what makes its call sites findable.
        var caller = NamedType(bound.Compilation, "ProtoFixture.WidgetClientCaller");
        var field = Assert.Single(caller.GetMembers("_client").OfType<IFieldSymbol>());
        Assert.Equal(Name(field.Type), Name(bound.Index.ServiceClientFor(service)));
    }

    [Fact]
    public async Task AUnaryRpcBindsToTheBaseMethodAndEveryClientOverload()
    {
        var bound = await BindAsync();
        var rpc = Declaration<ProtoRpc>(bound.Widgets, "widgets.WidgetService.GetWidgetsById");

        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceBase.GetWidgetsById",
            Name(bound.Index.BaseMethodFor(rpc)));
        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceClient.GetWidgetsById",
            Name(bound.Index.ClientMethodFor(rpc)));
        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceClient.GetWidgetsByIdAsync",
            Name(bound.Index.ClientAsyncMethodFor(rpc)));

        // Five members for one rpc. The plugin emits a CallOptions overload beside the
        // headers/deadline/cancellationToken one for both the blocking and the async call, and a
        // call site picks whichever its argument list fits — so a find-usages that searched only
        // the first-declared overload of each would silently miss half the callers.
        var all = bound.Index.MethodsFor(rpc);
        Assert.Equal(5, all.Length);
        Assert.Single(all, method => method.ContainingType.Name == "WidgetServiceBase");
        Assert.Equal(4, all.Count(method => method.ContainingType.Name == "WidgetServiceClient"));

        Assert.Contains(all, method =>
            method.Name == "GetWidgetsById" && method.Parameters.Length == 2
            && method.Parameters[1].Type.Name == "CallOptions");
        Assert.Contains(all, method =>
            method.Name == "GetWidgetsById" && method.Parameters.Length == 4);
    }

    [Fact]
    public async Task TheServerStreamingRpcBindsEvenThoughItsGeneratedShapeIsDifferent()
    {
        var bound = await BindAsync();
        var rpc = Declaration<ProtoRpc>(bound.Widgets, "widgets.WidgetService.WatchWidgets");

        Assert.True(rpc.ServerStreaming);

        // The streaming base method takes a response writer and returns a bare Task, so anything
        // recognising an rpc by the Task<TReply> shape the unary ones have would drop this one —
        // and with it every navigation from the only streaming contract in the file.
        var baseMethod = bound.Index.BaseMethodFor(rpc);
        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceBase.WatchWidgets", Name(baseMethod));
        Assert.Equal(3, baseMethod!.Parameters.Length);
        Assert.Equal("IServerStreamWriter", baseMethod.Parameters[1].Type.Name);

        Assert.Equal(
            "ProtoFixture.Widgets.WidgetService.WidgetServiceClient.WatchWidgets",
            Name(bound.Index.ClientMethodFor(rpc)));

        // No …Async twin. A streaming call has no blocking form to be told apart from, so the
        // plugin never emits one; reporting a method here would name something that does not exist.
        Assert.Null(bound.Index.ClientAsyncMethodFor(rpc));

        Assert.Equal(3, bound.Index.MethodsFor(rpc).Length);
    }

    // ---- Messages and nested types ---------------------------------------------------------------

    [Fact]
    public async Task EachTopLevelMessageBindsToTheClassHoldingItsOwnDescriptorSlot()
    {
        var bound = await BindAsync();

        string[] expected =
        [
            "ProtoFixture.Widgets.Widget",
            "ProtoFixture.Widgets.GroupMember",
            "ProtoFixture.Widgets.GroupMemberList",
            "ProtoFixture.Widgets.Note",
        ];

        Assert.Equal(
            expected,
            bound.WidgetTypes.Messages.Select(message => Name(bound.Index.TypeFor(message))).ToArray());

        // The names above would fall out of any binder that matched on them. What makes the binding
        // right is that each class states which descriptor slot it was generated for, and the class
        // chosen for the Nth message states N.
        for (int i = 0; i < bound.WidgetTypes.Messages.Length; i++)
        {
            var type = bound.Index.TypeFor(bound.WidgetTypes.Messages[i]);
            Assert.NotNull(type);
            Assert.Contains($"MessageTypes[{i}]", DescriptorAnchor(type!));
        }
    }

    [Fact]
    public async Task ANestedMessageBindsPastTheMapEntryThatTookADescriptorSlot()
    {
        var bound = await BindAsync();
        var placement = Declaration<ProtoMessage>(bound.WidgetTypes, "widgets.Widget.Placement");
        var type = bound.Index.TypeFor(placement);

        Assert.Equal("ProtoFixture.Widgets.Widget.Types.Placement", Name(type));

        // Placement is the only message nested in Widget and the first one written, yet its
        // descriptor slot is 1: `map<string, string> attributes` took slot 0 with an entry type
        // that generates no C# class at all. A binder reading the descriptor index as a position
        // among the generated classes lands on nothing here.
        Assert.Contains("NestedTypes[1]", DescriptorAnchor(type!));

        // and the nesting does not stop the fields inside it from binding.
        var row = bound.Index.PropertyFor(
            Declaration<ProtoField>(bound.WidgetTypes, "widgets.Widget.Placement.row"));
        Assert.Equal("ProtoFixture.Widgets.Widget.Types.Placement.Row", Name(row));
        Assert.Equal(1, FieldNumber(row!));
    }

    [Fact]
    public async Task ANestedEnumBindsInsideProtocsTypesContainer()
    {
        var bound = await BindAsync();
        var visibility = Declaration<ProtoEnum>(bound.WidgetTypes, "widgets.Widget.Visibility");
        var type = bound.Index.TypeFor(visibility);

        Assert.Equal("ProtoFixture.Widgets.Widget.Types.Visibility", Name(type));

        // protoc puts everything a message declares into a static `Types` class, because a nested
        // type and a field of the same name cannot both live in one C# class. Matching a nested
        // enum against the members of the bound message finds none of them.
        Assert.Equal("Types", type!.ContainingType.Name);

        string[] originals = ["VISIBILITY_UNSPECIFIED", "VISIBILITY_PRIVATE", "VISIBILITY_PUBLIC"];

        Assert.Equal(
            originals,
            visibility.Values
                .Select(value => OriginalName(bound.Index.MemberFor(value)) ?? "<unbound>")
                .ToArray());

        // ImageOneofCase is nested in the same message, is shaped identically and stands for no
        // proto declaration whatsoever — the OriginalName attributes are the only thing telling the
        // two kinds of generated enum apart, which is why a caret on it must lead back to nothing.
        var oneofCase = NamedType(bound.Compilation, "ProtoFixture.Widgets.Widget+ImageOneofCase");
        Assert.Null(bound.Index.DeclarationFor(oneofCase));
        Assert.Null(bound.Index.DeclarationFor(oneofCase, includeInherited: true));
    }

    // ---- Fields and enum values -------------------------------------------------------------------

    [Fact]
    public async Task AFieldBindsToThePropertyWhoseFieldNumberConstantCarriesItsNumber()
    {
        var bound = await BindAsync();
        var imageUrl = Declaration<ProtoField>(bound.WidgetTypes, "widgets.Widget.image_url");

        Assert.Equal(6, imageUrl.Number);

        var property = bound.Index.PropertyFor(imageUrl);
        Assert.Equal("ProtoFixture.Widgets.Widget.ImageUrl", Name(property));
        Assert.Equal(6, FieldNumber(property!));

        // A oneof member is an ordinary field of the generated class, so sitting inside
        // `oneof image` must not keep it out of the map — the oneof numbering runs on through the
        // message's own fields and the properties are emitted inline with them.
        Assert.NotNull(imageUrl.Oneof);
        Assert.Equal("image", imageUrl.Oneof!.Name.Value);

        // and a map field generates a property like any other, even though its entry type takes a
        // descriptor slot that generates no class.
        var attributes = Declaration<ProtoField>(bound.WidgetTypes, "widgets.Widget.attributes");
        Assert.True(attributes.IsMap);
        Assert.Equal(
            "ProtoFixture.Widgets.Widget.Attributes", Name(bound.Index.PropertyFor(attributes)));
        Assert.Equal(8, FieldNumber(bound.Index.PropertyFor(attributes)!));
    }

    [Fact]
    public async Task AFieldWhoseGeneratedPropertyDoesNotShareItsNameStillBindsByNumber()
    {
        var bound = await BindAsync();
        var note = Declaration<ProtoField>(bound.WidgetTypes, "widgets.Note.note");

        // protoc cannot call the property `Note` — that is the class's own name — so it emits
        // `Note_`. Nothing about the two spellings matches, which is both the case a name-matching
        // binder gets wrong and the stand-in for a field renamed in the .proto since the last
        // build: the wire number is a field's identity and its name is not.
        var property = bound.Index.PropertyFor(note);
        Assert.Equal("Note_", property?.Name);
        Assert.Equal(note.Number, FieldNumber(property!));

        // The rest of the same message keeps matching names, so this is not a message bound by
        // position with the mismatch going unnoticed.
        Assert.Equal(
            "WrittenAt",
            bound.Index.PropertyFor(
                Declaration<ProtoField>(bound.WidgetTypes, "widgets.Note.written_at"))?.Name);
        Assert.Equal(
            "Channel",
            bound.Index.PropertyFor(
                Declaration<ProtoField>(bound.WidgetTypes, "widgets.Note.channel"))?.Name);
    }

    [Fact]
    public async Task AnEnumValueBindsToTheMemberCarryingItsOriginalName()
    {
        var bound = await BindAsync();

        var alpha = Declaration<ProtoEnumValue>(bound.Common, "common.CHANNEL_ALPHA");
        var member = bound.Index.MemberFor(alpha);

        // protoc strips the enum's own name off the front of each value, so the proto spelling
        // survives nowhere in the C# except the attribute — which is what makes the attribute the
        // binding rather than a re-derivation of the stripping rule.
        Assert.Equal("ProtoFixture.Common.Channel.Alpha", Name(member));
        Assert.Equal("CHANNEL_ALPHA", OriginalName(member));
        Assert.Equal(1, Assert.IsType<int>(member!.ConstantValue));

        // CHANNEL_GAMMA is 4, not 3. Its member has to carry that number, which a binder pairing
        // values with members by position would get wrong on exactly this enum.
        var gamma = Declaration<ProtoEnumValue>(bound.Common, "common.CHANNEL_GAMMA");
        var gammaMember = bound.Index.MemberFor(gamma);
        Assert.Equal("Gamma", gammaMember?.Name);
        Assert.Equal(4, Assert.IsType<int>(gammaMember!.ConstantValue));
    }

    // ---- The reverse map --------------------------------------------------------------------------

    [Fact]
    public async Task EveryBoundSymbolLeadsBackToTheDeclarationItWasGeneratedFrom()
    {
        var bound = await BindAsync();

        var service = NamedType(bound.Compilation, "ProtoFixture.Widgets.WidgetService");
        var serviceBase = NamedType(bound.Compilation, "ProtoFixture.Widgets.WidgetService+WidgetServiceBase");
        var client = NamedType(bound.Compilation, "ProtoFixture.Widgets.WidgetService+WidgetServiceClient");

        // All three generated halves of a service answer the same declaration: a caret on any of
        // them is a caret on `service WidgetService`.
        INamedTypeSymbol[] halves = [service, serviceBase, client];

        foreach (var half in halves)
        {
            AssertLeadsBack(
                bound.Index, half,
                FixturePaths.WidgetsProtoFile, "widgets.WidgetService", ProtoDeclarationKind.Service);
        }

        AssertLeadsBack(
            bound.Index, Assert.Single(serviceBase.GetMembers("WatchWidgets").OfType<IMethodSymbol>()),
            FixturePaths.WidgetsProtoFile,
            "widgets.WidgetService.WatchWidgets", ProtoDeclarationKind.Rpc);

        AssertLeadsBack(
            bound.Index, NamedType(bound.Compilation, "ProtoFixture.Widgets.Widget"),
            FixturePaths.WidgetTypesProtoFile, "widgets.Widget", ProtoDeclarationKind.Message);

        AssertLeadsBack(
            bound.Index, NamedType(bound.Compilation, "ProtoFixture.Widgets.Widget+Types+Placement"),
            FixturePaths.WidgetTypesProtoFile, "widgets.Widget.Placement", ProtoDeclarationKind.Message);

        AssertLeadsBack(
            bound.Index, NamedType(bound.Compilation, "ProtoFixture.Widgets.Widget+Types+Visibility"),
            FixturePaths.WidgetTypesProtoFile, "widgets.Widget.Visibility", ProtoDeclarationKind.Enum);

        var imageUrl = Assert.Single(
            NamedType(bound.Compilation, "ProtoFixture.Widgets.Widget").GetMembers("ImageUrl").OfType<IPropertySymbol>());
        AssertLeadsBack(
            bound.Index, imageUrl,
            FixturePaths.WidgetTypesProtoFile, "widgets.Widget.image_url", ProtoDeclarationKind.Field);

        var alpha = Assert.Single(
            NamedType(bound.Compilation, "ProtoFixture.Common.Channel").GetMembers("Alpha").OfType<IFieldSymbol>());
        AssertLeadsBack(
            bound.Index, alpha,
            FixturePaths.CommonTypesProtoFile, "common.CHANNEL_ALPHA", ProtoDeclarationKind.EnumValue);
    }

    [Fact]
    public async Task AHandWrittenImplementationLeadsBackToTheContractItImplements()
    {
        var bound = await BindAsync();
        var implementation = NamedType(bound.Compilation, "ProtoFixture.WidgetGrpcService");

        // The implementation is not generated code, so the exact map cannot see it. It is still the
        // service as far as a user with the caret on it is concerned, which is the whole reason the
        // inherited walk exists.
        Assert.Null(bound.Index.DeclarationFor(implementation));
        Assert.Equal(
            "widgets.WidgetService",
            bound.Index.DeclarationFor(implementation, includeInherited: true)?.FullName);

        var watch = Assert.Single(implementation.GetMembers("WatchWidgets").OfType<IMethodSymbol>());
        Assert.Null(bound.Index.DeclarationFor(watch));

        var inherited = bound.Index.DeclarationFor(watch, includeInherited: true);
        Assert.Equal("widgets.WidgetService.WatchWidgets", inherited?.FullName);
        Assert.Equal(ProtoDeclarationKind.Rpc, inherited?.Kind);
    }

    [Fact]
    public async Task AGeneratedMemberThatStandsForNoDeclarationLeadsBackToNothing()
    {
        var bound = await BindAsync();
        var client = NamedType(bound.Compilation, "ProtoFixture.Widgets.WidgetService+WidgetServiceClient");

        // NewInstance is generated, is an override, and sits inside a type that is bound to the
        // service. Answering "the service" for it — which is what walking out to the containing
        // type would do — would put a caret on plumbing onto the contract.
        var newInstance = Assert.Single(client.GetMembers("NewInstance").OfType<IMethodSymbol>());
        Assert.Null(bound.Index.DeclarationFor(newInstance));
        Assert.Null(bound.Index.DeclarationFor(newInstance, includeInherited: true));

        // and hand-written code that merely calls the contract is not part of it.
        var caller = NamedType(bound.Compilation, "ProtoFixture.WidgetClientCaller");
        Assert.Null(bound.Index.DeclarationFor(caller, includeInherited: true));
        Assert.Null(bound.Index.DeclarationFor(
            Assert.Single(caller.GetMembers("GetOriginAttributeAsync").OfType<IMethodSymbol>()),
            includeInherited: true));
    }

    // ---- The never-built path ---------------------------------------------------------------------

    [Fact]
    public void AnIndexForAProjectThatWasNeverBuiltReportsItselfEmptyAndBindsNothing()
    {
        var parse = Parse(FixturePaths.OrphanProtoFile);

        Assert.True(ProtoGeneratedIndex.Empty.IsEmpty);
        Assert.Empty(ProtoGeneratedIndex.Empty.ProtoFiles);
        Assert.Empty(ProtoGeneratedIndex.Empty.CompiledProtoFiles);
        Assert.Empty(ProtoGeneratedIndex.Empty.DocumentsFor(FixturePaths.OrphanProtoFile));

        // Null for every kind of declaration rather than a throw for the kinds nothing was recorded
        // for: an unbuilt project is the ordinary state of a checkout, and every caller reads these
        // as "no C# yet" rather than guarding each one.
        foreach (var symbol in BindingsOf(parse, ProtoGeneratedIndex.Empty))
            Assert.Null(symbol);
    }

    [Fact]
    public async Task AProtoWithNoGeneratedCounterpartBindsNothingInAProjectThatWasBuilt()
    {
        var view = await ViewAsync(FixturePaths.OrphanProtoFile);

        // Nothing generated names orphan.proto in its header, so the project that was built from
        // the other three protos has no documents for this one.
        Assert.Empty(view.Index.DocumentsFor(FixturePaths.OrphanProtoFile));

        foreach (var symbol in BindingsOf(view.Parse, view.Index))
            Assert.Null(symbol);

        // The generated bindings that do exist belong to the other files and must not leak into
        // this one by name: `PingReply` and `Widget` are both messages in the same project.
        Assert.Null(view.Index.TypeFor(Declaration<ProtoMessage>(view.Parse, "orphan.PingReply")));
    }

    [Fact]
    public async Task AskingAboutAProtoWithNoGeneratedCounterpartThrowsNothingAtAll()
    {
        var view = await ViewAsync(FixturePaths.OrphanProtoFile);

        // Warmed first, so what is measured below is the steady-state cost rather than whatever a
        // lazy singleton does on its way up.
        Assert.Equal(0, Probe(view));

        int thread = Environment.CurrentManagedThreadId;
        var thrown = new List<string>();

        void Record(object? sender, FirstChanceExceptionEventArgs e)
        {
            // This thread only: the suite runs other collections in parallel and their exceptions
            // are none of this test's business.
            if (Environment.CurrentManagedThreadId == thread)
                thrown.Add(e.Exception.GetType().FullName ?? "<unknown>");
        }

        AppDomain.CurrentDomain.FirstChanceException += Record;
        try
        {
            // Repeated, because one throw per missing binding is invisible in a single pass and is
            // what an editor would pay on every keystroke in a .proto that has never been built.
            for (int i = 0; i < 50; i++)
                _ = Probe(view);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Record;
        }

        Assert.True(
            thrown.Count == 0,
            $"a lookup that finds nothing threw: {string.Join(", ", thrown.Distinct())}");
    }

    [Fact]
    public async Task ATypeReferenceInANeverGeneratedFileStillResolvesInsideItsOwnFile()
    {
        var view = await ViewAsync(FixturePaths.OrphanProtoFile);

        var hit = ProtoSymbolResolver.ResolveAt(
            view, OffsetOf(FixturePaths.OrphanProtoFile, "Verdict verdict"));

        Assert.NotNull(hit);
        Assert.Equal(ProtoHitKind.FieldType, hit!.Kind);

        // Only the C# half is missing. What a name refers to is a question about the .proto and its
        // imports alone, and answering it is what keeps navigation, hover and the outline working
        // in a project nobody has built yet — which is when a user is most likely to be reading it.
        Assert.Null(hit.Symbol);

        var target = Assert.IsType<ProtoEnum>(hit.ResolvedProtoTarget);
        Assert.Equal("orphan.Verdict", target.FullName);
        Assert.Equal(view.FilePath, hit.TargetFile?.FilePath);

        var request = ProtoSymbolResolver.ResolveAt(
            view, OffsetOf(FixturePaths.OrphanProtoFile, "(PingRequest)", 1));

        Assert.Equal(ProtoHitKind.RpcRequestType, request?.Kind);
        Assert.Equal("orphan.PingRequest", request?.ResolvedProtoTarget?.FullName);
        Assert.Null(request?.Symbol);
    }

    [Fact]
    public async Task AnImportFromANeverGeneratedFileStillResolvesToAPath()
    {
        var view = await ViewAsync(FixturePaths.OrphanProtoFile);

        // An import path is relative to the proto root — the project directory — and not to the
        // file that writes it, so "common/types.proto" written in NoGenerated\ means the folder two
        // levels away rather than one beside it. That arithmetic reads nothing generated, so it has
        // to keep answering for a file protoc has never seen. orphan.proto writes no import of its
        // own, so the statement it would write is resolved directly here.
        string? resolved = ProtoImportResolver.Resolve(
            "common/types.proto", view.FilePath, view.ProjectDirectory);

        Assert.NotNull(resolved);
        Assert.True(
            ProtoDocumentService.PathsEqual(FixturePaths.CommonTypesProtoFile, resolved!),
            $"'common/types.proto' resolved to {resolved}");
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>The bindings for the whole fixture project, plus the parses every test names
    /// declarations out of.</summary>
    private sealed record BoundFixture(
        ProtoGeneratedIndex Index,
        Compilation Compilation,
        ProtoFile Widgets,
        ProtoFile WidgetTypes,
        ProtoFile Common);

    private static async Task<BoundFixture> BindAsync()
    {
        var view = await ViewAsync(FixturePaths.WidgetsProtoFile);

        Assert.NotNull(view.Project);
        var compilation = await view.Project!.GetCompilationAsync(default);
        Assert.NotNull(compilation);

        // The fixture commits protoc's output as ordinary source precisely so this is never a
        // question of whether a build ran. An empty index here means the project did not load —
        // usually an unrestored fixture — and every assertion below would fail as a binder bug.
        Assert.False(
            view.Index.IsEmpty,
            "the ProtoProject fixture produced no generated documents; the project failed to load");

        return new BoundFixture(
            view.Index,
            compilation!,
            view.Parse,
            Parse(FixturePaths.WidgetTypesProtoFile),
            Parse(FixturePaths.CommonTypesProtoFile));
    }

    private static async Task<ProtoProjectView> ViewAsync(string protoPath)
    {
        var view = await ProtoWorkspace.GetAsync(protoPath, default);
        Assert.NotNull(view);
        return view!;
    }

    private static ProtoFile Parse(string protoPath)
    {
        var parse = ProtoDocumentService.GetParse(protoPath);
        Assert.NotNull(parse);
        return parse!;
    }

    private static T Declaration<T>(ProtoFile file, string fullName)
        where T : ProtoDeclaration
    {
        var declaration = file.FindByFullName(fullName);
        Assert.True(declaration is not null, $"{fullName} is not declared in {file.FilePath}");
        return Assert.IsType<T>(declaration);
    }

    private static INamedTypeSymbol NamedType(Compilation compilation, string metadataName)
    {
        var type = compilation.GetTypeByMetadataName(metadataName);
        Assert.True(type is not null, $"{metadataName} is not in the ProtoProject compilation");
        return type!;
    }

    private static string Name(ISymbol? symbol) => symbol?.ToDisplayString(s_qualified) ?? "<unbound>";

    /// <summary>
    /// The <c>Descriptor</c> property protoc wrote into a generated class, which states the slot in
    /// its own descriptor the class was generated for. This is the anchor the binder reads, so
    /// checking it here is what makes an assertion about a binding more than a restatement of it.
    /// </summary>
    private static string DescriptorAnchor(INamedTypeSymbol type) =>
        string.Concat(type.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .SelectMany(declaration => declaration.Members.OfType<PropertyDeclarationSyntax>())
            .Where(property => property.Identifier.ValueText == "Descriptor")
            .Select(property => property.ToString()));

    /// <summary>The wire number the constant beside a generated property carries.</summary>
    private static int? FieldNumber(IPropertySymbol property) =>
        property.ContainingType.GetMembers(property.Name + "FieldNumber")
            .OfType<IFieldSymbol>()
            .Select(field => field.ConstantValue as int?)
            .FirstOrDefault();

    /// <summary>The proto spelling protoc recorded on a generated enum member.</summary>
    private static string? OriginalName(IFieldSymbol? member) =>
        member?.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.Name == "OriginalNameAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static void AssertLeadsBack(
        ProtoGeneratedIndex index,
        ISymbol symbol,
        string protoFile,
        string fullName,
        ProtoDeclarationKind kind)
    {
        var reference = index.DeclarationFor(symbol);

        Assert.True(reference.HasValue, $"{Name(symbol)} leads back to nothing");
        Assert.Equal(fullName, reference!.Value.FullName);
        Assert.Equal(kind, reference.Value.Kind);
        Assert.True(
            ProtoDocumentService.PathsEqual(protoFile, reference.Value.FilePath),
            $"{Name(symbol)} leads back to {reference.Value.FilePath}");
    }

    /// <summary>Every C# symbol one file's declarations bind to, in declaration order.</summary>
    private static IEnumerable<ISymbol?> BindingsOf(ProtoFile file, ProtoGeneratedIndex index)
    {
        foreach (var declaration in file.AllDeclarations)
        {
            switch (declaration)
            {
                case ProtoMessage message:
                    yield return index.TypeFor(message);
                    break;

                case ProtoEnum @enum:
                    yield return index.TypeFor(@enum);
                    break;

                case ProtoEnumValue value:
                    yield return index.MemberFor(value);
                    break;

                case ProtoField field:
                    yield return index.PropertyFor(field);
                    break;

                case ProtoService service:
                    yield return index.ServiceTypeFor(service);
                    yield return index.ServiceBaseFor(service);
                    yield return index.ServiceClientFor(service);
                    break;

                case ProtoRpc rpc:
                    yield return index.BaseMethodFor(rpc);
                    yield return index.ClientMethodFor(rpc);
                    yield return index.ClientAsyncMethodFor(rpc);

                    foreach (var method in index.MethodsFor(rpc))
                        yield return method;

                    break;
            }
        }
    }

    /// <summary>Every question a caret in the file can ask, run once over the whole file. Returns
    /// how many of them found something, which for an unbuilt file is always none.</summary>
    private static int Probe(ProtoProjectView view)
    {
        int found = BindingsOf(view.Parse, view.Index).Count(symbol => symbol is not null);

        foreach (var declaration in view.Parse.AllDeclarations)
        {
            if (ProtoSymbolResolver.ResolveAt(view, declaration.Name.Span.Start)?.Symbol is not null)
                found++;
        }

        return found;
    }

    /// <summary>The offset of <paramref name="needle"/> in the file.</summary>
    private static int OffsetOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);

        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");
        return index + offsetIntoNeedle;
    }
}
