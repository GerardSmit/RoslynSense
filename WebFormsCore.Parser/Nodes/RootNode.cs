using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WebFormsCore.Models;
using WebFormsCore.SourceGenerator.Models;
using Lexer = WebFormsCore.Language.Lexer;
using Parser = WebFormsCore.Language.Parser;
using TokenType = WebFormsCore.Models.TokenType;

namespace WebFormsCore.Nodes;

public record IncludeFile(string Path, string Hash);

public class RootNode : ContainerNode
{
    private INamedTypeSymbol? _inherits;
    private string? _path;
    private string? _directory;

    public RootNode()
        : base(NodeType.Root)
    {
    }

    public List<DirectiveNode> Directives { get; set; } = new();

    public List<TemplateNode> Templates { get; set; } = new();

    public List<ControlId> Ids { get; set; } = new();

    public List<ContainerNode> RenderMethods { get; set; } = new();

    public List<TokenString> InlineScripts { get; set; } = new();

    public INamedTypeSymbol? Inherits
    {
        get => _inherits;
        set => _inherits = value;
    }

    public bool AddFields { get; set; }

    public string? ClassName { get; set; }

    public string? AssemblyName { get; set; }

    public string? Path
    {
        get => _path;
        set
        {
            _path = value;
            _directory = null;
        }
    }

    public string? RelativePath { get; set; }

    private static readonly char[] DirectorySeparators = { '/', '\\' };

    public string? Directory
    {
        get
        {
            if (_directory != null || Path == null)
            {
                return _directory;
            }

            var index = Path.LastIndexOfAny(DirectorySeparators);

            _directory = index == -1
                ? string.Empty
                : Path.Substring(0, index);

            return _directory;
        }
    }

    public string? Hash { get; set; }

    public string? VbNamespace { get; set; }

    public string? Namespace { get; set; }

    public Language Language { get; set; } = Language.CSharp;

    public List<string> Namespaces { get; set; } = new();

    public List<IncludeFile> IncludeFiles { get; set; } = new();

    public List<TokenString> ScriptBlocks { get; set; } = new();

    [return: NotNullIfNotNull("text")]
    public static RootNode? Parse(
        out ImmutableArray<ReportedDiagnostic> diagnostics,
        Compilation compilation,
        string fullPath,
        string? text,
        string? rootNamespace = null,
        IEnumerable<KeyValuePair<string, string>>? namespaces = null,
        bool addFields = true,
        string? relativePath = null,
        string? rootDirectory = null,
        bool generateHash = true)
    {
        if (text == null)
        {
            diagnostics = ImmutableArray<ReportedDiagnostic>.Empty;
            return null;
        }

        var lexer = new Lexer(fullPath, text.AsSpan());
        var parser = new Parser(compilation, rootNamespace, addFields, rootDirectory);

        if (namespaces != null)
        {
            foreach (var ns in namespaces)
            {
                parser.AddNamespace(ns.Key, ns.Value);
            }
        }

        parser.Parse(ref lexer);

        diagnostics = parser.Diagnostics.ToImmutableArray();

        if (relativePath == null)
        {
            if (rootDirectory != null && fullPath.StartsWith(rootDirectory))
            {
                relativePath = NormalizePath(fullPath.Substring(rootDirectory.Length));
            }
            else
            {
                relativePath = fullPath;
            }
        }

        parser.Root.Path = fullPath;
        parser.Root.RelativePath = relativePath;
        parser.Root.ClassName = Regex.Replace(relativePath, "[^a-zA-Z0-9_]+", "_");
        parser.Root.AssemblyName = Regex.Replace(compilation.AssemblyName ?? "", "[^a-zA-Z0-9_]+", "_");
        if (generateHash)
        {
            parser.Root.Hash = GenerateHash(text);
        }

        return parser.Root;
    }

    public List<Diagnostic> Diagnostics { get; } = new();

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var span = path.AsSpan();

#if NET
        var hasInvalidPathSeparator = span.Contains('\\');
#else
        var hasInvalidPathSeparator = span.IndexOf('\\') != -1;
#endif

        if (!hasInvalidPathSeparator && span[0] != '/')
        {
            return path;
        }

        Span<char> buffer = stackalloc char[path.Length];

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];

            buffer[i] = c switch
            {
                '\\' => '/',
                _ => c
            };
        }

        if (buffer[0] == '/')
        {
            return buffer.Slice(1).ToString();
        }

        return buffer.ToString();
    }

    public static Language DetectLanguage(string text)
    {
        var lexer = new Lexer("Language.aspx", text.AsSpan());
        var step = 0;

        while (lexer.Next() is {} token)
        {
            switch (step)
            {
                case 0 when token.Type == TokenType.StartDirective:
                    step++;
                    break;
                case 1 when token.Type == TokenType.Attribute && token.Text.Value.Equals("language", StringComparison.OrdinalIgnoreCase):
                    return lexer.Next()?.Text.Value.ToLowerInvariant() switch
                    {
                        "vb" => Language.VisualBasic,
                        "c#" => Language.CSharp,
                        _ => Language.CSharp
                    };
            }
        }

        return Language.CSharp;
    }

    public static string? DetectInherits(string text)
    {
        var lexer = new Lexer("Language.aspx", text.AsSpan());
        var step = 0;

        while (lexer.Next() is {} token)
        {
            switch (step)
            {
                case 0 when token.Type == TokenType.StartDirective:
                    step++;
                    break;
                case 1 when token.Type == TokenType.Attribute && token.Text.Value.Equals("inherits", StringComparison.OrdinalIgnoreCase):
                    return lexer.Next()?.Text.Value;
            }
        }

        return null;
    }

    public static string GenerateHash(string text)
    {
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"));
        var hashBytes = md5.ComputeHash(inputBytes);
        var sb = new StringBuilder();

        foreach (var c in hashBytes)
        {
            sb.Append(c.ToString("X2"));
        }

        return sb.ToString();
    }

    public static ImmutableArray<KeyValuePair<string, string>> GetNamespaces(string? webConfigText)
    {
        if (string.IsNullOrEmpty(webConfigText))
        {
            return default;
        }

        var namespaces = new List<KeyValuePair<string, string>>();

        try
        {
            var controls = XElement.Parse(webConfigText)
                .Descendants("system.web").FirstOrDefault()
                ?.Descendants("pages").FirstOrDefault()
                ?.Descendants("controls").FirstOrDefault();

            if (controls != null)
            {
                foreach (var add in controls.Descendants("add"))
                {
                    var tagPrefix = add.Attribute("tagPrefix")?.Value;
                    var namespaceName = add.Attribute("namespace")?.Value;

                    if (tagPrefix != null && namespaceName != null)
                    {
                        namespaces.Add(new KeyValuePair<string, string>(tagPrefix, namespaceName));
                    }
                }
            }
        }
        catch (Exception)
        {
            // TODO: Diagnostic
        }

        return namespaces.ToImmutableArray();
    }
}
