using System.Text.Json;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class ProfilingSessionStoreTests : IDisposable
{
    private readonly ProfilingSessionStore _store = new();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private SpeedscopeParser.ProfilingResult CreateTestProfile()
    {
        // Build a profile with a known call graph:
        //   Main → ServiceA.Process → Repository.Query  (hot path)
        //   Main → ServiceA.Process → Logger.Log
        //   Main → ServiceB.Handle → Repository.Query
        //   Main → ServiceB.Handle → Cache.Get
        var speedscope = new
        {
            version = "0.0.1",
            shared = new
            {
                frames = new object[]
                {
                    new { name = "App.Program.Main()" },           // 0
                    new { name = "App.ServiceA.Process(Request)" }, // 1
                    new { name = "App.ServiceB.Handle(Event)" },   // 2
                    new { name = "Data.Repository.Query(string)" },// 3
                    new { name = "Infra.Logger.Log(string)" },     // 4
                    new { name = "Infra.Cache.Get(string)" }       // 5
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
                    endValue = 100,
                    samples = new int[][]
                    {
                        [0, 1, 3], // Main → ServiceA → Repository  w=30
                        [0, 1, 3], // Main → ServiceA → Repository  w=20
                        [0, 1, 4], // Main → ServiceA → Logger      w=10
                        [0, 2, 3], // Main → ServiceB → Repository  w=15
                        [0, 2, 5], // Main → ServiceB → Cache       w=25
                    },
                    weights = new double[] { 30, 20, 10, 15, 25 }
                }
            }
        };

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.speedscope.json");
        _tempFiles.Add(path);
        File.WriteAllText(path, JsonSerializer.Serialize(speedscope));
        return SpeedscopeParser.Parse(path, maxResults: 100);
    }

    [Fact]
    public void StoreAndRetrieveSession()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test run", result);

        Assert.NotNull(id);
        Assert.StartsWith("profile-", id);

        var session = _store.Get(id);
        Assert.NotNull(session);
        Assert.Equal(6, session.FrameNames.Length);
        Assert.Equal(5, session.Samples.Length);
        Assert.Equal(100.0, session.TotalDurationMs);
    }

    [Fact]
    public void ListSessionsReturnsStoredSessions()
    {
        var result = CreateTestProfile();
        var id1 = _store.Store("session 1", result);
        var id2 = _store.Store("session 2", result);

        var sessions = _store.ListSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.Id == id1);
        Assert.Contains(sessions, s => s.Id == id2);
    }

    [Fact]
    public void GetNonExistentSessionReturnsNull()
    {
        Assert.Null(_store.Get("does-not-exist"));
    }

    [Fact]
    public void SearchMethodsBySubstring()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        var matches = _store.SearchMethods(session, "Repository", maxResults: 10);
        Assert.Single(matches);
        Assert.Contains("Repository.Query", matches[0].Name);
    }

    [Fact]
    public void SearchMethodsByRegex()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Match any Service method
        var matches = _store.SearchMethods(session, "Service[AB]", maxResults: 10);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void GetCallersOfRepository()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Repository.Query is called by ServiceA.Process (samples 0,1: w=30+20=50)
        // and ServiceB.Handle (sample 3: w=15)
        var callers = _store.GetCallers(session, "Repository.Query", maxResults: 10);
        Assert.Equal(2, callers.Count);

        // ServiceA should be first (more time)
        Assert.Contains("ServiceA.Process", callers[0].Name);
        Assert.Equal(50.0, callers[0].TimeMs);
        Assert.Contains("ServiceB.Handle", callers[1].Name);
        Assert.Equal(15.0, callers[1].TimeMs);
    }

    [Fact]
    public void GetCalleesOfServiceA()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // ServiceA.Process calls Repository.Query (w=30+20=50) and Logger.Log (w=10)
        var callees = _store.GetCallees(session, "ServiceA.Process", maxResults: 10);
        Assert.Equal(2, callees.Count);

        Assert.Contains("Repository.Query", callees[0].Name);
        Assert.Equal(50.0, callees[0].TimeMs);
        Assert.Contains("Logger.Log", callees[1].Name);
        Assert.Equal(10.0, callees[1].TimeMs);
    }

    [Fact]
    public void GetCalleesOfMain()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Main calls ServiceA (w=30+20+10=60) and ServiceB (w=15+25=40)
        var callees = _store.GetCallees(session, "Main", maxResults: 10);
        Assert.Equal(2, callees.Count);
        Assert.Contains("ServiceA.Process", callees[0].Name);
        Assert.Equal(60.0, callees[0].TimeMs);
        Assert.Contains("ServiceB.Handle", callees[1].Name);
        Assert.Equal(40.0, callees[1].TimeMs);
    }

    [Fact]
    public void GetHotPathsThroughRepository()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        var paths = _store.GetHotPaths(session, "Repository.Query", maxResults: 5);
        Assert.True(paths.Count >= 2);

        // Hottest path should be Main → ServiceA → Repository (50ms)
        var hottest = paths[0];
        Assert.Equal(50.0, hottest.TimeMs, precision: 1);
        Assert.Equal(50.0, hottest.Percent, precision: 1);
    }

    [Fact]
    public void GetCallersOfLeafMethodReturnsParent()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Cache.Get is only called by ServiceB.Handle
        var callers = _store.GetCallers(session, "Cache.Get", maxResults: 10);
        Assert.Single(callers);
        Assert.Contains("ServiceB.Handle", callers[0].Name);
        Assert.Equal(25.0, callers[0].TimeMs);
    }

    [Fact]
    public void GetCalleesOfLeafMethodReturnsEmpty()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Cache.Get is always a leaf (top of stack), no callees
        var callees = _store.GetCallees(session, "Cache.Get", maxResults: 10);
        Assert.Empty(callees);
    }

    [Fact]
    public void SearchNoMatchReturnsEmpty()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        var matches = _store.SearchMethods(session, "DoesNotExist", maxResults: 10);
        Assert.Empty(matches);
    }

    [Fact]
    public void AllMethodsIncludesFramesWithOnlyTotalTime()
    {
        var result = CreateTestProfile();
        var id = _store.Store("test", result);
        var session = _store.Get(id)!;

        // Main never appears as leaf (self-time=0) but should be in AllMethods
        var mainMethod = session.AllMethods.FirstOrDefault(m => m.Name.Contains("Main"));
        Assert.NotNull(mainMethod);
        Assert.Equal(0.0, mainMethod.SelfTimeMs);
        Assert.Equal(100.0, mainMethod.TotalTimeMs); // Main is in all 5 samples
    }
}
