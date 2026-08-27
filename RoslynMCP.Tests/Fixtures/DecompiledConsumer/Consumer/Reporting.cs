using System.Text;

namespace DecompiledConsumer;

/// <summary>
/// The interface a decompiled copy of this assembly declares too, for the gestures whose answer
/// is "what implements this".
/// </summary>
public interface IReportFormatter
{
    string FormatReport(int value);
}

/// <summary>The one implementation, and the one call into a framework member.</summary>
public sealed class PlainReportFormatter : IReportFormatter
{
    public string FormatReport(int value) => Build(value.ToString());

    private static string Build(string value)
    {
        var builder = new StringBuilder();
        builder.Append(value);
        return builder.ToString();
    }
}
