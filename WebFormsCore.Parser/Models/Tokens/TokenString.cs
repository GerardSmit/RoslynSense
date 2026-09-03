using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;

namespace WebFormsCore.Models;

public enum AttributeValueKind
{
    Literal,
    Code,
    ExpressionBuilder
}

public record struct AttributeValue(AttributeValueKind Kind, TokenString Token)
{
    /// <summary>The pre-kind shape, kept so every existing construction site compiles untouched.</summary>
    public AttributeValue(bool isCode, TokenString token)
        : this(isCode ? AttributeValueKind.Code : AttributeValueKind.Literal, token)
    {
    }

    public override string ToString()
    {
        return Token.ToString();
    }

    /// <summary>The expression builder prefix, so <see cref="Token"/> stays the argument and its
    /// range is the exact span of the key.</summary>
    public TokenString Prefix { get; init; }

    public bool IsCode => Kind == AttributeValueKind.Code;

    public string Value => Token.Value;

    public TokenRange Range => Token.Range;

    public string CodeString => IsCode ? Value : Token.CodeString;

    public string VbCodeString => IsCode ? Value : Token.VbCodeString;

    public static implicit operator string(AttributeValue attributeValue)
    {
        return attributeValue.Value;
    }
}

[DebuggerDisplay("{Value} [{Range}]")]
public readonly struct TokenString : IEquatable<TokenString>
{
    private readonly string _value;

    public TokenString(string value, TokenRange range)
    {
        _value = value;
        Range = range;
    }
    
    public string Value => _value ?? "";

    private string CodeStringBase => Value.Replace("\"", "\"\"");

    public string CodeString => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(Value)).ToFullString();

    public string VbCodeString => @$"""{CodeStringBase.Replace("\r\n", "\" + vbCrLf + \"").Replace("\n", "\" + vbLf + \"").Replace("\r", "\" + vbCr + \"")}""";

    public TokenRange Range { get; }

    public override string ToString()
    {
        return Value;
    }

    public static implicit operator string(TokenString tokenString)
    {
        return tokenString.Value;
    }

    public static implicit operator TokenString(string nodeString)
    {
        return new TokenString(nodeString, default);
    }

    public bool Equals(TokenString other)
    {
        return Value == other.Value;
    }

    public override bool Equals(object? obj)
    {
        return obj is TokenString other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }

    public static bool operator ==(TokenString left, TokenString right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TokenString left, TokenString right)
    {
        return !left.Equals(right);
    }
}
