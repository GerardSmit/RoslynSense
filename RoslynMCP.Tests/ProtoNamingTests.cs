using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// protoc's C# naming rules as the pack reimplements them, checked against the C# protoc actually
/// wrote for the same protos.
/// </summary>
/// <remarks>
/// The pack deliberately binds through anchors in the generated output rather than through these
/// predictions, because a prediction that has drifted fails invisibly: the wrong name usually
/// still resolves to <em>something</em>, so a hover or a jump lands on a plausible neighbour
/// instead of on nothing. That makes drift the only failure mode worth testing for, so every
/// expected string below is also looked up in the fixture's committed protoc output. A rule that
/// stops agreeing with protoc fails here rather than quietly downgrading navigation.
/// </remarks>
public class ProtoNamingTests
{
    private static readonly ConcurrentDictionary<string, SyntaxNode> s_generatedRoots = new(StringComparer.OrdinalIgnoreCase);

    private static ProtoFile Parse(string path) =>
        ProtoParser.Parse(path, SourceText.From(File.ReadAllText(path)));

    private static ProtoFile Parse(string path, string text) =>
        ProtoParser.Parse(path, SourceText.From(text));

    // ---- Namespaces and file-level names -------------------------------------------------------

    [Fact]
    public void TheNamespaceIsTheCsharpNamespaceOptionVerbatimAndThePackageWhenThereIsNone()
    {
        var widgets = Parse(FixturePaths.WidgetTypesProtoFile);
        Assert.Equal("ProtoFixture.Widgets", ProtoNaming.Namespace(widgets));
        Assert.Contains("ProtoFixture.Widgets", DeclaredNamespaces(FixturePaths.WidgetTypesGeneratedFile));

        var common = Parse(FixturePaths.CommonTypesProtoFile);
        Assert.Equal("ProtoFixture.Common", ProtoNaming.Namespace(common));
        Assert.Contains("ProtoFixture.Common", DeclaredNamespaces(FixturePaths.CommonTypesGeneratedFile));

        // Without the option protoc derives the namespace from the package, and that derivation is
        // the one place the camel-case conversion keeps periods: `my_app.v2` has to stay two
        // namespace segments rather than collapsing into `MyAppV2`.
        Assert.Equal("Widgets", ProtoNaming.Namespace(Parse("widgets/plain.proto", """
            syntax = "proto3";

            package widgets;
            """)));

        Assert.Equal("MyApp.V2", ProtoNaming.Namespace(Parse("app/app.proto", """
            syntax = "proto3";

            package my_app.v2;
            """)));

        // Neither option nor package generates into the global namespace, so the empty string is
        // a real answer here — qualifying with a leading dot would name nothing.
        Assert.Equal(string.Empty, ProtoNaming.Namespace(Parse("bare.proto", "syntax = \"proto3\";")));
    }

    [Fact]
    public void TheFileLevelClassAndFileNamesAreBuiltFromTheProtosOwnFileName()
    {
        var types = Parse(FixturePaths.WidgetTypesProtoFile);

        // Every generated message points back at the reflection class for its descriptor, which
        // makes this name the hinge the descriptor-expression binder turns on.
        Assert.Equal("TypesReflection", ProtoNaming.ReflectionClassName(types));
        Assert.Contains("ProtoFixture.Widgets.TypesReflection", DeclaredTypes(FixturePaths.WidgetTypesGeneratedFile));

        Assert.Equal("Types.cs", ProtoNaming.GeneratedFileName(types));
        Assert.Equal("Types.cs", Path.GetFileName(FixturePaths.WidgetTypesGeneratedFile));

        var widgets = Parse(FixturePaths.WidgetsProtoFile);
        Assert.Equal("WidgetsReflection", ProtoNaming.ReflectionClassName(widgets));
        Assert.Contains("ProtoFixture.Widgets.WidgetsReflection", DeclaredTypes(FixturePaths.WidgetsGeneratedFile));

        Assert.Equal("WidgetsGrpc.cs", ProtoNaming.GrpcFileName(widgets));
        Assert.Equal("WidgetsGrpc.cs", Path.GetFileName(FixturePaths.WidgetsGrpcGeneratedFile));

        // Two protos both called types.proto sit in different directories and generate two files
        // both called Types.cs, which is exactly why only the leaf name comes from protoc.
        Assert.Equal(
            ProtoNaming.GeneratedFileName(types),
            ProtoNaming.GeneratedFileName(Parse(FixturePaths.CommonTypesProtoFile)));
    }

    // ---- Messages and enums --------------------------------------------------------------------

    [Fact]
    public void ANestedMessageAndANestedEnumBothGoThroughProtocsTypesContainer()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        var placement = Assert.Single(widget.Messages);
        var visibility = Assert.Single(widget.Enums);

        // The container exists because a nested type and a field very often share a name, and C#
        // will not let a class and a property of the same name sit in one type.
        Assert.Equal("Widget.Types.Placement", ProtoNaming.NestedName(placement));
        Assert.Equal("Widget.Types.Visibility", ProtoNaming.NestedName(visibility));
        Assert.Equal("ProtoFixture.Widgets.Widget.Types.Placement", ProtoNaming.DisplayName(file, placement));
        Assert.Equal("ProtoFixture.Widgets.Widget.Types.Visibility", ProtoNaming.DisplayName(file, visibility));

        // A type name is never converted: protoc builds the class name straight from the proto
        // name, so a top-level message keeps its spelling and gains no container.
        Assert.Equal("ProtoFixture.Widgets.Widget", ProtoNaming.DisplayName(file, widget));

        var declared = DeclaredTypes(FixturePaths.WidgetTypesGeneratedFile);
        Assert.Contains("ProtoFixture.Widgets.Widget", declared);
        Assert.Contains($"ProtoFixture.Widgets.Widget.{ProtoNaming.NestedTypesContainerName}", declared);
        Assert.Contains("ProtoFixture.Widgets.Widget.Types.Placement", declared);
        Assert.Contains("ProtoFixture.Widgets.Widget.Types.Visibility", declared);

        // The same holds a file away, where the nested type is an enum with no nested message
        // beside it — the container is per nesting level, not per kind.
        var widgetsFile = Parse(FixturePaths.WidgetsProtoFile);
        var kind = Assert.Single(widgetsFile.Messages.Single(m => m.Name.Value == "WidgetEvent").Enums);
        Assert.Equal("ProtoFixture.Widgets.WidgetEvent.Types.Kind", ProtoNaming.DisplayName(widgetsFile, kind));
        Assert.Contains(
            "ProtoFixture.Widgets.WidgetEvent.Types.Kind",
            DeclaredTypes(FixturePaths.WidgetsGeneratedFile));
    }

    [Fact]
    public void AnEnumValueLosesItsEnumsNamePrefixAndIsPascalCasedFromShoutyCase()
    {
        var file = Parse(FixturePaths.CommonTypesProtoFile);
        var channel = Assert.Single(file.Enums);

        // Neither the case nor the separator matches literally: the comparison ignores both, which
        // is the only reason CHANNEL_ALPHA in `enum Channel` loses its prefix at all.
        Assert.Equal(
            new[] { "Unknown", "Alpha", "Beta", "Gamma" },
            channel.Values.Select(value => ProtoNaming.EnumMemberName(value)));

        // protoc keeps the proto spelling in an OriginalName attribute beside each member, so the
        // generated enum is the arbiter of the whole rule, member for member.
        Assert.Equal(
            new[] { "Unknown", "Alpha", "Beta", "Gamma" },
            DeclaredMembers(FixturePaths.CommonTypesGeneratedFile, "ProtoFixture.Common.Channel"));

        var visibility = Assert.Single(
            Parse(FixturePaths.WidgetTypesProtoFile).Messages
                .Single(message => message.Name.Value == "Widget").Enums);

        Assert.Equal(
            new[] { "Unspecified", "Private", "Public" },
            visibility.Values.Select(value => ProtoNaming.EnumMemberName(value)));
        Assert.Equal(
            new[] { "Unspecified", "Private", "Public" },
            DeclaredMembers(
                FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Widget.Types.Visibility"));
    }

    [Fact]
    public void AnEnumValueThatStrippingWouldRuinKeepsAUsableNameInstead()
    {
        // A value that is nothing but the prefix would be left with no member name at all, so the
        // prefix is not removed and the original is PascalCased whole.
        Assert.Equal("Channel", ProtoNaming.EnumMemberName("Channel", "CHANNEL"));
        Assert.Equal("Channel", ProtoNaming.EnumMemberName("Channel", "CHANNEL_"));

        // A value made of nothing but separators survives the prefix rule and then produces an
        // empty PascalCase result, which is not a name protoc could emit either.
        Assert.Equal("__", ProtoNaming.EnumMemberName("Level", "__"));

        // A remainder that starts with a digit is not a legal C# identifier and gains a leading
        // underscore rather than being emitted as one.
        Assert.Equal("_2", ProtoNaming.EnumMemberName("Syntax", "SYNTAX_2"));
        Assert.Equal("_2Fa", ProtoNaming.EnumMemberName("Auth", "AUTH_2FA"));

        // A value shorter than the prefix runs out before matching it and is left alone.
        Assert.Equal("Chan", ProtoNaming.EnumMemberName("Channel", "CHAN"));

        // Shouty casing lowercases a capital that follows another capital, which a field name must
        // never get: the two conversions are not interchangeable.
        Assert.Equal("AlphaBeta", ProtoNaming.ShoutyToPascalCase("ALPHA_BETA"));
        Assert.Equal("ALPHABETA", ProtoNaming.UnderscoresToPascalCase("ALPHA_BETA"));
    }

    // ---- Fields --------------------------------------------------------------------------------

    [Fact]
    public void AFieldsPropertyAndFieldNumberConstantAreTheNamesProtocDeclared()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        var imageUrl = widget.AllFields.Single(field => field.Name.Value == "image_url");

        Assert.Equal("ImageUrl", ProtoNaming.PropertyName(imageUrl));
        Assert.Equal("ImageUrlFieldNumber", ProtoNaming.FieldNumberConstName(imageUrl));
        Assert.Equal("HasImageUrl", ProtoNaming.HasPropertyName(imageUrl));
        Assert.Equal("ClearImageUrl", ProtoNaming.ClearMethodName(imageUrl));

        var members = DeclaredMembers(FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Widget");
        Assert.Contains("ImageUrl", members);
        Assert.Contains("ImageUrlFieldNumber", members);
        Assert.Contains("HasImageUrl", members);
        Assert.Contains("ClearImageUrl", members);

        // The constant's value is the wire number, and that pairing is the sharpest anchor a field
        // has: a rename can move the name and cannot move the number.
        Assert.Equal(6, imageUrl.Number);

        // A oneof member has explicit presence in every dialect, which is why a proto3 file has a
        // HasImageUrl at all. A map never has presence, and protoc generates neither member for it.
        Assert.True(ProtoNaming.HasExplicitPresence(imageUrl, file.SyntaxLevel));

        var attributes = widget.AllFields.Single(field => field.Name.Value == "attributes");
        Assert.False(ProtoNaming.HasExplicitPresence(attributes, file.SyntaxLevel));
        Assert.DoesNotContain("HasAttributes", members);
    }

    [Fact]
    public void AFieldWhoseNameWouldCollideGainsATrailingUnderscore()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var note = file.Messages.Single(message => message.Name.Value == "Note");
        var noteField = note.AllFields.Single(field => field.Name.Value == "note");

        // `message Note { string note = 1; }` would generate a property with its own class's name,
        // which C# refuses.
        Assert.Equal("Note_", ProtoNaming.PropertyName(noteField));
        Assert.Equal("Note_FieldNumber", ProtoNaming.FieldNumberConstName(noteField));

        var members = DeclaredMembers(FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Note");
        Assert.Contains("Note_", members);
        Assert.Contains("Note_FieldNumber", members);

        // Its neighbours are untouched: fields never collide with each other, so nothing else is
        // checked and nothing else gets a suffix.
        Assert.Equal("WrittenAt", ProtoNaming.PropertyName(note.AllFields.Single(f => f.Name.Value == "written_at")));
        Assert.Contains("WrittenAt", members);

        // The other half of the same rule is the fixed set of members every generated message
        // already declares. It is not a general C# keyword guard, which is why the list has to be
        // reproduced exactly rather than approximated.
        var reserved = Parse("app/reserved.proto", """
            syntax = "proto3";

            package app;

            message Holder {
              string parser = 1;
              string descriptor = 2;
              string to_string = 3;
              string identifier = 4;
            }
            """);

        var holder = Assert.Single(reserved.Messages);
        Assert.Equal(
            new[] { "Parser_", "Descriptor_", "ToString_", "Identifier" },
            holder.AllFields.Select(ProtoNaming.PropertyName));
    }

    [Fact]
    public void TheDescriptorsPropertyArrayIsExactlyTheMessagesFieldsInOrder()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);

        // protoc lists a message's property names in one array with the oneof members inline, so
        // this is a second and independent check on both the naming rules and the field order the
        // parser produced. The two disagreeing means one of them is wrong, and the pack binds
        // fields by index into this array.
        foreach (string name in new[] { "Widget", "GroupMember", "GroupMemberList", "Note" })
        {
            var message = file.Messages.Single(m => m.Name.Value == name);

            Assert.Equal(
                DescriptorPropertyNames(FixturePaths.WidgetTypesGeneratedFile, $"ProtoFixture.Widgets.{name}"),
                message.AllFields.Select(ProtoNaming.PropertyName));
        }

        // Including the nested message, whose own array lives inside its parent's entry.
        var placement = Assert.Single(file.Messages.Single(m => m.Name.Value == "Widget").Messages);
        Assert.Equal(
            DescriptorPropertyNames(
                FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Widget.Types.Placement"),
            placement.AllFields.Select(ProtoNaming.PropertyName));
    }

    // ---- Oneofs --------------------------------------------------------------------------------

    [Fact]
    public void AOneofNamesACaseEnumACasePropertyAndAClearMethod()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        var image = Assert.Single(widget.Oneofs);

        // A oneof generates no descriptor index, no constant and no attribute, so it is one of the
        // two things in the pack that has to be found by predicted name — which is what makes
        // these three strings load-bearing rather than cosmetic.
        Assert.Equal("ImageOneofCase", ProtoNaming.OneofCaseEnumName(image));
        Assert.Equal("ImageCase", ProtoNaming.OneofCasePropertyName(image));
        Assert.Equal("ClearImage", ProtoNaming.ClearMethodName(image));

        // The case enum nests in the message itself, not in its Types container: the container
        // holds only what the proto declared nested.
        var declared = DeclaredTypes(FixturePaths.WidgetTypesGeneratedFile);
        Assert.Contains("ProtoFixture.Widgets.Widget.ImageOneofCase", declared);
        Assert.DoesNotContain("ProtoFixture.Widgets.Widget.Types.ImageOneofCase", declared);

        var members = DeclaredMembers(FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Widget");
        Assert.Contains("ImageCase", members);
        Assert.Contains("ClearImage", members);

        // One member per field, named as the field's property is, after protoc's own zero.
        Assert.Equal(
            new[] { "ImageUrl", "ImageHash" },
            image.Fields.Select(ProtoNaming.OneofCaseName));
        Assert.Equal(
            new[] { ProtoNaming.OneofNoneCaseName, "ImageUrl", "ImageHash" },
            DeclaredMembers(FixturePaths.WidgetTypesGeneratedFile, "ProtoFixture.Widgets.Widget.ImageOneofCase"));

        var common = Parse(FixturePaths.CommonTypesProtoFile);
        var action = Assert.Single(
            common.Messages.Single(message => message.Name.Value == "OptionalStringUpdate").Oneofs);

        Assert.Equal("ActionOneofCase", ProtoNaming.OneofCaseEnumName(action));
        Assert.Equal("ActionCase", ProtoNaming.OneofCasePropertyName(action));
        Assert.Equal("ClearAction", ProtoNaming.ClearMethodName(action));
        Assert.Contains(
            "ProtoFixture.Common.OptionalStringUpdate.ActionOneofCase",
            DeclaredTypes(FixturePaths.CommonTypesGeneratedFile));
    }

    [Fact]
    public void AOneofMemberCalledNoneStepsAsideForTheEnumsOwnZero()
    {
        var file = Parse("app/choice.proto", """
            syntax = "proto3";

            package app;

            message Choice {
              oneof pick {
                string none = 1;
                string some = 2;
              }
            }
            """);

        var pick = Assert.Single(Assert.Single(file.Messages).Oneofs);

        // The zero member is not derived from any field, so a field that would produce it has to
        // move — otherwise the generated enum declares None twice and does not compile.
        Assert.Equal(new[] { "None_", "Some" }, pick.Fields.Select(ProtoNaming.OneofCaseName));

        // The property keeps the unsuffixed name: only the enum member collides.
        Assert.Equal("None", ProtoNaming.PropertyName(pick.Fields[0]));
    }

    // ---- Services ------------------------------------------------------------------------------

    [Fact]
    public void AServiceNamesTheGrpcClassesItsImplementationAndItsCallersUse()
    {
        var file = Parse(FixturePaths.WidgetsProtoFile);
        var service = Assert.Single(file.Services);

        Assert.Equal("WidgetService", ProtoNaming.ServiceClassName(service));
        Assert.Equal("WidgetServiceBase", ProtoNaming.ServiceBaseName(service));
        Assert.Equal("WidgetServiceClient", ProtoNaming.ServiceClientName(service));
        Assert.Equal("ProtoFixture.Widgets.WidgetService", ProtoNaming.ServiceDisplayName(file, service));

        // The wire name owes nothing to any C# naming rule, which is what makes it the one anchor
        // that still binds a proto service to its generated class if protoc's naming moves.
        Assert.Equal("widgets.WidgetService", ProtoNaming.GrpcServiceName(service));
        Assert.Contains(
            "\"widgets.WidgetService\"",
            File.ReadAllText(FixturePaths.WidgetsGrpcGeneratedFile));

        // Both the base class and the client hang off the outer service class rather than sitting
        // beside it, so navigating from the proto to "the implementation" has to go through it.
        var declared = DeclaredTypes(FixturePaths.WidgetsGrpcGeneratedFile);
        Assert.Contains("ProtoFixture.Widgets.WidgetService", declared);
        Assert.Contains("ProtoFixture.Widgets.WidgetService.WidgetServiceBase", declared);
        Assert.Contains("ProtoFixture.Widgets.WidgetService.WidgetServiceClient", declared);
    }

    [Fact]
    public void AnRpcNamesTheServerMethodAndOnlyAUnaryOneGetsAnAsyncClientOverload()
    {
        var file = Parse(FixturePaths.WidgetsProtoFile);
        var service = Assert.Single(file.Services);
        var unary = service.Rpcs.Single(rpc => rpc.Name.Value == "GetWidgetsById");
        var streaming = service.Rpcs.Single(rpc => rpc.Name.Value == "WatchWidgets");

        Assert.Equal("GetWidgetsById", ProtoNaming.MethodName(unary));
        Assert.Equal("__Method_GetWidgetsById", ProtoNaming.MethodFieldName(unary));
        Assert.Equal("__Method_WatchWidgets", ProtoNaming.MethodFieldName(streaming));

        Assert.True(ProtoNaming.IsUnary(unary));
        Assert.False(ProtoNaming.IsUnary(streaming));

        // The suffix exists only to keep a unary rpc's two client overloads apart. A streaming rpc
        // has no blocking form to clash with, so looking for WatchWidgetsAsync finds nothing.
        Assert.Equal("GetWidgetsByIdAsync", ProtoNaming.AsyncMethodName(unary));
        Assert.Equal("WatchWidgets", ProtoNaming.AsyncMethodName(streaming));

        var serviceMembers = DeclaredMembers(
            FixturePaths.WidgetsGrpcGeneratedFile, "ProtoFixture.Widgets.WidgetService");
        Assert.Contains("__Method_GetWidgetsById", serviceMembers);
        Assert.Contains("__Method_WatchWidgets", serviceMembers);

        // Grpc.Tools does not turn on the plugin's Async-suffixed server methods, so the class an
        // implementation overrides carries the plain rpc name.
        var baseMembers = DeclaredMembers(
            FixturePaths.WidgetsGrpcGeneratedFile, "ProtoFixture.Widgets.WidgetService.WidgetServiceBase");
        Assert.Contains("GetWidgetsById", baseMembers);
        Assert.Contains("WatchWidgets", baseMembers);
        Assert.DoesNotContain("GetWidgetsByIdAsync", baseMembers);

        var clientMembers = DeclaredMembers(
            FixturePaths.WidgetsGrpcGeneratedFile, "ProtoFixture.Widgets.WidgetService.WidgetServiceClient");
        Assert.Contains("GetWidgetsById", clientMembers);
        Assert.Contains("GetWidgetsByIdAsync", clientMembers);
        Assert.Contains("WatchWidgets", clientMembers);
        Assert.DoesNotContain("WatchWidgetsAsync", clientMembers);
    }

    // ---- Reading the committed protoc output ---------------------------------------------------

    private static SyntaxNode Root(string generatedFile) =>
        s_generatedRoots.GetOrAdd(
            generatedFile,
            path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot());

    /// <summary>The name a C# author writes for a type in the generated file: its namespace, then
    /// each enclosing type, dotted.</summary>
    private static string QualifiedName(BaseTypeDeclarationSyntax declaration)
    {
        var segments = new List<string>();

        for (SyntaxNode? node = declaration; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    segments.Add(type.Identifier.ValueText);
                    break;

                case BaseNamespaceDeclarationSyntax @namespace:
                    segments.Add(@namespace.Name.ToString());
                    break;
            }
        }

        segments.Reverse();
        return string.Join('.', segments);
    }

    private static List<string> DeclaredTypes(string generatedFile) =>
        [.. Root(generatedFile).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(QualifiedName)];

    private static List<string> DeclaredNamespaces(string generatedFile) =>
    [
        .. Root(generatedFile).DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(@namespace => @namespace.Name.ToString()),
    ];

    /// <summary>Every member the generated file declares directly in the named type, in source
    /// order and merged across the type's partial declarations.</summary>
    private static List<string> DeclaredMembers(string generatedFile, string qualifiedTypeName)
    {
        var names = new List<string>();

        foreach (var declaration in Root(generatedFile).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (QualifiedName(declaration) != qualifiedTypeName)
                continue;

            foreach (var member in declaration.ChildNodes().OfType<MemberDeclarationSyntax>())
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        names.AddRange(field.Declaration.Variables.Select(v => v.Identifier.ValueText));
                        break;

                    case PropertyDeclarationSyntax property:
                        names.Add(property.Identifier.ValueText);
                        break;

                    case MethodDeclarationSyntax method:
                        names.Add(method.Identifier.ValueText);
                        break;

                    case EnumMemberDeclarationSyntax value:
                        names.Add(value.Identifier.ValueText);
                        break;

                    case BaseTypeDeclarationSyntax nested:
                        names.Add(nested.Identifier.ValueText);
                        break;
                }
            }
        }

        // A qualified name that matches nothing would make every DoesNotContain below pass for
        // the wrong reason.
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>The property-name array protoc wrote into the file's <c>GeneratedClrTypeInfo</c>
    /// table for one message — the reflection descriptor's own view of the message's fields.</summary>
    private static string[] DescriptorPropertyNames(string generatedFile, string clrTypeName)
    {
        foreach (var creation in Root(generatedFile).DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var arguments = creation.ArgumentList?.Arguments;

            if (!creation.Type.ToString().EndsWith("GeneratedClrTypeInfo", StringComparison.Ordinal)
                || arguments is not { Count: >= 3 }
                || arguments.Value[0].Expression is not TypeOfExpressionSyntax typeOf
                || typeOf.Type.ToString() != "global::" + clrTypeName
                || arguments.Value[2].Expression is not ImplicitArrayCreationExpressionSyntax array)
            {
                continue;
            }

            return [.. array.Initializer.Expressions.Select(e => ((LiteralExpressionSyntax)e).Token.ValueText)];
        }

        Assert.Fail($"{Path.GetFileName(generatedFile)} declares no GeneratedClrTypeInfo for {clrTypeName}.");
        return [];
    }
}
