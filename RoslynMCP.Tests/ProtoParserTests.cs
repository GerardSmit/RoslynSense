using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The <c>.proto</c> parser on its own: no workspace, no Roslyn, no generated C#.
/// </summary>
/// <remarks>
/// Two properties of the tree carry the whole pack. Every editor feature addresses a declaration
/// by its span, so a span a single character wide of the identifier miscolours it, renames the
/// punctuation beside it and parks the caret off the word. And every binding to protoc's output
/// reproduces the descriptor's per-kind index, so a declaration numbered against the wrong
/// siblings resolves to a plausible neighbour instead of to nothing. Spans are asserted by slicing
/// the source and comparing the string, because an assertion on offsets or lengths passes on
/// exactly the off-by-one it is meant to catch.
/// </remarks>
public class ProtoParserTests
{
    private static ProtoFile Parse(string path) =>
        ProtoParser.Parse(path, SourceText.From(File.ReadAllText(path)));

    private static ProtoFile Parse(string path, string text) =>
        ProtoParser.Parse(path, SourceText.From(text));

    /// <summary>The source a span actually covers.</summary>
    private static string Slice(string text, TextSpan span) => text.Substring(span.Start, span.Length);

    [Fact]
    public void EachKindOfTopLevelDeclarationIsNumberedFromZeroOnItsOwn()
    {
        var file = Parse(FixturePaths.WidgetsProtoFile);

        // The service is written above every message, and the first message is still index 0:
        // protoc's descriptor counts MessageTypes and Services separately, so a shared counter
        // would push every message in this file one place along and bind it to its neighbour.
        var service = Assert.Single(file.Services);
        Assert.Equal(0, service.DeclarationIndex);

        Assert.Equal(
            new[]
            {
                "GetWidgetsByIdRequest", "GetWidgetsByIdReply", "GetMembersForGroupsRequest",
                "GetMembersForGroupsReply", "WatchWidgetsRequest", "WidgetEvent",
            },
            file.Messages.Select(message => message.Name.Value));

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, file.Messages.Select(message => message.DeclarationIndex));

        // Rpcs are numbered within their service, which is what Services[0].Methods[N] indexes.
        Assert.Equal(new[] { 0, 1, 2 }, service.Rpcs.Select(rpc => rpc.DeclarationIndex));
        Assert.Equal("widgets.WidgetService", service.FullName);
    }

    [Fact]
    public void ANestedMessageAndANestedEnumAreBothTheZerothOfTheirKind()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");

        var placement = Assert.Single(widget.Messages);
        var visibility = Assert.Single(widget.Enums);

        // Written one after the other inside Widget, and both are index 0. NestedTypes[N] counts
        // only nested messages and EnumTypes[N] only nested enums, so counting them together
        // would send Visibility to Placement's descriptor.
        Assert.Equal(0, placement.DeclarationIndex);
        Assert.Equal(0, visibility.DeclarationIndex);

        Assert.Same(widget, placement.Parent);
        Assert.Same(widget, visibility.Parent);
        Assert.Equal("widgets.Widget.Placement", placement.FullName);
        Assert.Equal("widgets.Widget.Visibility", visibility.FullName);

        // An enum names no scope of its own — protobuf gives its values C++ scoping — which is
        // why two enums in one message may not share a value name.
        var member = visibility.Values.Single(value => value.Name.Value == "VISIBILITY_PUBLIC");
        Assert.Same(visibility, member.Parent);
        Assert.Equal("widgets.Widget.VISIBILITY_PUBLIC", member.FullName);
        Assert.Equal(2, member.DeclarationIndex);

        // The nested message's own fields restart from zero rather than continuing Widget's.
        Assert.Equal(new[] { 0, 1 }, placement.AllFields.Select(field => field.DeclarationIndex));
    }

    [Fact]
    public void ADeclarationsNameSpanCoversTheIdentifierAndNothingElse()
    {
        string text = File.ReadAllText(FixturePaths.WidgetTypesProtoFile);
        var file = Parse(FixturePaths.WidgetTypesProtoFile, text);

        Assert.NotEmpty(file.AllDeclarations);

        foreach (var declaration in file.AllDeclarations)
            Assert.Equal(declaration.Name.Value, Slice(text, declaration.Name.Span));

        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        Assert.Equal("Widget", Slice(text, widget.Name.Span));

        var createdAt = widget.AllFields.Single(field => field.Name.Value == "created_at");
        Assert.Equal("created_at", Slice(text, createdAt.Name.Span));

        // The name span and the declaration span are different questions and must stay different
        // answers: rename replaces the first, folding and selection-expansion grow to the second.
        Assert.Equal("google.protobuf.Timestamp created_at = 5;", Slice(text, createdAt.Span));

        // A message's span runs to its closing brace, and its body span starts at the opening one.
        Assert.StartsWith("message Widget {", Slice(text, widget.Span), StringComparison.Ordinal);
        Assert.EndsWith("}", Slice(text, widget.Span), StringComparison.Ordinal);
        Assert.StartsWith("{", Slice(text, widget.BodySpan), StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeReferencesSpanCoversTheDottedNameAndNothingElse()
    {
        string text = File.ReadAllText(FixturePaths.WidgetTypesProtoFile);
        var file = Parse(FixturePaths.WidgetTypesProtoFile, text);

        Assert.NotEmpty(file.TypeReferences);

        foreach (var reference in file.TypeReferences)
            Assert.Equal(reference.Text, Slice(text, reference.Span));

        var widget = file.Messages.Single(message => message.Name.Value == "Widget");

        // The dots belong to the name; the whitespace and the field around it do not. Everything
        // downstream resolves the text of this span through the import graph.
        var uuid = widget.AllFields.Single(field => field.Name.Value == "uuid");
        Assert.Equal("common.UUID", Slice(text, uuid.Type.Span));
        Assert.False(uuid.Type.IsScalar);
        Assert.False(uuid.Type.IsFullyQualified);

        var createdAt = widget.AllFields.Single(field => field.Name.Value == "created_at");
        Assert.Equal("google.protobuf.Timestamp", Slice(text, createdAt.Type.Span));

        var id = widget.AllFields.Single(field => field.Name.Value == "id");
        Assert.Equal("int64", Slice(text, id.Type.Span));
        Assert.Equal(ProtoScalarKind.Int64, id.Type.Scalar);
    }

    [Fact]
    public void AFullyQualifiedTypeKeepsItsLeadingDotInsideItsSpan()
    {
        const string Source = """
            syntax = "proto3";

            package widgets;

            message Holder {
              .common.UUID uuid = 1;
            }
            """;

        var file = Parse("widgets/holder.proto", Source);
        var uuid = Assert.Single(Assert.Single(file.Messages).AllFields);

        // A rooted name skips relative lookup entirely, so the dot has to be inside the span the
        // resolver is handed — dropping it would silently turn the reference into a relative one.
        Assert.True(uuid.Type.IsFullyQualified);
        Assert.Equal(".common.UUID", uuid.Type.Text);
        Assert.Equal(".common.UUID", Slice(Source, uuid.Type.Span));
    }

    [Fact]
    public void AStreamKeywordIsNotPartOfTheRpcsTypeSpan()
    {
        string text = File.ReadAllText(FixturePaths.WidgetsProtoFile);
        var file = Parse(FixturePaths.WidgetsProtoFile, text);
        var service = Assert.Single(file.Services);

        var watch = service.Rpcs.Single(rpc => rpc.Name.Value == "WatchWidgets");
        Assert.True(watch.ServerStreaming);
        Assert.False(watch.ClientStreaming);

        // A span that swallowed the keyword would send go-to-definition looking for a message
        // called "stream WidgetEvent", which resolves to nothing at all.
        Assert.Equal("WatchWidgetsRequest", Slice(text, watch.RequestType.Span));
        Assert.Equal("WidgetEvent", Slice(text, watch.ResponseType.Span));

        var unary = service.Rpcs.Single(rpc => rpc.Name.Value == "GetWidgetsById");
        Assert.False(unary.ClientStreaming);
        Assert.False(unary.ServerStreaming);
        Assert.Equal("GetWidgetsByIdRequest", Slice(text, unary.RequestType.Span));
        Assert.Equal("GetWidgetsByIdReply", Slice(text, unary.ResponseType.Span));
    }

    [Fact]
    public void AOneofMemberIsScopedToItsMessageAndCountsAmongItsFields()
    {
        var file = Parse(FixturePaths.WidgetTypesProtoFile);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        var image = Assert.Single(widget.Oneofs);
        var imageUrl = image.Fields.Single(field => field.Name.Value == "image_url");

        Assert.Same(image, imageUrl.Oneof);
        Assert.Equal("widgets.Widget.image", image.FullName);

        // Transparent in three ways at once, because protobuf scopes a oneof's members on the
        // enclosing message: the parent is the message, the full name skips the oneof, and the
        // fields keep counting through the message's own numbering.
        Assert.Same(widget, imageUrl.Parent);
        Assert.Equal("widgets.Widget.image_url", imageUrl.FullName);

        // The order protoc emits properties in, with the oneof's members inline where the oneof
        // was written. Fields alone would list `attributes` sixth and bind it to `image_url`.
        Assert.Equal(
            new[]
            {
                "id", "uuid", "label", "channel", "created_at", "image_url", "image_hash",
                "attributes", "placement", "visibility",
            },
            widget.AllFields.Select(field => field.Name.Value));

        Assert.DoesNotContain(widget.Fields, field => field.Name.Value == "image_url");

        // Which makes a field's index its position in that sequence, and nothing else.
        Assert.Equal(
            widget.AllFields.Select((_, index) => index),
            widget.AllFields.Select(field => field.DeclarationIndex));

        // The oneof is numbered among the oneofs, not among the fields it holds.
        Assert.Equal(0, image.DeclarationIndex);
    }

    [Fact]
    public void AMapFieldCarriesAKeyTypeAndBothHalvesKeepTheirOwnSpan()
    {
        string text = File.ReadAllText(FixturePaths.WidgetTypesProtoFile);
        var file = Parse(FixturePaths.WidgetTypesProtoFile, text);
        var widget = file.Messages.Single(message => message.Name.Value == "Widget");
        var attributes = widget.AllFields.Single(field => field.Name.Value == "attributes");

        Assert.True(attributes.IsMap);
        Assert.NotNull(attributes.MapKeyType);
        Assert.Equal("string", attributes.MapKeyType!.Text);
        Assert.Equal("string", attributes.Type.Text);

        // Both halves of `map<string, string>` are spelled the same, so only the spans tell them
        // apart — and colouring or renaming the value type has to land on the second one.
        Assert.True(attributes.MapKeyType.Span.End <= attributes.Type.Span.Start);
        Assert.Equal("string", Slice(text, attributes.MapKeyType.Span));
        Assert.Equal("string", Slice(text, attributes.Type.Span));

        // The key type is the only thing that marks a field as a map, so every other field in the
        // message has to leave it null.
        foreach (var field in widget.AllFields.Where(field => field.Name.Value != "attributes"))
        {
            Assert.False(field.IsMap);
            Assert.Null(field.MapKeyType);
        }
    }

    [Fact]
    public void AMapValueThatNamesAMessageIsNotReadAsAScalar()
    {
        string text = File.ReadAllText(FixturePaths.WidgetsProtoFile);
        var file = Parse(FixturePaths.WidgetsProtoFile, text);
        var reply = file.Messages.Single(message => message.Name.Value == "GetMembersForGroupsReply");
        var groupMembers = Assert.Single(reply.AllFields);

        Assert.True(groupMembers.IsMap);

        // The value half is the field's Type, and it is a named type that has to be resolved
        // through the import graph — reading it as a scalar would leave it unnavigable.
        Assert.Equal(ProtoScalarKind.Int64, groupMembers.MapKeyType!.Scalar);
        Assert.False(groupMembers.Type.IsScalar);
        Assert.Equal("int64", Slice(text, groupMembers.MapKeyType.Span));
        Assert.Equal("GroupMemberList", Slice(text, groupMembers.Type.Span));
    }

    [Fact]
    public void ACaretOnANameFindsThatDeclarationAndACaretOnATypeFindsTheReference()
    {
        string text = File.ReadAllText(FixturePaths.WidgetTypesProtoFile);
        var file = Parse(FixturePaths.WidgetTypesProtoFile, text);

        int placement = text.IndexOf("message Placement", StringComparison.Ordinal) + "message ".Length;
        Assert.Equal("Placement", file.DeclarationNamedAt(placement)?.Name.Value);

        // Being somewhere inside a message is not the same as being on the word that names it.
        // Rename and go-to-definition ask the second question, and answering it with the first
        // would rename the enclosing message from anywhere in its body.
        int insideBody = text.IndexOf("int32 row", StringComparison.Ordinal);
        Assert.Null(file.DeclarationNamedAt(insideBody));
        Assert.Equal("row", file.DeclarationAt(insideBody)?.Name.Value);

        int uuidType = text.IndexOf("common.UUID", StringComparison.Ordinal);
        Assert.Equal("common.UUID", file.TypeReferenceAt(uuidType)?.Text);

        Assert.Equal("widgets.Widget.Placement", file.FindByFullName("widgets.Widget.Placement")?.FullName);
    }

    /// <summary>
    /// Four kinds of damage at once, in the order a half-typed buffer produces them: a field
    /// missing its semicolon, a token that belongs to no construct, an unterminated literal, and
    /// the closing brace that has not been typed yet.
    /// </summary>
    private const string DamagedSource = """
        syntax = "proto3";

        package damaged;

        message Intact {
          int64 id = 1;
          string label = 2;
        }

        enum AlsoIntact {
          ALSO_INTACT_UNSPECIFIED = 0;
          ALSO_INTACT_READY = 1;
        }

        message Damaged {
          int64 id = 1
          string label = 2;
          ? stray = 3;
          option (note) = "never closed;
        """;

    [Fact]
    public void AFileBrokenFourWaysStillYieldsTheDeclarationsWrittenBeforeTheDamage()
    {
        var file = Parse("damaged/damaged.proto", DamagedSource);

        // Degrading to an empty tree is the failure this guards against. The parser runs on the
        // editor's buffer, where the input is half-written far more often than it is complete, so
        // an empty tree blanks the outline and every semantic colour on alternate keystrokes.
        var intact = file.Messages.Single(message => message.Name.Value == "Intact");
        Assert.Equal(new[] { "id", "label" }, intact.AllFields.Select(field => field.Name.Value));
        Assert.Equal(new[] { 1, 2 }, intact.AllFields.Select(field => field.Number));

        var alsoIntact = Assert.Single(file.Enums);
        Assert.Equal(
            new[] { "ALSO_INTACT_UNSPECIFIED", "ALSO_INTACT_READY" },
            alsoIntact.Values.Select(value => value.Name.Value));

        // The salvage reaches into the broken message too: one bad line costs one declaration,
        // not the rest of the file. The field missing its semicolon survives, so does the one
        // after it, and the unparseable line yields nothing rather than a half-built field.
        var damaged = file.Messages.Single(message => message.Name.Value == "Damaged");
        Assert.Equal(new[] { "id", "label" }, damaged.AllFields.Select(field => field.Name.Value));
        Assert.DoesNotContain(damaged.AllFields, field => field.Name.Value == "stray");

        var ids = file.Diagnostics.Select(diagnostic => diagnostic.Id).ToArray();
        Assert.Contains(ProtoDiagnosticIds.TokenExpected, ids);
        Assert.Contains(ProtoDiagnosticIds.UnexpectedToken, ids);
        Assert.Contains(ProtoDiagnosticIds.UnterminatedString, ids);
        Assert.Contains(ProtoDiagnosticIds.UnclosedBrace, ids);

        // Every diagnostic has to be reachable in the buffer it was reported against, or the
        // editor cannot underline it.
        foreach (var diagnostic in file.Diagnostics)
            Assert.True(diagnostic.Span.End <= DamagedSource.Length);
    }

    [Fact]
    public void AProto2FileParsesWithItsLabelsIntact()
    {
        const string Source = """
            syntax = "proto2";

            package legacy;

            message Person {
              required string name = 1;
              optional int32 age = 2;
              repeated string nicknames = 3;
            }
            """;

        var file = Parse("legacy/person.proto", Source);

        Assert.Equal(ProtoSyntaxLevel.Proto2, file.SyntaxLevel);
        Assert.Null(file.Edition);

        var person = Assert.Single(file.Messages);
        Assert.Equal(
            new[] { ProtoFieldLabel.Required, ProtoFieldLabel.Optional, ProtoFieldLabel.Repeated },
            person.AllFields.Select(field => field.Label));

        // A dialect the pack does not target still has to navigate: solutions hold both, and
        // rejecting the file would leave it with no outline and no go-to-definition at all.
        Assert.DoesNotContain(file.Diagnostics, d => d.Severity == ProtoDiagnosticSeverity.Error);
    }

    [Fact]
    public void AnEditionFileParsesAndKeepsTheEditionItDeclared()
    {
        const string Source = """
            edition = "2023";

            package modern;

            message Reading {
              int32 celsius = 1;
            }
            """;

        var file = Parse("modern/reading.proto", Source);

        Assert.Equal(ProtoSyntaxLevel.Edition, file.SyntaxLevel);
        Assert.Equal("2023", file.Edition);
        Assert.Equal("celsius", Assert.Single(Assert.Single(file.Messages).AllFields).Name.Value);

        // Not modelling feature resolution is not the same as failing to read the file. The gap
        // is reported at information severity so it is visible without disabling the editor.
        Assert.DoesNotContain(file.Diagnostics, d => d.Severity == ProtoDiagnosticSeverity.Error);
        Assert.Contains(
            file.Diagnostics,
            d => d.Id == ProtoDiagnosticIds.NotModelled
                && d.Severity == ProtoDiagnosticSeverity.Information);
    }

    [Fact]
    public void ACommentBlockAboveADeclarationBecomesItsDocumentation()
    {
        const string Source = """
            syntax = "proto3";

            package docs;

            // A widget.
            // Two lines, one block.
            message Widget {
              // The primary key.
              int64 id = 1;

              string label = 2; // Trailing note, about the line it is on.
              bool active = 3;
            }
            """;

        var file = Parse("docs/docs.proto", Source);
        var widget = Assert.Single(file.Messages);

        Assert.Equal("A widget.\nTwo lines, one block.", widget.Documentation);
        Assert.Equal("The primary key.", widget.AllFields[0].Documentation);

        // A comment that starts on a line where code has already been written describes that
        // code. Attaching it to what follows would caption `active` with a note about `label`.
        Assert.Null(widget.AllFields[1].Documentation);
        Assert.Null(widget.AllFields[2].Documentation);
    }

    [Fact]
    public void ABlankLineBetweenACommentAndADeclarationBreaksTheAssociation()
    {
        const string Source = """
            syntax = "proto3";

            package docs;

            // Copyright someone.
            // All rights reserved.

            message Unrelated {
              int64 id = 1;
            }
            """;

        var file = Parse("docs/header.proto", Source);

        // Without this rule the licence header at the top of nearly every real .proto becomes the
        // hover text of whatever the file happens to declare first.
        Assert.Null(Assert.Single(file.Messages).Documentation);
    }

    [Fact]
    public void AOneofKeepsItsOwnOptionsAndACaretOnOneFindsIt()
    {
        const string Source = """
            edition = "2023";

            package opts;

            message Payload {
              oneof body {
                option features.field_presence = EXPLICIT;
                string text = 1;
                bytes blob = 2;
              }
            }
            """;

        var file = Parse("opts/payload.proto", Source);
        var oneof = Assert.Single(Assert.Single(file.Messages).Oneofs);

        // A oneof is the one declaration whose options were parsed and thrown away, which read as
        // deliberate until editions started putting features.* on them: dropping them leaves hover
        // with nothing to say about a oneof that is explicitly configured.
        var option = Assert.Single(oneof.Options);
        Assert.Equal("features.field_presence", option.Name);
        Assert.Equal("features.field_presence", Slice(Source, option.NameSpan));

        // Kept reachable as well as kept: a resolver arm that never mentions oneofs leaves the
        // caret unclassified, so the options would survive the parse and still reach nobody.
        var hit = ProtoSymbolResolver.ResolveAt(file, option.NameSpan.Start + 1);
        Assert.Equal(ProtoHitKind.OptionName, hit?.Kind);
        Assert.Equal("features.field_presence", hit?.Name);
    }
}
