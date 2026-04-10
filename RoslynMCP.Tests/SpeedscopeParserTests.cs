using System.Text.Json;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class SpeedscopeParserTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private string WriteTempSpeedscope(object content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.speedscope.json");
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(content));
        return path;
    }

    [Fact]
    public void ParsesBasicProfile()
    {
        // Build a minimal speedscope file with known self/total times
        // Stack layout per sample:
        //   Sample 0: [A, B, C]  weight=10  → self=C, total=A,B,C
        //   Sample 1: [A, B]     weight=5   → self=B, total=A,B
        //   Sample 2: [A, C]     weight=3   → self=C, total=A,C
        //   Sample 3: [D]        weight=2   → self=D, total=D
        var speedscope = new
        {
            version = "0.0.1",
            shared = new
            {
                frames = new object[]
                {
                    new { name = "Namespace.ClassA.MethodA()" },
                    new { name = "Namespace.ClassB.MethodB(int)" },
                    new { name = "Namespace.ClassC.MethodC(string, int)" },
                    new { name = "Other.ClassD.MethodD()" }
                }
            },
            profiles = new object[]
            {
                new
                {
                    type = "sampled",
                    name = "CPU",
                    unit = "milliseconds",
                    startValue = 0,
                    endValue = 20,
                    samples = new int[][] { [0, 1, 2], [0, 1], [0, 2], [3] },
                    weights = new double[] { 10, 5, 3, 2 }
                }
            }
        };

        var path = WriteTempSpeedscope(speedscope);
        var result = SpeedscopeParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        Assert.Equal(20.0, result.TotalDurationMs);
        Assert.Equal(4, result.TotalSamples);
        Assert.Equal(4, result.HotMethods.Count);

        // C should be hottest by self-time: 10 + 3 = 13ms
        var methodC = result.HotMethods[0];
        Assert.Equal("ClassC.MethodC(string, int)", methodC.Name);
        Assert.Equal(13.0, methodC.SelfTimeMs);
        Assert.Equal(13.0, methodC.TotalTimeMs); // C is always leaf
        Assert.Equal(65.0, methodC.SelfPercent, precision: 1);

        // B is next: self=5, total=15 (present in samples 0 and 1)
        var methodB = result.HotMethods[1];
        Assert.Equal("ClassB.MethodB(int)", methodB.Name);
        Assert.Equal(5.0, methodB.SelfTimeMs);
        Assert.Equal(15.0, methodB.TotalTimeMs);

        // D: self=2, total=2
        var methodD = result.HotMethods[2];
        Assert.Equal("ClassD.MethodD()", methodD.Name);
        Assert.Equal(2.0, methodD.SelfTimeMs);
        Assert.Equal(2.0, methodD.TotalTimeMs);

        // A: self=0, total=18 (present in samples 0, 1, 2)
        var methodA = result.HotMethods[3];
        Assert.Equal("ClassA.MethodA()", methodA.Name);
        Assert.Equal(0.0, methodA.SelfTimeMs);
        Assert.Equal(18.0, methodA.TotalTimeMs);
    }

    [Fact]
    public void RespectsMaxResults()
    {
        var speedscope = new
        {
            version = "0.0.1",
            shared = new
            {
                frames = new object[]
                {
                    new { name = "A.A()" },
                    new { name = "B.B()" },
                    new { name = "C.C()" },
                    new { name = "D.D()" },
                    new { name = "E.E()" }
                }
            },
            profiles = new object[]
            {
                new
                {
                    type = "sampled",
                    name = "CPU",
                    unit = "milliseconds",
                    startValue = 0,
                    endValue = 50,
                    samples = new int[][] { [0], [1], [2], [3], [4] },
                    weights = new double[] { 10, 8, 6, 4, 2 }
                }
            }
        };

        var path = WriteTempSpeedscope(speedscope);
        var result = SpeedscopeParser.Parse(path, maxResults: 3);

        Assert.Null(result.Error);
        Assert.Equal(3, result.HotMethods.Count);
        // Top 3 by self-time should be A(10), B(8), C(6)
        Assert.Equal("A.A()", result.HotMethods[0].Name);
        Assert.Equal("B.B()", result.HotMethods[1].Name);
        Assert.Equal("C.C()", result.HotMethods[2].Name);
    }

    [Fact]
    public void HandlesEmptySamples()
    {
        var speedscope = new
        {
            version = "0.0.1",
            shared = new { frames = new object[] { new { name = "A" } } },
            profiles = new object[]
            {
                new
                {
                    type = "sampled",
                    name = "CPU",
                    unit = "milliseconds",
                    startValue = 0,
                    endValue = 0,
                    samples = Array.Empty<int[]>(),
                    weights = Array.Empty<double>()
                }
            }
        };

        var path = WriteTempSpeedscope(speedscope);
        var result = SpeedscopeParser.Parse(path, maxResults: 10);

        Assert.NotNull(result.Error);
        Assert.Contains("no samples", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandlesRecursion()
    {
        // A calls itself recursively: [A, A, A] — should not double-count total-time
        var speedscope = new
        {
            version = "0.0.1",
            shared = new { frames = new object[] { new { name = "Ns.Cls.Recursive()" } } },
            profiles = new object[]
            {
                new
                {
                    type = "sampled",
                    name = "CPU",
                    unit = "milliseconds",
                    startValue = 0,
                    endValue = 10,
                    samples = new int[][] { [0, 0, 0] },
                    weights = new double[] { 10 }
                }
            }
        };

        var path = WriteTempSpeedscope(speedscope);
        var result = SpeedscopeParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        Assert.Single(result.HotMethods);
        Assert.Equal(10.0, result.HotMethods[0].SelfTimeMs);
        Assert.Equal(10.0, result.HotMethods[0].TotalTimeMs); // not 30!
    }

    [Fact]
    public void SplitMethodNameHandlesSimpleName()
    {
        var (name, module) = SpeedscopeParser.SplitMethodName("System.String.Concat(string, string)");
        Assert.Equal("String.Concat(string, string)", name);
        Assert.Equal("System", module);
    }

    [Fact]
    public void SplitMethodNameHandlesDeepNamespace()
    {
        var (name, module) = SpeedscopeParser.SplitMethodName("Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax.Accept()");
        Assert.Equal("CompilationUnitSyntax.Accept()", name);
        Assert.Equal("Microsoft.CodeAnalysis.CSharp.Syntax", module);
    }

    [Fact]
    public void SplitMethodNameHandlesNoNamespace()
    {
        var (name, module) = SpeedscopeParser.SplitMethodName("Calculator.Add(int, int)");
        Assert.Equal("Calculator.Add(int, int)", name);
        Assert.Equal("", module);
    }

    [Fact]
    public void SplitMethodNameHandlesPlainName()
    {
        var (name, module) = SpeedscopeParser.SplitMethodName("Main");
        Assert.Equal("Main", name);
        Assert.Equal("", module);
    }

    [Fact]
    public void HandlesNoSampledProfile()
    {
        var speedscope = new
        {
            version = "0.0.1",
            shared = new { frames = new object[] { new { name = "A" } } },
            profiles = new object[]
            {
                new
                {
                    type = "evented",
                    name = "GC",
                    unit = "bytes",
                    startValue = 0,
                    endValue = 0,
                    events = Array.Empty<object>()
                }
            }
        };

        var path = WriteTempSpeedscope(speedscope);
        var result = SpeedscopeParser.Parse(path, maxResults: 10);

        Assert.NotNull(result.Error);
        Assert.Contains("No sampled CPU profile", result.Error);
    }

    [Fact]
    public void HandlesInvalidJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        File.WriteAllText(path, "this is not json {{{");

        var result = SpeedscopeParser.Parse(path, maxResults: 10);

        Assert.NotNull(result.Error);
        Assert.Contains("parse", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
