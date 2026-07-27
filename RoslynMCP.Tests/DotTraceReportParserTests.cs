using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class DotTraceReportParserTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private string WriteReport(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dottrace-report-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void WhenInstancesHaveOwnTimeThenSelfTimeAggregatesPerLeaf()
    {
        // Two call-tree nodes for Repository.Query under different callers, each with self-time.
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="Data.Repository.Query" TotalTime="50" OwnTime="50" Samples="5" Instances="2">
                <Instance CallStack="App.Program.Main/App.ServiceA.Process/Data.Repository.Query" TotalTime="30" OwnTime="30" Samples="3" />
                <Instance CallStack="App.Program.Main/App.ServiceB.Handle/Data.Repository.Query" TotalTime="20" OwnTime="20" Samples="2" />
              </Function>
              <Function Id="0x2" FQN="Infra.Cache.Get" TotalTime="25" OwnTime="25" Samples="2" Instances="1">
                <Instance CallStack="App.Program.Main/App.ServiceB.Handle/Infra.Cache.Get" TotalTime="25" OwnTime="25" Samples="2" />
              </Function>
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        var query = result.HotMethods.Single(m => m.FullName == "Data.Repository.Query");
        Assert.Equal(50, query.SelfTimeMs);
        Assert.Equal(50, query.TotalTimeMs);
    }

    [Fact]
    public void WhenCallerHasNoOwnTimeThenItStillGetsTotalTimeFromCallees()
    {
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="Data.Repository.Query" TotalTime="45" OwnTime="45" Samples="5" Instances="2">
                <Instance CallStack="App.Program.Main/App.ServiceA.Process/Data.Repository.Query" TotalTime="30" OwnTime="30" Samples="3" />
                <Instance CallStack="App.Program.Main/App.ServiceB.Handle/Data.Repository.Query" TotalTime="15" OwnTime="15" Samples="2" />
              </Function>
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        var main = result.HotMethods.Single(m => m.FullName == "App.Program.Main");
        Assert.Equal(0, main.SelfTimeMs);
        Assert.Equal(45, main.TotalTimeMs);
        Assert.Equal(100, main.TotalPercent, precision: 3);

        var serviceA = result.HotMethods.Single(m => m.FullName == "App.ServiceA.Process");
        Assert.Equal(30, serviceA.TotalTimeMs);
    }

    [Fact]
    public void WhenInstanceHasZeroOwnTimeThenItIsNotEmittedAsSample()
    {
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="App.ServiceA.Process" TotalTime="30" OwnTime="0" Samples="3" Instances="1">
                <Instance CallStack="App.Program.Main/App.ServiceA.Process" TotalTime="30" OwnTime="0" Samples="3" />
              </Function>
              <Function Id="0x2" FQN="Data.Repository.Query" TotalTime="30" OwnTime="30" Samples="3" Instances="1">
                <Instance CallStack="App.Program.Main/App.ServiceA.Process/Data.Repository.Query" TotalTime="30" OwnTime="30" Samples="3" />
              </Function>
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        Assert.Equal(1, result.TotalSamples);
        Assert.Equal(30, result.TotalDurationMs);
    }

    [Fact]
    public void WhenFunctionHasNoInstancesThenOwnTimeBecomesSingleFrameSample()
    {
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="Data.Repository.Query" TotalTime="40" OwnTime="40" Samples="4" Instances="0" />
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        var query = result.HotMethods.Single(m => m.FullName == "Data.Repository.Query");
        Assert.Equal(40, query.SelfTimeMs);
    }

    [Fact]
    public void WhenTimesUseDecimalPointThenTheyParseInvariantOfLocale()
    {
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="Data.Repository.Query" TotalTime="6.5" OwnTime="6.5" Samples="1" Instances="1">
                <Instance CallStack="App.Program.Main/Data.Repository.Query" TotalTime="6.5" OwnTime="6.5" Samples="1" />
              </Function>
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.Null(result.Error);
        Assert.Equal(6.5, result.HotMethods.Single(m => m.FullName == "Data.Repository.Query").SelfTimeMs);
    }

    [Fact]
    public void WhenReportIsEmptyThenErrorExplainsIdleApplication()
    {
        var path = WriteReport("<Report></Report>");

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.NotNull(result.Error);
        Assert.Contains("idle", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenXmlIsMalformedThenParseErrorIsReturned()
    {
        var path = WriteReport("<Report><Function></Report>");

        var result = DotTraceReportParser.Parse(path, maxResults: 10);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void WhenParsedThenSessionStoreCanComputeCallersFromReconstructedStacks()
    {
        var path = WriteReport(
            """
            <Report>
              <Function Id="0x1" FQN="Data.Repository.Query" TotalTime="45" OwnTime="45" Samples="5" Instances="2">
                <Instance CallStack="App.Program.Main/App.ServiceA.Process/Data.Repository.Query" TotalTime="30" OwnTime="30" Samples="3" />
                <Instance CallStack="App.Program.Main/App.ServiceB.Handle/Data.Repository.Query" TotalTime="15" OwnTime="15" Samples="2" />
              </Function>
            </Report>
            """);

        var result = DotTraceReportParser.Parse(path, maxResults: 10);
        Assert.Null(result.Error);

        var store = new ProfilingSessionStore();
        var sessionId = store.Store("test", result);
        var session = store.Get(sessionId);
        Assert.NotNull(session);

        var callers = store.GetCallers(session, "Repository.Query", maxResults: 10);

        Assert.Equal(2, callers.Count);
        Assert.Equal("App.ServiceA.Process", callers[0].FullName);
        Assert.Equal(30, callers[0].TimeMs);
        Assert.Equal("App.ServiceB.Handle", callers[1].FullName);
        Assert.Equal(15, callers[1].TimeMs);
    }
}
