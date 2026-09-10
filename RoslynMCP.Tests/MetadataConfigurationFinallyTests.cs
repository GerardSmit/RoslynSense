using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services.MetadataConfiguration;
using Xunit;

namespace RoslynMCP.Tests;

public class MetadataConfigurationFinallyTests
{
    [Theory]
    [InlineData("settings = new NameValueCollection();")]
    [InlineData("Replace(ref settings);")]
    public void FinallyReadsKeepProvenanceButOverwrittenContinuationDoesNot(string mutation)
    {
        string source = $$"""
            using System.Collections.Specialized;
            namespace System.Configuration
            {
                public static class ConfigurationManager
                {
                    public static NameValueCollection AppSettings => new NameValueCollection();
                }
            }
            public static class Consumer
            {
                public static string Read()
                {
                    var settings = System.Configuration.ConfigurationManager.AppSettings;
                    try { Consume(null); }
                    finally
                    {
                        Consume(settings["InsideFinally"]);
                        {{mutation}}
                    }
                    return settings["NotConfiguration"];
                }
                static void Replace(ref NameValueCollection settings) => settings = new NameValueCollection();
                static void Consume(string value) { }
            }
            """;
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create("FinallyMutation",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Collections.Specialized.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        stream.Position = 0;
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var reads = MetadataConfigurationFlow.Scan(pe, md, new MetadataConstantStrings(pe, md)).ToArray();

        Assert.Contains(reads, read => read.Literal == "InsideFinally");
        Assert.DoesNotContain(reads, read => read.Literal == "NotConfiguration");
    }
}
