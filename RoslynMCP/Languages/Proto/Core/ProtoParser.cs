using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>
/// Turns <c>.proto</c> source into a <see cref="ProtoFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// The parser never throws and never returns null. It runs against the editor's buffer, so the
/// input is half-written far more often than it is complete: anything it cannot make sense of
/// becomes a <see cref="ProtoParseDiagnostic"/> and it resynchronises at the next <c>;</c> or
/// <c>}</c> at the current depth. That keeps the declarations around a broken line intact, which
/// is the whole point — losing the tree because one field is mid-word would blank the outline and
/// every semantic colour in the file on alternate keystrokes.
/// </para>
/// <para>
/// It targets proto3 but accepts proto2 and editions rather than rejecting them, because a
/// solution usually holds both and a file that fails to parse offers no navigation at all.
/// Constructs it reads but does not model — <c>group</c>, edition feature resolution — are
/// reported as information so the gap is visible without being an error.
/// </para>
/// </remarks>
internal static class ProtoParser
{
    public static ProtoFile Parse(string filePath, SourceText text) =>
        new Parser(filePath, text).ParseFile();

    /// <summary>The builders a message body fills, kept together so every member-parsing method
    /// takes one parameter instead of eight.</summary>
    private sealed class MessageBody
    {
        public readonly ImmutableArray<ProtoField>.Builder Fields = ImmutableArray.CreateBuilder<ProtoField>();
        public readonly ImmutableArray<ProtoOneof>.Builder Oneofs = ImmutableArray.CreateBuilder<ProtoOneof>();
        public readonly ImmutableArray<ProtoMessage>.Builder Messages = ImmutableArray.CreateBuilder<ProtoMessage>();
        public readonly ImmutableArray<ProtoEnum>.Builder Enums = ImmutableArray.CreateBuilder<ProtoEnum>();
        public readonly ImmutableArray<ProtoExtend>.Builder Extends = ImmutableArray.CreateBuilder<ProtoExtend>();
        public readonly ImmutableArray<ProtoOption>.Builder Options = ImmutableArray.CreateBuilder<ProtoOption>();
        public readonly ImmutableArray<ProtoField>.Builder AllFields = ImmutableArray.CreateBuilder<ProtoField>();
        public readonly ImmutableArray<ProtoDeclaration>.Builder Children = ImmutableArray.CreateBuilder<ProtoDeclaration>();
    }

    private sealed class Parser
    {
        private static readonly int s_kindCount = Enum.GetValues<ProtoDeclarationKind>().Length;

        private readonly string _filePath;
        private readonly SourceText _text;
        private readonly ImmutableArray<ProtoToken> _tokens;

        private readonly ImmutableArray<ProtoParseDiagnostic>.Builder _diagnostics =
            ImmutableArray.CreateBuilder<ProtoParseDiagnostic>();

        private readonly ImmutableArray<ProtoTypeRef>.Builder _typeReferences =
            ImmutableArray.CreateBuilder<ProtoTypeRef>();

        private readonly ImmutableArray<ProtoDeclaration>.Builder _allDeclarations =
            ImmutableArray.CreateBuilder<ProtoDeclaration>();

        private int _index;

        public Parser(string filePath, SourceText text)
        {
            _filePath = filePath;
            _text = text;
            _tokens = ProtoLexer.Lex(text, _diagnostics);
        }

        // ---- File ------------------------------------------------------------------------------

        public ProtoFile ParseFile()
        {
            var imports = ImmutableArray.CreateBuilder<ProtoImport>();
            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            var messages = ImmutableArray.CreateBuilder<ProtoMessage>();
            var enums = ImmutableArray.CreateBuilder<ProtoEnum>();
            var services = ImmutableArray.CreateBuilder<ProtoService>();
            var extends = ImmutableArray.CreateBuilder<ProtoExtend>();

            // Top-level declarations in source order, which is what the flattening walk needs:
            // messages, enums and services interleave in a real file.
            var topLevel = ImmutableArray.CreateBuilder<ProtoDeclaration>();

            var level = ProtoSyntaxLevel.Proto2;
            string? edition = null;
            var syntaxSpan = default(TextSpan);
            string package = string.Empty;
            var packageSpan = default(TextSpan);
            bool sawSyntax = false;

            while (!AtEnd)
            {
                int before = _index;

                if (Current.Kind == ProtoTokenKind.Semicolon)
                {
                    Advance();
                    continue;
                }

                if (IsKeyword("syntax") || IsKeyword("edition"))
                {
                    var statement = ParseSyntaxStatement();

                    // A second statement is a mistake, not a redefinition: the first one is what
                    // protoc would have honoured, so it stays.
                    if (!sawSyntax)
                    {
                        (level, edition, syntaxSpan) = statement;
                        sawSyntax = true;
                    }
                }
                else if (IsKeyword("package"))
                {
                    var declared = ParsePackage();

                    if (package.Length == 0)
                        (package, packageSpan) = declared;
                }
                else if (IsKeyword("import"))
                {
                    if (ParseImport() is { } import)
                        imports.Add(import);
                }
                else if (IsKeyword("option"))
                {
                    options.Add(ParseOptionStatement());
                }
                else if (IsKeyword("message"))
                {
                    var message = ParseMessage();
                    messages.Add(message);
                    topLevel.Add(message);
                }
                else if (IsKeyword("enum"))
                {
                    var @enum = ParseEnum();
                    enums.Add(@enum);
                    topLevel.Add(@enum);
                }
                else if (IsKeyword("service"))
                {
                    var service = ParseService();
                    services.Add(service);
                    topLevel.Add(service);
                }
                else if (IsKeyword("extend"))
                {
                    var extend = ParseExtend();
                    extends.Add(extend);
                    topLevel.Add(extend);
                }
                else
                {
                    ReportUnexpected();
                    Recover(insideBody: false);
                }

                // Every path above either consumes a token or recovers, but a malformed file must
                // not be able to prove otherwise.
                if (_index == before)
                    Advance();
            }

            if (!sawSyntax)
            {
                Report(
                    ProtoDiagnosticIds.MissingSyntax,
                    "No 'syntax' statement; 'proto2' is assumed, as protoc assumes it.",
                    new TextSpan(0, 0),
                    ProtoDiagnosticSeverity.Warning);
            }

            var counters = new int[s_kindCount];
            foreach (var declaration in topLevel)
                Complete(declaration, parent: null, scope: package, counters);

            return new ProtoFile(_filePath, _text)
            {
                SyntaxLevel = level,
                Edition = edition,
                SyntaxSpan = syntaxSpan,
                Package = package,
                PackageSpan = packageSpan,
                CSharpNamespace = FindOption(options, "csharp_namespace"),
                Imports = imports.ToImmutable(),
                Options = options.ToImmutable(),
                Messages = messages.ToImmutable(),
                Enums = enums.ToImmutable(),
                Services = services.ToImmutable(),
                Extends = extends.ToImmutable(),
                Diagnostics = _diagnostics.ToImmutable(),
                TypeReferences = _typeReferences.ToImmutable(),
                AllDeclarations = _allDeclarations.ToImmutable(),
            };
        }

        private static string? FindOption(ImmutableArray<ProtoOption>.Builder options, string name)
        {
            foreach (var option in options)
            {
                if (string.Equals(option.Name, name, StringComparison.Ordinal))
                    return option.Value;
            }

            return null;
        }

        /// <summary>
        /// Fills in what could not be known while a declaration was being read — its parent, its
        /// fully-qualified name and its index among its siblings — and flattens the tree in
        /// declaration order on the way through.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only a message and a service name a scope their members live in. An <c>enum</c> does
        /// not: protobuf gives enum values C++ scoping, so <c>Kind.K_A</c> declared in message
        /// <c>Outer</c> is <c>Outer.K_A</c> and not <c>Outer.Kind.K_A</c> — which is also why two
        /// enums in one message cannot share a value name. An <c>extend</c> block names no scope
        /// either, since its fields extend the enclosing scope rather than belong to the block.
        /// </para>
        /// <para>
        /// A <c>oneof</c> is transparent in three ways at once, because protobuf scopes its members
        /// on the enclosing message: their parent is the message, their full name skips the oneof's
        /// name, and they keep counting through the message's field numbering rather than
        /// restarting. That last one is what makes a field's
        /// <see cref="ProtoDeclaration.DeclarationIndex"/> line up with the property-name array in
        /// protoc's generated reflection descriptor, where oneof members sit inline with the rest.
        /// </para>
        /// </remarks>
        private void Complete(ProtoDeclaration declaration, ProtoDeclaration? parent, string scope, int[] counters)
        {
            declaration.Parent = parent;
            declaration.DeclarationIndex = counters[(int)declaration.Kind]++;
            declaration.FullName = Scoped(scope, declaration.Name.Value);

            _allDeclarations.Add(declaration);

            bool isOneof = declaration.Kind == ProtoDeclarationKind.Oneof;
            bool opensScope = declaration.Kind is ProtoDeclarationKind.Message or ProtoDeclarationKind.Service;

            string childScope = opensScope ? declaration.FullName : scope;
            var childParent = isOneof ? parent : declaration;
            int[] childCounters = isOneof ? counters : new int[s_kindCount];

            foreach (var child in declaration.ChildDeclarations)
                Complete(child, childParent, childScope, childCounters);
        }

        /// <summary>
        /// Qualifies a declared name with the scope it was written in.
        /// </summary>
        /// <remarks>
        /// Only an <c>extend</c> block can arrive here with a rooted name, since its name is the
        /// target it was written against — <c>extend .google.protobuf.FieldOptions</c> — and a
        /// rooted name already says where it points, so the scope must not be prepended to it. A
        /// relative target is scoped like everything else, which is the whole reason this is one
        /// rule rather than a special case: a block left at its raw written name would sit in a
        /// different package from the fields inside it. What the block's name means is a separate
        /// matter — it names the extended message rather than the block — which is why
        /// <see cref="ProtoFile.FindByFullName"/> and the duplicate-name check both leave
        /// <c>extend</c> out.
        /// </remarks>
        private static string Scoped(string scope, string name)
        {
            if (name.StartsWith('.'))
                return name[1..];

            return scope.Length == 0 ? name : string.Concat(scope, ".", name);
        }

        // ---- File-level statements -------------------------------------------------------------

        private (ProtoSyntaxLevel Level, string? Edition, TextSpan Span) ParseSyntaxStatement()
        {
            var keyword = Advance();
            bool isEdition = IsKeyword(keyword, "edition");

            Expect(ProtoTokenKind.Equals, "'='");
            var (value, _) = ParseStringLiteral();
            Expect(ProtoTokenKind.Semicolon, "';'");

            var span = TextSpan.FromBounds(keyword.Span.Start, LastEnd);

            if (isEdition)
            {
                Report(
                    ProtoDiagnosticIds.NotModelled,
                    $"Edition '{value}' is read with proto3 rules; feature resolution is not modelled.",
                    span,
                    ProtoDiagnosticSeverity.Information);

                return (ProtoSyntaxLevel.Edition, value, span);
            }

            switch (value)
            {
                case "proto3":
                    return (ProtoSyntaxLevel.Proto3, null, span);

                case "proto2":
                    return (ProtoSyntaxLevel.Proto2, null, span);

                default:
                    Report(
                        ProtoDiagnosticIds.UnknownSyntax,
                        $"Unknown syntax '{value}'; the file is read as proto3.",
                        span,
                        ProtoDiagnosticSeverity.Warning);

                    return (ProtoSyntaxLevel.Proto3, null, span);
            }
        }

        private (string Name, TextSpan Span) ParsePackage()
        {
            Advance();
            var name = ParseDottedName(allowLeadingDot: false);
            Expect(ProtoTokenKind.Semicolon, "';'");
            return name;
        }

        private ProtoImport? ParseImport()
        {
            var keyword = Advance();
            bool isPublic = false;
            bool isWeak = false;

            if (IsKeyword("public"))
            {
                isPublic = true;
                Advance();
            }
            else if (IsKeyword("weak"))
            {
                isWeak = true;
                Advance();
            }

            if (Current.Kind != ProtoTokenKind.String)
            {
                ReportExpected("a quoted file path", ProtoDiagnosticIds.TokenExpected);
                Recover(insideBody: false);
                return null;
            }

            var (path, pathSpan) = ParseStringLiteral();
            Expect(ProtoTokenKind.Semicolon, "';'");

            return new ProtoImport(
                path, pathSpan, TextSpan.FromBounds(keyword.Span.Start, LastEnd), isPublic, isWeak);
        }

        // ---- Options ---------------------------------------------------------------------------

        private ProtoOption ParseOptionStatement()
        {
            var keyword = Advance();
            var option = ParseOptionBody(keyword.Span.Start);
            Expect(ProtoTokenKind.Semicolon, "';'");
            return option with { Span = TextSpan.FromBounds(keyword.Span.Start, LastEnd) };
        }

        /// <summary>The <c>name = value</c> of an option, which is all a field option is written
        /// as — the <c>option</c> keyword belongs to the statement form only.</summary>
        private ProtoOption ParseOptionBody(int start)
        {
            var (name, nameSpan) = ParseOptionName();
            string? value = null;
            var valueSpan = default(TextSpan);

            if (Expect(ProtoTokenKind.Equals, "'='"))
                (value, valueSpan) = ParseOptionValue();

            return new ProtoOption(name, nameSpan, value, valueSpan, TextSpan.FromBounds(start, LastEnd));
        }

        /// <summary>
        /// An option's name, parentheses and all: a custom option is written
        /// <c>(my.custom.opt).field</c> and keeping the spelling means recognising a known option
        /// is an ordinary string compare rather than a reconstruction.
        /// </summary>
        private (string Name, TextSpan Span) ParseOptionName()
        {
            int start = Current.Span.Start;
            int end;

            if (Current.Kind == ProtoTokenKind.OpenParen)
            {
                Advance();
                ParseDottedName(allowLeadingDot: true);

                if (Current.Kind == ProtoTokenKind.CloseParen)
                {
                    end = Advance().Span.End;
                }
                else
                {
                    ReportExpected("')'", ProtoDiagnosticIds.TokenExpected);
                    end = LastEnd;
                }
            }
            else if (Current.Kind == ProtoTokenKind.Identifier)
            {
                end = Advance().Span.End;
            }
            else
            {
                ReportExpected("an option name", ProtoDiagnosticIds.IdentifierExpected);
                return (string.Empty, new TextSpan(start, 0));
            }

            while (Current.Kind == ProtoTokenKind.Dot && Peek().Kind == ProtoTokenKind.Identifier)
            {
                Advance();
                end = Advance().Span.End;
            }

            var span = TextSpan.FromBounds(start, end);
            return (_text.ToString(span), span);
        }

        private (string? Value, TextSpan Span) ParseOptionValue()
        {
            switch (Current.Kind)
            {
                case ProtoTokenKind.String:
                {
                    var (value, span) = ParseStringLiteral();
                    return (value, span);
                }

                case ProtoTokenKind.OpenBrace:
                {
                    // A message literal. Its structure has no home in this model, so it is kept as
                    // written — enough for hover, and honest about not being understood.
                    int start = Current.Span.Start;
                    SkipBalancedBraces();
                    var span = TextSpan.FromBounds(start, LastEnd);
                    return (_text.ToString(span), span);
                }

                case ProtoTokenKind.Minus:
                case ProtoTokenKind.Plus:
                {
                    int start = Advance().Span.Start;

                    // `-inf` is an identifier behind the sign, which is why this accepts both.
                    if (Current.Kind is ProtoTokenKind.Number or ProtoTokenKind.Identifier)
                    {
                        var span = TextSpan.FromBounds(start, Advance().Span.End);
                        return (_text.ToString(span), span);
                    }

                    ReportExpected("a number", ProtoDiagnosticIds.TokenExpected);
                    return (null, new TextSpan(start, 0));
                }

                case ProtoTokenKind.Number:
                {
                    var token = Advance();
                    return (_text.ToString(token.Span), token.Span);
                }

                case ProtoTokenKind.Identifier:
                {
                    var (text, span) = ParseDottedName(allowLeadingDot: false);
                    return (text, span);
                }

                default:
                    ReportExpected("an option value", ProtoDiagnosticIds.TokenExpected);
                    return (null, new TextSpan(Current.Span.Start, 0));
            }
        }

        /// <summary>The <c>[ … ]</c> list on a field or an enum value.</summary>
        private ImmutableArray<ProtoOption> ParseInlineOptions()
        {
            if (Current.Kind != ProtoTokenKind.OpenBracket)
                return [];

            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            Advance();

            while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBracket)
            {
                // A missing `]` must not let the option list eat the rest of the body.
                if (Current.Kind is ProtoTokenKind.Semicolon or ProtoTokenKind.CloseBrace)
                    break;

                int before = _index;
                options.Add(ParseOptionBody(Current.Span.Start));

                if (Current.Kind == ProtoTokenKind.Comma)
                    Advance();

                if (_index == before)
                    Advance();
            }

            if (Current.Kind == ProtoTokenKind.CloseBracket)
                Advance();
            else
                ReportExpected("']'", ProtoDiagnosticIds.TokenExpected);

            return options.ToImmutable();
        }

        // ---- Messages --------------------------------------------------------------------------

        private ProtoMessage ParseMessage()
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var name = ParseName();
            var (body, bodySpan) = ParseMessageBody();

            return CreateMessage(keyword.Span.Start, name, documentation, body, bodySpan);
        }

        private ProtoMessage CreateMessage(
            int start, ProtoName name, string? documentation, MessageBody body, TextSpan bodySpan) =>
            new(name, TextSpan.FromBounds(start, LastEnd), documentation)
            {
                BodySpan = bodySpan,
                Fields = body.Fields.ToImmutable(),
                Oneofs = body.Oneofs.ToImmutable(),
                Messages = body.Messages.ToImmutable(),
                Enums = body.Enums.ToImmutable(),
                Extends = body.Extends.ToImmutable(),
                Options = body.Options.ToImmutable(),
                AllFields = body.AllFields.ToImmutable(),
                ChildDeclarations = body.Children.ToImmutable(),
            };

        private (MessageBody Body, TextSpan BodySpan) ParseMessageBody()
        {
            var body = new MessageBody();
            int brace = OpenBody();

            if (brace >= 0)
            {
                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;
                    ParseMessageMember(body);

                    if (_index == before)
                        Advance();
                }
            }

            return (body, CloseBody(brace));
        }

        private void ParseMessageMember(MessageBody body)
        {
            switch (Current.Kind)
            {
                case ProtoTokenKind.Semicolon:
                    Advance();
                    return;

                case ProtoTokenKind.Dot:
                    // A field whose type is fully qualified: `.common.UUID uuid = 1;`.
                    AddField(body, ParseField(Current, ProtoFieldLabel.None));
                    return;

                case ProtoTokenKind.Identifier:
                    break;

                default:
                    ReportUnexpected();
                    Recover(insideBody: true);
                    return;
            }

            var start = Current;

            if (IsKeyword("message"))
            {
                var message = ParseMessage();
                body.Messages.Add(message);
                body.Children.Add(message);
                return;
            }

            if (IsKeyword("enum"))
            {
                var @enum = ParseEnum();
                body.Enums.Add(@enum);
                body.Children.Add(@enum);
                return;
            }

            if (IsKeyword("extend"))
            {
                var extend = ParseExtend();
                body.Extends.Add(extend);
                body.Children.Add(extend);
                return;
            }

            if (IsKeyword("oneof"))
            {
                var oneof = ParseOneof(body);
                body.Oneofs.Add(oneof);
                body.Children.Add(oneof);
                return;
            }

            if (IsKeyword("option"))
            {
                body.Options.Add(ParseOptionStatement());
                return;
            }

            if (IsKeyword("reserved") || IsKeyword("extensions"))
            {
                // Both carry number ranges and names that nothing navigates to; they are consumed
                // so the body stays parseable and otherwise dropped.
                SkipStatement();
                return;
            }

            var label = ParseLabel();

            if (IsKeyword("group") && Peek().Kind == ProtoTokenKind.Identifier)
            {
                var group = ParseGroup(start);
                body.Messages.Add(group);
                body.Children.Add(group);
                return;
            }

            AddField(body, ParseField(start, label));
        }

        private static void AddField(MessageBody body, ProtoField field)
        {
            body.Fields.Add(field);
            body.AllFields.Add(field);
            body.Children.Add(field);
        }

        private ProtoOneof ParseOneof(MessageBody parent)
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var name = ParseName();

            var fields = ImmutableArray.CreateBuilder<ProtoField>();
            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            var children = ImmutableArray.CreateBuilder<ProtoDeclaration>();

            int brace = OpenBody();

            if (brace >= 0)
            {
                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;

                    if (Current.Kind == ProtoTokenKind.Semicolon)
                    {
                        Advance();
                    }
                    else if (IsKeyword("option"))
                    {
                        options.Add(ParseOptionStatement());
                    }
                    else if (Current.Kind is ProtoTokenKind.Identifier or ProtoTokenKind.Dot)
                    {
                        var field = ParseField(Current, ProtoFieldLabel.None);
                        fields.Add(field);
                        children.Add(field);

                        // The message counts these among its own fields, in the position the oneof
                        // occupies, because that is the order protoc generates properties in.
                        parent.AllFields.Add(field);
                    }
                    else
                    {
                        ReportUnexpected();
                        Recover(insideBody: true);
                    }

                    if (_index == before)
                        Advance();
                }
            }

            var bodySpan = CloseBody(brace);

            var oneof = new ProtoOneof(name, TextSpan.FromBounds(keyword.Span.Start, LastEnd), documentation)
            {
                BodySpan = bodySpan,
                Fields = fields.ToImmutable(),
                Options = options.ToImmutable(),
                ChildDeclarations = children.ToImmutable(),
            };

            foreach (var field in fields)
                field.Oneof = oneof;

            return oneof;
        }

        /// <summary>
        /// A proto2 <c>group</c>: a nested message and a field of that type written as one
        /// construct. The body becomes the nested message; the field half is left out rather than
        /// guessed at, since protoc derives its name by a lowercasing rule this model does not
        /// encode.
        /// </summary>
        private ProtoMessage ParseGroup(ProtoToken start)
        {
            Advance();
            var name = ParseName();

            if (Expect(ProtoTokenKind.Equals, "'='"))
                ParseFieldNumber();

            var (body, bodySpan) = ParseMessageBody();
            var message = CreateMessage(start.Span.Start, name, Documentation(start), body, bodySpan);

            Report(
                ProtoDiagnosticIds.NotModelled,
                $"'group {name.Value}' is read as a nested message; its implied field is not modelled.",
                message.Span,
                ProtoDiagnosticSeverity.Information);

            return message;
        }

        // ---- Fields ----------------------------------------------------------------------------

        /// <summary>
        /// A label, but only when a type name follows it: <c>optional</c> is a perfectly good field
        /// name, and in <c>optional = 1;</c> it is one.
        /// </summary>
        private ProtoFieldLabel ParseLabel()
        {
            if (Current.Kind != ProtoTokenKind.Identifier
                || Peek().Kind is not (ProtoTokenKind.Identifier or ProtoTokenKind.Dot))
            {
                return ProtoFieldLabel.None;
            }

            var label = ProtoFieldLabel.None;

            if (IsKeyword("repeated"))
                label = ProtoFieldLabel.Repeated;
            else if (IsKeyword("optional"))
                label = ProtoFieldLabel.Optional;
            else if (IsKeyword("required"))
                label = ProtoFieldLabel.Required;

            if (label != ProtoFieldLabel.None)
                Advance();

            return label;
        }

        /// <param name="start">The token the declaration begins at — the label when there is one,
        /// so that the field's span and its documentation both start where the user sees it start.</param>
        private ProtoField ParseField(ProtoToken start, ProtoFieldLabel label)
        {
            ProtoTypeRef? mapKey = null;
            ProtoTypeRef type;

            if (IsKeyword("map") && Peek().Kind == ProtoTokenKind.Less)
            {
                Advance();
                Expect(ProtoTokenKind.Less, "'<'");
                mapKey = ParseTypeRef();
                Expect(ProtoTokenKind.Comma, "','");
                type = ParseTypeRef();
                Expect(ProtoTokenKind.Greater, "'>'");
            }
            else
            {
                type = ParseTypeRef();
            }

            var name = ParseName();
            int number = 0;
            var numberSpan = default(TextSpan);

            if (Expect(ProtoTokenKind.Equals, "'='"))
                (number, numberSpan) = ParseFieldNumber();

            var options = ParseInlineOptions();
            Expect(ProtoTokenKind.Semicolon, "';'");

            return new ProtoField(name, TextSpan.FromBounds(start.Span.Start, LastEnd), Documentation(start))
            {
                Number = number,
                NumberSpan = numberSpan,
                Label = label,
                Type = type,
                MapKeyType = mapKey,
                Options = options,
            };
        }

        private (int Number, TextSpan Span) ParseFieldNumber()
        {
            int start = Current.Span.Start;
            bool negative = Current.Kind == ProtoTokenKind.Minus;

            if (Current.Kind is ProtoTokenKind.Minus or ProtoTokenKind.Plus)
                Advance();

            if (Current.Kind != ProtoTokenKind.Number)
            {
                ReportExpected("a number", ProtoDiagnosticIds.TokenExpected);
                return (0, new TextSpan(start, 0));
            }

            var token = Advance();
            var span = TextSpan.FromBounds(start, token.Span.End);

            if (!TryReadInteger(token.Span, out int value))
            {
                Report(ProtoDiagnosticIds.InvalidNumber, "Not a valid integer.", span);
                return (0, span);
            }

            return (negative ? -value : value, span);
        }

        /// <summary>
        /// Reads an integer literal straight out of the buffer — decimal, <c>0x…</c> hex, or
        /// leading-zero octal — rather than materialising a substring to hand to <c>int.Parse</c>.
        /// Field numbers are read on every keystroke and there is one per field.
        /// </summary>
        private bool TryReadInteger(TextSpan span, out int value)
        {
            value = 0;
            int position = span.Start;
            int end = span.End;

            if (position >= end)
                return false;

            int radix = 10;

            if (_text[position] == '0' && position + 1 < end)
            {
                if (_text[position + 1] is 'x' or 'X')
                {
                    radix = 16;
                    position += 2;
                }
                else
                {
                    radix = 8;
                    position++;
                }
            }

            if (position >= end)
                return true;

            long result = 0;

            for (; position < end; position++)
            {
                int digit = DigitValue(_text[position]);

                if (digit < 0 || digit >= radix)
                    return false;

                result = (result * radix) + digit;

                if (result > int.MaxValue)
                    return false;
            }

            value = (int)result;
            return true;
        }

        private static int DigitValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        // ---- Enums -----------------------------------------------------------------------------

        private ProtoEnum ParseEnum()
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var name = ParseName();

            var values = ImmutableArray.CreateBuilder<ProtoEnumValue>();
            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            var children = ImmutableArray.CreateBuilder<ProtoDeclaration>();

            int brace = OpenBody();

            if (brace >= 0)
            {
                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;

                    if (Current.Kind == ProtoTokenKind.Semicolon)
                    {
                        Advance();
                    }
                    else if (IsKeyword("option"))
                    {
                        options.Add(ParseOptionStatement());
                    }
                    else if (IsKeyword("reserved"))
                    {
                        SkipStatement();
                    }
                    else if (Current.Kind == ProtoTokenKind.Identifier)
                    {
                        var value = ParseEnumValue();
                        values.Add(value);
                        children.Add(value);
                    }
                    else
                    {
                        ReportUnexpected();
                        Recover(insideBody: true);
                    }

                    if (_index == before)
                        Advance();
                }
            }

            var bodySpan = CloseBody(brace);

            return new ProtoEnum(name, TextSpan.FromBounds(keyword.Span.Start, LastEnd), documentation)
            {
                BodySpan = bodySpan,
                Values = values.ToImmutable(),
                Options = options.ToImmutable(),
                ChildDeclarations = children.ToImmutable(),
            };
        }

        private ProtoEnumValue ParseEnumValue()
        {
            var start = Current;
            string? documentation = Documentation(start);
            var name = ParseName();

            int number = 0;
            var numberSpan = default(TextSpan);

            if (Expect(ProtoTokenKind.Equals, "'='"))
                (number, numberSpan) = ParseFieldNumber();

            var options = ParseInlineOptions();
            Expect(ProtoTokenKind.Semicolon, "';'");

            return new ProtoEnumValue(name, TextSpan.FromBounds(start.Span.Start, LastEnd), documentation)
            {
                Number = number,
                NumberSpan = numberSpan,
                Options = options,
            };
        }

        // ---- Services --------------------------------------------------------------------------

        private ProtoService ParseService()
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var name = ParseName();

            var rpcs = ImmutableArray.CreateBuilder<ProtoRpc>();
            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            var children = ImmutableArray.CreateBuilder<ProtoDeclaration>();

            int brace = OpenBody();

            if (brace >= 0)
            {
                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;

                    if (Current.Kind == ProtoTokenKind.Semicolon)
                    {
                        Advance();
                    }
                    else if (IsKeyword("option"))
                    {
                        options.Add(ParseOptionStatement());
                    }
                    else if (IsKeyword("rpc"))
                    {
                        var rpc = ParseRpc();
                        rpcs.Add(rpc);
                        children.Add(rpc);
                    }
                    else
                    {
                        ReportUnexpected();
                        Recover(insideBody: true);
                    }

                    if (_index == before)
                        Advance();
                }
            }

            var bodySpan = CloseBody(brace);

            return new ProtoService(name, TextSpan.FromBounds(keyword.Span.Start, LastEnd), documentation)
            {
                BodySpan = bodySpan,
                Rpcs = rpcs.ToImmutable(),
                Options = options.ToImmutable(),
                ChildDeclarations = children.ToImmutable(),
            };
        }

        private ProtoRpc ParseRpc()
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var name = ParseName();

            Expect(ProtoTokenKind.OpenParen, "'('");
            bool clientStreaming = ParseStreamKeyword();
            var request = ParseTypeRef();
            Expect(ProtoTokenKind.CloseParen, "')'");

            if (IsKeyword("returns"))
                Advance();
            else
                ReportExpected("'returns'", ProtoDiagnosticIds.TokenExpected);

            Expect(ProtoTokenKind.OpenParen, "'('");
            bool serverStreaming = ParseStreamKeyword();
            var response = ParseTypeRef();
            Expect(ProtoTokenKind.CloseParen, "')'");

            var options = ImmutableArray.CreateBuilder<ProtoOption>();
            var bodySpan = default(TextSpan);

            if (Current.Kind == ProtoTokenKind.OpenBrace)
            {
                int brace = Advance().Span.Start;

                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;

                    if (Current.Kind == ProtoTokenKind.Semicolon)
                        Advance();
                    else if (IsKeyword("option"))
                        options.Add(ParseOptionStatement());
                    else
                        SkipStatement();

                    if (_index == before)
                        Advance();
                }

                bodySpan = CloseBody(brace);
            }
            else
            {
                Expect(ProtoTokenKind.Semicolon, "';'");
            }

            return new ProtoRpc(name, TextSpan.FromBounds(keyword.Span.Start, LastEnd), documentation)
            {
                BodySpan = bodySpan,
                RequestType = request,
                ResponseType = response,
                ClientStreaming = clientStreaming,
                ServerStreaming = serverStreaming,
                Options = options.ToImmutable(),
            };
        }

        /// <summary>Consumes <c>stream</c>, but only when a type name follows: a message may be
        /// called <c>stream</c>.</summary>
        private bool ParseStreamKeyword()
        {
            if (!IsKeyword("stream") || Peek().Kind is not (ProtoTokenKind.Identifier or ProtoTokenKind.Dot))
                return false;

            Advance();
            return true;
        }

        // ---- Extensions ------------------------------------------------------------------------

        private ProtoExtend ParseExtend()
        {
            var keyword = Advance();
            string? documentation = Documentation(keyword);
            var target = ParseTypeRef();

            var fields = ImmutableArray.CreateBuilder<ProtoField>();
            var children = ImmutableArray.CreateBuilder<ProtoDeclaration>();

            int brace = OpenBody();

            if (brace >= 0)
            {
                while (!AtEnd && Current.Kind != ProtoTokenKind.CloseBrace)
                {
                    int before = _index;

                    if (Current.Kind == ProtoTokenKind.Semicolon)
                    {
                        Advance();
                    }
                    else if (Current.Kind is ProtoTokenKind.Identifier or ProtoTokenKind.Dot)
                    {
                        var start = Current;
                        var label = ParseLabel();
                        var field = ParseField(start, label);
                        fields.Add(field);
                        children.Add(field);
                    }
                    else
                    {
                        ReportUnexpected();
                        Recover(insideBody: true);
                    }

                    if (_index == before)
                        Advance();
                }
            }

            var bodySpan = CloseBody(brace);

            return new ProtoExtend(
                new ProtoName(target.Text, target.Span),
                TextSpan.FromBounds(keyword.Span.Start, LastEnd),
                documentation)
            {
                BodySpan = bodySpan,
                Target = target,
                Fields = fields.ToImmutable(),
                ChildDeclarations = children.ToImmutable(),
            };
        }

        // ---- Names and literals ------------------------------------------------------------------

        private ProtoName ParseName()
        {
            if (Current.Kind != ProtoTokenKind.Identifier)
            {
                ReportExpected("an identifier", ProtoDiagnosticIds.IdentifierExpected);
                return new ProtoName(string.Empty, new TextSpan(Current.Span.Start, 0));
            }

            var token = Advance();
            return new ProtoName(_text.ToString(token.Span), token.Span);
        }

        /// <summary>
        /// A dotted name, whose span covers the name and nothing around it — the dots between the
        /// segments are part of it, the whitespace on either side is not.
        /// </summary>
        private (string Text, TextSpan Span) ParseDottedName(bool allowLeadingDot)
        {
            int start = Current.Span.Start;
            int end = start;

            if (allowLeadingDot && Current.Kind == ProtoTokenKind.Dot)
                end = Advance().Span.End;

            if (Current.Kind != ProtoTokenKind.Identifier)
            {
                ReportExpected("an identifier", ProtoDiagnosticIds.IdentifierExpected);
                var partial = TextSpan.FromBounds(start, end);
                return (end > start ? _text.ToString(partial) : string.Empty, partial);
            }

            end = Advance().Span.End;

            while (Current.Kind == ProtoTokenKind.Dot && Peek().Kind == ProtoTokenKind.Identifier)
            {
                Advance();
                end = Advance().Span.End;
            }

            var span = TextSpan.FromBounds(start, end);
            return (_text.ToString(span), span);
        }

        private ProtoTypeRef ParseTypeRef()
        {
            var (text, span) = ParseDottedName(allowLeadingDot: true);
            var reference = new ProtoTypeRef(text, span, ProtoScalars.FromName(text));
            _typeReferences.Add(reference);
            return reference;
        }

        /// <summary>
        /// A string literal, or several: adjacent literals concatenate, which is how a long
        /// <c>option</c> value is wrapped across lines.
        /// </summary>
        private (string Value, TextSpan Span) ParseStringLiteral()
        {
            if (Current.Kind != ProtoTokenKind.String)
            {
                ReportExpected("a quoted string", ProtoDiagnosticIds.TokenExpected);
                return (string.Empty, new TextSpan(Current.Span.Start, 0));
            }

            var first = Advance();
            string value = first.Value ?? string.Empty;
            int end = first.Span.End;

            while (Current.Kind == ProtoTokenKind.String)
            {
                var next = Advance();
                value += next.Value;
                end = next.Span.End;
            }

            return (value, TextSpan.FromBounds(first.Span.Start, end));
        }

        // ---- Token plumbing ----------------------------------------------------------------------

        private ProtoToken Current => _tokens[_index];

        private bool AtEnd => _tokens[_index].Kind == ProtoTokenKind.EndOfFile;

        /// <summary>The end of the last token consumed, which is where a declaration that ran to a
        /// semicolon or a brace ends.</summary>
        private int LastEnd => _index > 0 ? _tokens[_index - 1].Span.End : 0;

        private ProtoToken Peek(int offset = 1)
        {
            int index = _index + offset;
            return index < _tokens.Length ? _tokens[index] : _tokens[^1];
        }

        /// <summary>Consumes the current token and returns it. At end of file it returns the
        /// end-of-file token forever, which is what lets every loop rely on <see cref="AtEnd"/>
        /// alone rather than on a bounds check as well.</summary>
        private ProtoToken Advance()
        {
            var token = _tokens[_index];

            if (token.Kind != ProtoTokenKind.EndOfFile)
                _index++;

            return token;
        }

        private bool Expect(ProtoTokenKind kind, string description)
        {
            if (Current.Kind == kind)
            {
                Advance();
                return true;
            }

            ReportExpected(description, ProtoDiagnosticIds.TokenExpected);
            return false;
        }

        private bool IsKeyword(string keyword) => IsKeyword(Current, keyword);

        /// <summary>
        /// Compares an identifier against a keyword without materialising it. Proto has no reserved
        /// words, so this runs several times per token and allocating a string for each would
        /// dominate the parse.
        /// </summary>
        private bool IsKeyword(in ProtoToken token, string keyword)
        {
            if (token.Kind != ProtoTokenKind.Identifier || token.Span.Length != keyword.Length)
                return false;

            int start = token.Span.Start;

            for (int i = 0; i < keyword.Length; i++)
            {
                if (_text[start + i] != keyword[i])
                    return false;
            }

            return true;
        }

        // ---- Bodies and recovery -------------------------------------------------------------

        /// <summary>Consumes the opening brace and returns its offset, or -1 when there is none.</summary>
        private int OpenBody()
        {
            if (Current.Kind == ProtoTokenKind.OpenBrace)
                return Advance().Span.Start;

            ReportExpected("'{'", ProtoDiagnosticIds.TokenExpected);
            return -1;
        }

        private TextSpan CloseBody(int braceStart)
        {
            if (braceStart < 0)
                return default;

            if (Current.Kind == ProtoTokenKind.CloseBrace)
                return TextSpan.FromBounds(braceStart, Advance().Span.End);

            Report(ProtoDiagnosticIds.UnclosedBrace, "Unclosed '{'.", new TextSpan(braceStart, 1));
            return TextSpan.FromBounds(braceStart, LastEnd);
        }

        /// <summary>Consumes a statement whose contents this model drops — <c>reserved</c>,
        /// <c>extensions</c> — up to and including its semicolon.</summary>
        private void SkipStatement()
        {
            while (!AtEnd && Current.Kind != ProtoTokenKind.Semicolon)
            {
                if (Current.Kind == ProtoTokenKind.CloseBrace)
                    return;

                if (Current.Kind == ProtoTokenKind.OpenBrace)
                {
                    SkipBalancedBraces();
                    continue;
                }

                Advance();
            }

            if (Current.Kind == ProtoTokenKind.Semicolon)
                Advance();
        }

        private void SkipBalancedBraces()
        {
            int depth = 0;

            do
            {
                if (Current.Kind == ProtoTokenKind.OpenBrace)
                    depth++;
                else if (Current.Kind == ProtoTokenKind.CloseBrace)
                    depth--;

                Advance();
            }
            while (!AtEnd && depth > 0);
        }

        /// <summary>
        /// Resynchronises after something unparseable.
        /// </summary>
        /// <remarks>
        /// The next <c>;</c> ends the broken statement, and a <c>{ … }</c> is skipped whole so that
        /// a half-typed header cannot make the parser read a body as if it were file level. Inside
        /// a body a <c>}</c> is left where it is — it belongs to the enclosing declaration, and
        /// consuming it here would unbalance every brace after it. A keyword that can only start a
        /// declaration also stops the scan, so one bad line costs one declaration rather than
        /// everything up to the next semicolon.
        /// </remarks>
        private void Recover(bool insideBody)
        {
            while (!AtEnd)
            {
                switch (Current.Kind)
                {
                    case ProtoTokenKind.Semicolon:
                        Advance();
                        return;

                    case ProtoTokenKind.CloseBrace:
                        if (insideBody)
                            return;

                        Advance();
                        return;

                    case ProtoTokenKind.OpenBrace:
                        SkipBalancedBraces();
                        return;

                    case ProtoTokenKind.Identifier when StartsDeclaration():
                        return;
                }

                Advance();
            }
        }

        private bool StartsDeclaration() =>
            IsKeyword("message")
            || IsKeyword("enum")
            || IsKeyword("service")
            || IsKeyword("extend")
            || IsKeyword("oneof")
            || IsKeyword("rpc")
            || IsKeyword("option")
            || IsKeyword("import")
            || IsKeyword("package")
            || IsKeyword("syntax")
            || IsKeyword("edition")
            || IsKeyword("reserved")
            || IsKeyword("extensions");

        // ---- Diagnostics -----------------------------------------------------------------------

        private string? Documentation(in ProtoToken token) =>
            token.LeadingCommentSpan.IsEmpty
                ? null
                : ProtoLexer.ExtractDocumentation(_text, token.LeadingCommentSpan);

        private void Report(
            string id,
            string message,
            TextSpan span,
            ProtoDiagnosticSeverity severity = ProtoDiagnosticSeverity.Error) =>
            _diagnostics.Add(new ProtoParseDiagnostic(id, message, span, severity));

        private void ReportUnexpected() =>
            Report(ProtoDiagnosticIds.UnexpectedToken, $"Unexpected {Describe(Current)}.", DiagnosticSpan(Current));

        private void ReportExpected(string what, string id) =>
            Report(id, $"Expected {what}, found {Describe(Current)}.", DiagnosticSpan(Current));

        /// <summary>At end of file there is nothing to underline, so the diagnostic goes on the
        /// last thing there was.</summary>
        private TextSpan DiagnosticSpan(in ProtoToken token) =>
            token.Kind == ProtoTokenKind.EndOfFile && _index > 0 ? _tokens[_index - 1].Span : token.Span;

        private string Describe(in ProtoToken token) => token.Kind switch
        {
            ProtoTokenKind.EndOfFile => "end of file",
            ProtoTokenKind.String => "a string",
            _ => $"'{_text.ToString(token.Span)}'",
        };
    }
}
