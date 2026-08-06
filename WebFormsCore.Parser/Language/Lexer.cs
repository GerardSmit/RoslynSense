using System.Text;
using WebFormsCore.Models;
using WebFormsCore.SourceGenerator.Models;

namespace WebFormsCore.Language;

public ref struct Lexer
{
    private static readonly ReadOnlyMemory<char> StartDocType = "<!DOCTYPE".ToCharArray();
    private static readonly ReadOnlyMemory<char> StartStatement = "<%".ToCharArray();
    private static readonly ReadOnlyMemory<char> End = "%>".ToCharArray();
    private static readonly ReadOnlyMemory<char> StartServerComment = "<%--".ToCharArray();
    private static readonly ReadOnlyMemory<char> EndServerComment = "--%>".ToCharArray();
    private static readonly ReadOnlyMemory<char> StartComment = "<!--".ToCharArray();
    private static readonly ReadOnlyMemory<char> EndComment = "-->".ToCharArray();
    private static readonly ReadOnlyMemory<char> RunAt = "runat".ToCharArray();

    private readonly ReadOnlySpan<char> _startStatement;
    private readonly ReadOnlySpan<char> _end;
    private readonly ReadOnlySpan<char> _endServerComment;
    private readonly ReadOnlySpan<char> _startDocType;
    private readonly ReadOnlySpan<char> _startComment;
    private readonly ReadOnlySpan<char> _startServerComment;
    private readonly ReadOnlySpan<char> _endComment;
    private readonly ReadOnlySpan<char> _runAt;

    private readonly Stack<string> _tags;

    // The opening tags that were downgraded to text, so a closing tag that gets the same
    // treatment can tell "closes a tag the lexer left as text" apart from "closes nothing".
    // A list rather than a stack: HTML lets <li> and <td> stay open, so a close may match
    // any entry, not just the top.
    private readonly List<string> _htmlTags;

    // End of the last downgraded opening tag. Once such a tag is text, the lexer re-enters
    // at every '<' inside its attribute values as if a new tag started there; anything that
    // begins before this offset is attribute text, not markup.
    private int _htmlTagEnd;

    // Whether the file has opened a tag of its own yet, which is what separates a fragment
    // finishing someone else's wrapper from a close that matches nothing.
    private bool _sawOpenTag;

    private readonly StringBuilder _textBuilder;
    private TokenPosition _textStart;
    private TokenPosition _textEnd;

    private readonly List<Token> _nodes;
    private readonly ReadOnlySpan<char> _input;
    private int _offset;
    private int _line;
    private int _column;
    private int _nodeOffset;
    private bool _ignoreNewLine;
    private bool _isStart;

    // Memo for the tag scan: _scanClose is the first '>' at or after _scanStart (input length
    // when there is none) and _scanRunAt is the first "runat" before it (-1 when there is none).
    // Inline script is full of '<' with no tag around it, and without this every one of them
    // re-scanned ahead to the same faraway '>'.
    private int _scanStart;
    private int _scanClose;
    private int _scanRunAt;

    public Lexer(string file, ReadOnlySpan<char> input)
    {
        _nodes = new List<Token>();
        _textBuilder = new StringBuilder();
        _startStatement = StartStatement.Span;
        _startDocType = StartDocType.Span;
        _startComment = StartComment.Span;
        _startServerComment = StartServerComment.Span;
        _endComment = EndComment.Span;
        _runAt = RunAt.Span;
        _end = End.Span;
        _endServerComment = EndServerComment.Span;
        _input = input;
        File = Path.GetFullPath(file);
        _line = 0;
        _column = 0;
        _offset = 0;
        _nodeOffset = -1;
        _ignoreNewLine = false;
        _textStart = default;
        _textEnd = default;
        _tags = new Stack<string>();
        _htmlTags = new List<string>();
        _htmlTagEnd = -1;
        _sawOpenTag = false;
        Diagnostics = new List<ReportedDiagnostic>();
        _isStart = true;
        _scanStart = -1;
        _scanClose = -1;
        _scanRunAt = -1;
    }

    public string File { get; }

    public List<ReportedDiagnostic> Diagnostics { get; }

    public List<int> Lines { get; } = new() { 0 };

    public TokenPosition Position => new(_offset, _line, _column);

    private char Current => _offset < _input.Length ? _input[_offset] : '\0';

    public bool HasNext => _offset < _input.Length || _nodeOffset < _nodes.Count;

    public void Forward()
    {
        _column++;
        CheckNewLine();
        _offset++;
    }

    public void Forward(int length)
    {
        _column += length;
        CheckNewLine();
        _offset += length;
    }

    private void CheckNewLine()
    {
        if (_offset >= _input.Length)
        {
            return;
        }

        var current = Current;
        var isNewLine = current is '\r' or '\n';

        if (_ignoreNewLine)
        {
            _ignoreNewLine = false;
        }
        else if (isNewLine)
        {
            _line++;
            _ignoreNewLine = current == '\r';

            Lines.Add(_offset + (_ignoreNewLine ? 2 : 1));
        }

        if (isNewLine)
        {
            _column = 0;
        }
    }

    public List<Token> GetAll()
    {
        while (Consume())
        {
            // next
        }

        return _nodes;
    }

    public Token? Next()
    {
        var result = Peek();

        if (result.HasValue)
        {
            _nodeOffset++;
        }

        return result;
    }

    public Token? Peek(int offset = 1)
    {
        var index = _nodeOffset + offset;

        while (index >= _nodes.Count)
        {
            if (!Consume())
            {
                return null;
            }
        }

        if (index >= _nodes.Count)
        {
            return null;
        }

        return _nodes[index];
    }

    private bool Consume()
    {
        if (_offset >= _input.Length)
        {
            return AddText();
        }

        if (ConsumeComment())
        {
            return true;
        }

        if (ConsumeServerComment())
        {
            return true;
        }

        if (ConsumeInline())
        {
            return true;
        }

        if (ConsumeDocType())
        {
            return true;
        }

        if (ConsumeElement())
        {
            return true;
        }

        var start = Position;
        SkipUntil('<');
        AddNode(TokenType.Text, start);
        return true;
    }

    private bool ConsumeComment()
    {
        return Consume(_startComment, _endComment, TokenType.Comment);
    }

    private bool ConsumeServerComment()
    {
        return Consume(_startServerComment, _endServerComment, TokenType.ServerComment);
    }

    private bool ConsumeDocType()
    {
        if (!Consume(_startDocType, true))
        {
            return false;
        }

        var offsetStart = Position;
        SkipUntil('>');
        AddNode(TokenType.DocType, offsetStart);
        Forward();
        return true;
    }

    private bool IsWebFormsElement()
    {
        if (Current != '<')
        {
            return false;
        }

        var close = NextTagClose(_offset);
        return close < _input.Length && _scanRunAt >= _offset;
    }

    /// <summary>
    /// The offset of the first '>' at or after <paramref name="offset"/>, or the input length
    /// when there is none, leaving <see cref="_scanRunAt"/> at the first "runat" in
    /// [<paramref name="offset"/>, close) or -1.
    /// </summary>
    private int NextTagClose(int offset)
    {
        if (offset >= _scanStart && offset < _scanClose)
        {
            if (_scanRunAt >= offset || _scanRunAt == -1)
            {
                return _scanClose;
            }

            // The memoized hit starts before this window; the window can still hold a later one.
            var sub = _input.Slice(offset, _scanClose - offset);
            var next = sub.IndexOf(_runAt, StringComparison.OrdinalIgnoreCase);
            _scanStart = offset;
            _scanRunAt = next == -1 ? -1 : offset + next;
            return _scanClose;
        }

        var slice = _input.Slice(offset);
        var index = slice.IndexOf('>');
        var close = index == -1 ? _input.Length : offset + index;
        var runAt = slice.Slice(0, close - offset).IndexOf(_runAt, StringComparison.OrdinalIgnoreCase);
        _scanStart = offset;
        _scanClose = close;
        _scanRunAt = runAt == -1 ? -1 : offset + runAt;
        return close;
    }

    private bool ConsumeWebFormsTag()
    {
        if (Current != '<')
        {
            return false;
        }

        return ConsumeElement(true) || ConsumeInline();
    }

    private bool ConsumeElement(bool requireRunAt = false)
    {
        var isServerTag = IsWebFormsElement();

        if (requireRunAt && !isServerTag)
        {
            return false;
        }

        var tagStart = Position;

        if (!Consume('<'))
        {
            return false;
        }

        var isClosingTag = Consume('/');
        var nameStart = Position;
        var start = nameStart;
        var name = ReadTagName();
        var namespaceName = default(TokenString);
        var hasNamespace = false;

        // Resolve the prefix before the balance bookkeeping below, so the stack and the
        // unexpected-closing-tag diagnostic both see "asp:PlaceHolder" instead of "asp".
        if (name.Value.Length > 0 && Current == ':')
        {
            hasNamespace = true;
            namespaceName = name;
            start = Position;
            Forward();
            name = ReadTagName();
        }

        var fullName = hasNamespace ? $"{namespaceName.Value}:{name.Value}" : name.Value;
        var isInvalid = fullName.Length == 0 ||
                        (!isServerTag && !isClosingTag && !hasNamespace && !ShouldParse(name.Value, isClosingTag));

        if (isInvalid || isClosingTag)
        {
            if (isInvalid || _tags.Count == 0 || fullName != _tags.Peek())
            {
                TrackHtmlTag(tagStart, new TokenString(fullName, new TokenRange(File, nameStart, Position)), isClosingTag);
                AddNode(TokenType.Text, tagStart, new TokenString(isClosingTag ? "</" : "<", new TokenRange(File, tagStart, nameStart)));
                AddNode(TokenType.Text, nameStart, new TokenString(fullName, new TokenRange(File, nameStart, Position)));
                return true;
            }

            _tags.Pop();
        }
        else
        {
            _tags.Push(fullName);
        }

        AddNode(isClosingTag ? TokenType.TagOpenSlash : TokenType.TagOpen, new TokenRange(File, tagStart, nameStart));

        if (hasNamespace)
        {
            AddNode(TokenType.ElementNamespace, namespaceName.Range, namespaceName);
        }

        AddNode(TokenType.ElementName, new TokenRange(File, start, Position), name);

        var isVoidTag = IsVoidTag(name.Value);

        var hasClosing = false;

        if (!isClosingTag)
        {
            SkipWhiteSpace();

            while (ConsumeWebFormsTag() || ReadAttribute())
            {
                SkipWhiteSpace();
            }

            SkipWhiteSpace();

            start = Position;

            if (isVoidTag)
            {
                hasClosing = isVoidTag;
            }

            if (Current == '/')
            {
                hasClosing = true;
                Forward();
                SkipWhiteSpace();
            }

            if (!hasClosing && (
                    name.Value.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                    name.Value.Equals("style", StringComparison.OrdinalIgnoreCase)
                ))
            {
                Consume('>');
                AddNode(TokenType.TagClose, start, default(TokenString));
                start = Position;

                while (SkipUntil('<'))
                {
                    var end = Position;
                    var index = _nodes.Count;

                    if (!isServerTag && ConsumeWebFormsTag())
                    {
                        var text = CreateString(start, end);
                        InsertNode(index, TokenType.Text, text.Range, text);
                        start = Position;
                        continue;
                    }

                    Forward();

                    if (!Peek('/'))
                    {
                        continue;
                    }

                    var range = new TokenRange(File, end, Position);

                    Forward();

                    SkipWhiteSpace();
                    var currentName = ReadTagName();

                    if (name.Value.Equals(currentName.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        var text = CreateString(start, end);
                        AddNode(TokenType.Text, text.Range, text);
                        AddNode(TokenType.TagOpenSlash, range);
                        AddNode(TokenType.ElementName, start, currentName);
                        SkipWhiteSpace();
                        _tags.Pop();
                        break;
                    }
                }
            }
        }

        if (Consume('>'))
        {
            if (hasClosing)
            {
                _tags.Pop();
            }

            AddNode(hasClosing ? TokenType.TagSlashClose : TokenType.TagClose, start, default(TokenString));
        }

        return true;
    }

    private static bool IsVoidTag(string name)
    {
        return name is "area" or "base" or "br" or "col" or "command" or "embed"
            or "hr" or "img" or "input" or "keygen" or "link" or "meta"
            or "param" or "source" or "track" or "wbr";
    }

    /// <summary>
    /// Balance bookkeeping for the tags that stay text. An opening tag is remembered; a closing
    /// tag takes the nearest opening tag of its name off the list, and one that finds no opening
    /// tag anywhere — not here and not on <see cref="_tags"/> — closes nothing and is reported.
    /// </summary>
    /// <remarks>
    /// Only closing tags are ever reported. HTML lets <c>&lt;li&gt;</c> or <c>&lt;td&gt;</c> stay
    /// open, so leftover opening tags mean nothing — which is also why a close matches any entry
    /// instead of unwinding to it. And when nothing is open at all, a fragment may be closing a
    /// tag that another file opened — the repeater header/footer idiom at file scale — so an
    /// empty list stays quiet too.
    /// </remarks>
    private void TrackHtmlTag(TokenPosition tagStart, TokenString name, bool isClosingTag)
    {
        if (name.Value.Length == 0 || tagStart.Offset < _htmlTagEnd)
        {
            return;
        }

        if (!isClosingTag)
        {
            var close = NextTagClose(_offset);
            _htmlTagEnd = close;

            var isSelfClosing = close > 0 && close < _input.Length && _input[close - 1] == '/';

            _sawOpenTag = true;

            if (!isSelfClosing && !IsVoidTag(name.Value))
            {
                _htmlTags.Add(name.Value);
            }

            return;
        }

        for (var i = _htmlTags.Count - 1; i >= 0; i--)
        {
            if (_htmlTags[i].Equals(name.Value, StringComparison.OrdinalIgnoreCase))
            {
                _htmlTags.RemoveAt(i);
                return;
            }
        }

        foreach (var tag in _tags)
        {
            if (tag.Equals(name.Value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (_htmlTags.Count == 0 && _tags.Count == 0)
        {
            // Only before the file has opened anything of its own, where a stray close is a
            // fragment finishing a wrapper another file opened.
            if (!_sawOpenTag)
            {
                return;
            }

            Diagnostics.Add(ReportedDiagnostic.Create(
                Descriptors.ClosingTagWithNothingOpen,
                new TokenRange(File, tagStart, Position),
                name.Value));
            return;
        }

        var expected = _htmlTags.Count > 0 ? _htmlTags[^1] : _tags.Peek();

        Diagnostics.Add(ReportedDiagnostic.Create(
            Descriptors.UnexpectedClosingTag,
            new TokenRange(File, tagStart, Position),
            expected,
            name.Value));
    }

    /// <summary>
    /// The HTML (and common SVG) element names. A lowercase tag with one of these names is
    /// literal output; anything else is a property tag like <c>&lt;columns&gt;</c> or
    /// <c>&lt;itemtemplate&gt;</c>, which ASP.NET matches case-insensitively.
    /// </summary>
    private static readonly HashSet<string> HtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "body", "head", "title", "base", "basefont", "meta", "noscript", "template", "slot",
        "address", "article", "aside", "footer", "header", "hgroup", "main", "nav", "section",
        "search", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "dd", "div", "dl", "dt", "figcaption", "figure", "hr", "li", "menu", "ol",
        "p", "pre", "ul",
        "a", "abbr", "acronym", "b", "bdi", "bdo", "big", "br", "center", "cite", "code", "data",
        "dfn", "em", "font", "i", "kbd", "mark", "q", "rp", "rt", "ruby", "s", "samp", "small",
        "span", "strike", "strong", "sub", "sup", "time", "tt", "u", "var", "wbr",
        "area", "audio", "map", "track", "video", "embed", "iframe", "object", "param", "picture",
        "source", "canvas", "svg", "math", "script", "style", "link", "img",
        "circle", "ellipse", "g", "line", "path", "polygon", "polyline", "rect", "text", "use",
        "defs", "clippath", "lineargradient", "radialgradient", "stop", "filter", "symbol",
        "marker", "mask", "pattern", "tspan",
        "table", "caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr",
        "button", "datalist", "fieldset", "form", "input", "label", "legend", "meter", "optgroup",
        "option", "output", "progress", "select", "textarea",
        "details", "dialog", "summary",
        "dir", "frame", "frameset", "noframes", "marquee", "applet", "nobr",
    };

    private bool ShouldParse(string name, bool isClosingTag)
    {
        var isSpecialTag = // Properties: <ItemTemplate>, <Columns> — in any casing
            char.IsUpper(name[0]) ||
            (!HtmlElements.Contains(name) && !name.Contains('-')) ||

            // Special elements
            name.Equals("html", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("body", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("head", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("title", StringComparison.OrdinalIgnoreCase) ||

            // CSP elements
            name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("link", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("img", StringComparison.OrdinalIgnoreCase);

        if (!isSpecialTag)
        {
            return false;
        }

        // It's possible there is a expression in the attribute list.
        // If this it the case, we should not parse the tag since we need to render the expression.
        var last = NextTagClose(_offset);

        if (last >= _input.Length)
        {
            return true;
        }

        // Check for '<%'
        if (_input.Slice(_offset, last - _offset).Contains(_startStatement, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool ConsumeInline()
    {
        var start = Position;

        if (!Consume(_startStatement))
        {
            return false;
        }

        var type = TokenType.Statement;
        var end = _end;

        if (Peek('-') && Peek('-', 1))
        {
            Forward(2);
            type = TokenType.Comment;
            end = _endServerComment;
        }
        else if (Consume(':'))
        {
            type = TokenType.EncodeExpression;
        }
        else if (Consume('='))
        {
            type = TokenType.Expression;
        }
        else if (Consume('#'))
        {
            type = TokenType.EvalExpression;
        }
        else if (Consume('$'))
        {
            return ConsumeExpressionBuilder(start);
        }
        else if (Consume('@'))
        {
            AddNode(TokenType.StartDirective, start);
            SkipWhiteSpace();

            while (!Consume(_end, TokenType.EndDirective) && ReadAttribute())
            {
                SkipWhiteSpace();
            }

            return true;
        }

        return ConsumeUntil(_startStatement, end, type, start);
    }

    /// <summary>
    /// Reads <c>&lt;%$ Prefix: Argument %&gt;</c> as two tokens. The argument gets its own token
    /// because its range has to be exact: a builder may span a line, and slicing one token
    /// afterwards can only add to a column.
    /// </summary>
    private bool ConsumeExpressionBuilder(TokenPosition start)
    {
        var prefix = ReadExpressionBuilderPart(stopAtColon: true);

        var argument = Consume(':')
            ? ReadExpressionBuilderPart(stopAtColon: false)
            : CreateString(Position, Position);

        // ReadAttribute resumes its value scan from wherever the offset lands, so stopping short
        // of the closing %> folds the tail of the builder into the attribute value.
        if (Peek(_end))
        {
            Forward(_end.Length);
        }

        var range = new TokenRange(File, start, Position);

        AddNode(TokenType.ExpressionBuilderPrefix, range, prefix);
        AddNode(TokenType.ExpressionBuilderArgument, range, argument);
        return true;
    }

    private TokenString ReadExpressionBuilderPart(bool stopAtColon)
    {
        SkipWhiteSpace();

        var start = Position;
        var end = Position;

        while (_offset < _input.Length && !Peek(_end))
        {
            if (stopAtColon && Peek(':'))
            {
                break;
            }

            // Only spaces and tabs are dropped from the end: a newline would move the end onto
            // another line, and the position is tracked rather than recomputed.
            var isPadding = Current is ' ' or '\t';

            Forward();

            if (!isPadding)
            {
                end = Position;
            }
        }

        return CreateString(start, end);
    }

    private bool ConsumeInlineSkipWhiteSpace()
    {
        var result = false;
        SkipWhiteSpace();

        if (!ConsumeInline())
        {
            return false;
        }

        do
        {
            SkipWhiteSpace();
        } while (ConsumeInline());

        return result;
    }

    public bool ReadAttribute()
    {
        // https://www.w3.org/TR/2011/WD-html5-20110525/syntax.html#attributes-0
        ConsumeInlineSkipWhiteSpace();

        if (IsAttributeSeparator(Current))
        {
            return false;
        }

        var start = Position;

        while (_offset < _input.Length && !IsAttributeSeparator(Current))
        {
            Forward();
        }

        AddNode(TokenType.Attribute, start);

        ConsumeInlineSkipWhiteSpace();

        if (!Peek('='))
        {
            return true;
        }

        Forward();

        ConsumeInlineSkipWhiteSpace();
        var token = Current;

        if (token is '"' or '\'')
        {
            Forward();
            start = Position;

            for (; _offset < _input.Length; Forward())
            {
                if (Peek(_startStatement) || IsWebFormsElement())
                {
                    if (_offset > start.Offset)
                    {
                        AddNode(TokenType.AttributeValue, start);
                    }

                    ConsumeWebFormsTag();
                    start = Position;
                }

                if (_offset >= _input.Length)
                {
                    break;
                }

                var current = _input[_offset];

                if (current == token)
                {
                    break;
                }
            }

            if (_offset > start.Offset)
            {
                AddNode(TokenType.AttributeValue, start);
            }

            Forward();
            return true;
        }

        start = Position;

        while (_offset < _input.Length && !IsInvalidAttributeValueCharacter(Current))
        {
            ConsumeWebFormsTag();
            Forward();
        }

        AddNode(TokenType.AttributeValue, start);
        return true;
    }

    public TokenString ReadTagName()
    {
        var start = Position;

        while (_offset < _input.Length && IsTagCharacter(Current))
        {
            Forward();
        }

        return CreateString(start, Position);
    }
        
    private void AddNode(TokenType type, TokenPosition start)
    {
        AddNode(type, start, Position);
    }

    private void AddNode(TokenType type, TokenPosition start, TokenPosition end)
    {
        AddNode(type, new TokenRange(File, start, end), CreateString(start, end));
    }

    private void TrackText(TokenRange range, TokenString value)
    {
        if (_isStart)
        {
            if (string.IsNullOrWhiteSpace(value.Value))
            {
                return;
            }

            value = new TokenString(value.Value.TrimStart(), value.Range);
            _isStart = false;
        }

        if (_textBuilder.Length == 0)
        {
            _textStart = range.Start;
        }

        _textEnd = range.End;
        _textBuilder.Append(value.Value);
    }

    private bool AddText()
    {
        if (_textBuilder.Length == 0)
        {
            return false;
        }

        var text = new TokenString(_textBuilder.ToString(), new TokenRange(File, _textStart, _textEnd));
        _textBuilder.Clear();
        _nodes.Add(new Token(TokenType.Text, text.Range, text));
        return true;
    }

    private void InsertNode(int index, TokenType type, TokenRange range, TokenString value = default)
    {
        _nodes.Insert(index, new Token(type, range, value));
    }

    private void AddNode(TokenType type, TokenRange range, TokenString value = default)
    {
        if (_isStart && type is TokenType.Text or TokenType.Comment or TokenType.TagOpen or TokenType.Expression or TokenType.Statement or TokenType.EncodeExpression or TokenType.EvalExpression or TokenType.DocType)
        {
            if (type is TokenType.Text)
            {
                if (string.IsNullOrWhiteSpace(value.Value))
                {
                    return;
                }

                value = new TokenString(value.Value.TrimStart(), value.Range);
            }

            _isStart = false;
        }

        if (type == TokenType.Text)
        {
            TrackText(range, value);
            return;
        }

        AddText();
        _nodes.Add(new Token(type, range, value));
    }

    private void AddNode(TokenType type, TokenPosition start, TokenString value)
    {
        if (type == TokenType.Text)
        {
            TrackText(value.Range with { Start = start }, value);
            return;
        }

        AddText();
        _nodes.Add(new Token(type, new TokenRange(File, start, Position), value));
    }

    private TokenString CreateString(TokenPosition start, TokenPosition end)
    {
        // Clamped rather than trusted. Every token in the file is cut here, so this is the one
        // place where a position that ran off the end turns into an exception out of the parse —
        // and an exception out of the parse costs the file every markup feature at once, plus its
        // code-behind's C# lenses. A token that reports a slightly short span is a far better
        // outcome than a page with no hover, no folding and no diagnostics.
        int from = Math.Clamp(start.Offset, 0, _input.Length);
        int to = Math.Clamp(end.Offset, from, _input.Length);

        return new TokenString(_input.Slice(from, to - from).ToString(), new TokenRange(File, start, end));
    }

    public void SkipWhiteSpace()
    {
        while (_offset < _input.Length && IsSpaceCharacter(Current))
        {
            Forward();
        }
    }

    private static bool IsTagCharacter(char c)
    {
        // https://www.w3.org/TR/2011/WD-html5-20110525/syntax.html#syntax-tag-name
        return c
                is >= (char)0x0030 and <= (char)0x0039 // U+0030 DIGIT ZERO (0) to U+0039 DIGIT NINE (9)
                or >= (char)0x0061 and <= (char)0x007A // U+0061 LATIN SMALL LETTER A to U+007A LATIN SMALL LETTER Z
                or >= (char)0x0041 and <= (char)0x005A // U+0041 LATIN CAPITAL LETTER A to U+005A LATIN CAPITAL LETTER Z
            ;
    }

    private static bool IsSpaceCharacter(char c)
    {
        // https://www.w3.org/TR/2011/WD-html5-20110525/common-microsyntaxes.html#space-character
        return c
                is (char)0x0020 // U+0020 SPACE
                or (char)0x0009 // U+0009 CHARACTER TABULATION (tab)
                or (char)0x000A // U+000A LINE FEED (LF)
                or (char)0x000C // U+000C FORM FEED (FF)
                or (char)0x000D // U+000D CARRIAGE RETURN (CR)
            ;
    }

    private static bool IsAttributeSeparator(char c)
    {
        // https://www.w3.org/TR/2011/WD-html5-20110525/syntax.html#attributes-0
        return IsSpaceCharacter(c) || c
                is (char)0x0000 // U+0000 NULL
                or (char)0x0022 // U+0022 QUOTATION MARK (")
                or (char)0x0027 // U+0027 APOSTROPHE (')
                or (char)0x003E // U+003E GREATER-THAN SIGN (>)
                or (char)0x002F // U+002F SOLIDUS (/)
                or (char)0x003D // U+003D EQUALS SIGN (=)
            ;
    }

    private bool IsInvalidAttributeValueCharacter(char c)
    {
        // https://www.w3.org/TR/2011/WD-html5-20110525/syntax.html#attributes-0
        return IsAttributeSeparator(c) || c
                is (char)0x003C // U+003C LESS-THAN SIGN characters (<)
                or (char)0x003E // U+003E GREATER-THAN SIGN characters (>)
                or (char)0x0060 // U+0060 GRAVE ACCENT characters (`)
            ;
    }

    private bool ConsumeUntil(ReadOnlySpan<char> start, ReadOnlySpan<char> end, TokenType? type, TokenPosition offsetStart)
    {
        var textStart = Position;

        if (!SkipUntil(start, end))
        {
            if (type.HasValue)
            {
                AddNode(type.Value, new TokenRange(File, offsetStart, Position), CreateString(textStart, Position));
            }

            return true;
        }

        if (type.HasValue)
        {
            AddNode(type.Value, new TokenRange(File, offsetStart, Position), CreateString(textStart, Position));
        }

        Forward(end.Length);
        return true;
    }

    private bool Consume(ReadOnlySpan<char> data, ReadOnlySpan<char> end, TokenType? type)
    {
        var start = Position;

        if (!Consume(data))
        {
            return false;
        }

        return ConsumeUntil(data, end, type, start);
    }

    private bool Consume(ReadOnlySpan<char> data, TokenType type, bool ignoreCase = false)
    {
        var start = Position;
        if (!Consume(data, ignoreCase))
        {
            return false;
        }
        AddNode(type, start);
        return true;
    }

    private bool Consume(ReadOnlySpan<char> data, bool ignoreCase = false)
    {
        if (!Peek(data, ignoreCase))
        {
            return false;
        }

        Forward(data.Length);
        return true;
    }

    private bool Consume(char c)
    {
        if (!Peek(c))
        {
            return false;
        }

        Forward();
        return true;

    }

    private bool Peek(char c)
    {
        return Current == c;
    }

    private bool Peek(char c, int offset)
    {
        var index = _offset + offset;
        return index < _input.Length && _input[index] == c;
    }

    private bool Peek(ReadOnlySpan<char> data, bool ignoreCase = false)
    {
        if (_input.Length - _offset < data.Length)
        {
            return false;
        }

        var left = _input.Slice(_offset, data.Length);

        return ignoreCase
            ? left.Equals(data, StringComparison.OrdinalIgnoreCase)
            : left.SequenceEqual(data);
    }

    private bool SkipUntil(ReadOnlySpan<char> start, ReadOnlySpan<char> end)
    {
        var depth = 1;

        for (; _offset < _input.Length; Forward())
        {
            var current = _input[_offset];

            if (current == start[0] && Peek(start))
            {
                depth++;
            }

            if (current == end[0] && Peek(end))
            {
                if (--depth == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool SkipUntil(char untilChar, bool breakOnNewLine = false, bool allowInline = false)
    {
        if (allowInline)
        {
            ConsumeWebFormsTag();
        }

        for (; _offset < _input.Length; Forward())
        {
            if (allowInline)
            {
                ConsumeWebFormsTag();
            }

            if (_offset >= _input.Length)
            {
                break;
            }
            
            var current = _input[_offset];

            if (breakOnNewLine && current is '\n' or '\r')
            {
                return false;
            }

            if (current == '\\')
            {
                // Skip what the backslash escapes — but only if there is one. `continue` runs the
                // loop's own Forward() as well, so this branch advances twice against a single
                // bounds check, and a file whose last character is a backslash ends with the
                // offset one past the end. The token built from that position then slices past the
                // buffer and throws out of the middle of parsing, which costs the file every
                // markup feature it has.
                Forward();

                if (_offset >= _input.Length)
                {
                    break;
                }

                continue;
            }

            if (current == untilChar)
            {
                return true;
            }
        }

        return false;
    }
}
