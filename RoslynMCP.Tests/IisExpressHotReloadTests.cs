using System.Diagnostics;
using System.Net.Sockets;
using RoslynMCP.Services;
using RoslynMCP.Services.Debugging;
using RoslynMCP.Services.HotReload;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Opt-in gate for the IIS Express Edit-and-Continue test: everything the Framework gate guards
/// against, plus IIS Express itself, plus the x86 flavour specifically — the test forces the
/// 32-bit host because that is what legacy sites overwhelmingly run as.
/// </summary>
public sealed class IisExpressHotReloadFactAttribute : FactAttribute
{
    public IisExpressHotReloadFactAttribute()
    {
        if (FrameworkHotReloadFactAttribute.SkipReason is { } reason)
            Skip = reason;
        else if (NetFxToolchain.Info.IisExpressX86.Length == 0)
            Skip = "The 32-bit IIS Express is not installed on this machine.";
    }
}

/// <summary>
/// Edit-and-Continue against a live classic ASP.NET site hosted by 32-bit IIS Express.
/// </summary>
/// <remarks>
/// <para>
/// This is the scenario the console-target tests cannot stand in for: the site's assembly is
/// shadow-copied under "Temporary ASP.NET Files", loaded into a secondary AppDomain, and only
/// when the first request compiles the site — so the EnC JIT flag, the module registry keyed by
/// simple name, and the delta's MVID→name mapping all have to survive hosting details a console
/// exe never has. The host is deliberately the x86 IIS Express on an x64 machine, so the apply
/// also travels through the bitness-matched worker.
/// </para>
/// <para>
/// The site keeps its code in a bin assembly (the WAP shape) rather than inline in markup:
/// App_Web_* assemblies are deliberately not EnC targets, so an inline edit can never hot
/// reload — this test covers the path that is supposed to work.
/// </para>
/// </remarks>
[Collection(DebuggerCollection.Name)]
public class IisExpressHotReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"iis-hotreload-{Guid.NewGuid():N}");

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net48</TargetFramework>
            <AssemblyName>SiteCode</AssemblyName>
            <RootNamespace>SiteCode</RootNamespace>
            <Optimize>false</Optimize>
            <DebugType>full</DebugType>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="System.Web" />
          </ItemGroup>
        </Project>
        """;

    private const string BaselineSource = """
        namespace SiteCode
        {
            public class Handler : System.Web.IHttpHandler
            {
                public bool IsReusable { get { return true; } }

                public static int Compute(int input)
                {
                    return input * 2;
                }

                public void ProcessRequest(System.Web.HttpContext context)
                {
                    context.Response.ContentType = "text/plain";
                    context.Response.Write(Compute(3));
                }
            }
        }
        """;

    /// <summary>
    /// The same handler plus a background user-code thread that never stops running, so a Break
    /// All into the "idle" site still finds a thread parked in user code with a source location.
    /// </summary>
    private const string BusySource = """
        namespace SiteCode
        {
            public class Handler : System.Web.IHttpHandler
            {
                private static System.Threading.Thread s_pulse;

                public bool IsReusable { get { return true; } }

                public static int Compute(int input)
                {
                    return input * 2;
                }

                public static void Pulse()
                {
                    while (true)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }

                public void ProcessRequest(System.Web.HttpContext context)
                {
                    if (s_pulse == null)
                    {
                        s_pulse = new System.Threading.Thread(Pulse) { IsBackground = true };
                        s_pulse.Start();
                    }
                    context.Response.ContentType = "text/plain";
                    context.Response.Write(Compute(3));
                }
            }
        }
        """;

    private const string WebConfig = """
        <?xml version="1.0"?>
        <configuration>
          <system.web>
            <compilation debug="true" targetFramework="4.8" />
            <httpRuntime targetFramework="4.8" />
          </system.web>
        </configuration>
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The site on disk plus the launched, warmed-up IIS Express hosting it.</summary>
    private sealed record Site(string Csproj, string SourcePath, string Url, int Pid, long StartTicks);

    /// <summary>
    /// Writes the site and its code project, builds it, launches x86 IIS Express under the
    /// debugger, and serves the first request — after which SiteCode.dll is loaded and editable.
    /// </summary>
    private async Task<Site> LaunchSiteAsync(
        RoslynMCP.Services.Debugging.PublishingDebugBackend backend, HttpClient http,
        string source = BaselineSource)
    {
        string siteDir = Path.Combine(_root, "site");
        string projDir = Path.Combine(_root, "proj");
        Directory.CreateDirectory(Path.Combine(siteDir, "bin"));
        Directory.CreateDirectory(projDir);

        string csproj = Path.Combine(projDir, "SiteCode.csproj");
        string sourcePath = Path.Combine(projDir, "Handler.cs");

        await File.WriteAllTextAsync(csproj, Project);
        await File.WriteAllTextAsync(sourcePath, source);
        await File.WriteAllTextAsync(Path.Combine(siteDir, "web.config"), WebConfig);
        await File.WriteAllTextAsync(Path.Combine(siteDir, "Handler.ashx"),
            """<%@ WebHandler Language="C#" Class="SiteCode.Handler" %>""");

        Assert.True(await BuildAsync(csproj), "The site's code assembly did not build.");

        string built = Path.Combine(projDir, "bin", "Debug", "net48");
        File.Copy(Path.Combine(built, "SiteCode.dll"), Path.Combine(siteDir, "bin", "SiteCode.dll"));
        File.Copy(Path.Combine(built, "SiteCode.pdb"), Path.Combine(siteDir, "bin", "SiteCode.pdb"));

        int port = FreePort();

        Assert.DoesNotContain("Error:", await backend.LaunchAsync(
            NetFxToolchain.Info.IisExpressX86,
            [$"/path:{siteDir}", $"/port:{port}", "/systray:false"],
            null, siteDir));
        _ = backend.ContinueAsync();

        // The first request is what compiles the site and loads SiteCode.dll — before it
        // there is no module to edit. Cold start under a debugger is slow; be patient.
        string url = $"http://localhost:{port}/Handler.ashx";
        Assert.Equal("6", await GetWithRetriesAsync(http, url, TimeSpan.FromSeconds(120)));

        Assert.NotNull(backend.DebuggeePid);
        using var target = Process.GetProcessById(backend.DebuggeePid.Value);
        return new Site(csproj, sourcePath, url, target.Id, target.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>
    /// Applying to an idle site — no request in flight, no user-code thread anywhere — is the
    /// one stop shape the desktop CLR faults on. The edit must be accepted as queued, survive the
    /// wait, and leave a site that still stops at the user's breakpoints once it has landed.
    /// </summary>
    /// <remarks>
    /// Before the queue this exact sequence access-violated inside <c>ApplyChanges</c> and took
    /// the x86 worker down with it, which the editor experienced as hot reload silently doing
    /// nothing and the debug session going dead. What carries the queued edit into the process is
    /// covered by <see cref="AQueuedEditLandsOnTheNextRequestWithoutABreakpoint"/>; what this adds
    /// is the debugging that goes on around it. The delta arrives through breakpoints the engine
    /// armed and then took back out, so a breakpoint the user sets afterwards still has to bind
    /// and stop inside the edited method.
    /// </remarks>
    [IisExpressHotReloadFact]
    public async Task AnApplyToAnIdleSiteIsQueuedAndLeavesTheUsersBreakpointsWorking()
    {
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(DebugRuntime.NetFramework);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Notice += n =>
        {
            notices.Enqueue($"{n.Kind}: {n.Message} {n.FilePath}:{n.Line}");
            if (n.Kind == RoslynMCP.Services.Debugging.DebugNoticeKind.BreakpointBound)
                bound.TrySetResult();
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var pingCts = new CancellationTokenSource();
        Task? pinger = null;

        string? csproj = null;
        try
        {
            var site = await LaunchSiteAsync(backend, http);
            csproj = site.Csproj;

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await File.WriteAllTextAsync(
                site.SourcePath, BaselineSource.Replace("input * 2", "input * 10"));
            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());
            Assert.Contains("queued", outcome.Summary);

            // The queue must leave a live, resumed debuggee behind — the crash it replaces left a
            // dead worker — and requests alone must carry the edit in, since nothing has stopped
            // this site and nothing is going to.
            Assert.Equal("30", await GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(60)));
            AssertSameProcess(backend, site);

            // The edit is in the process now, applied at an entry breakpoint the engine armed and
            // then removed. A breakpoint the user sets on the same method has to bind against the
            // edited version and stop there, which is the part an apply can quietly break: the
            // bindings made before the edit name a method version that no longer exists.
            int line = Array.FindIndex(
                BaselineSource.ReplaceLineEndings("\n").Split('\n'),
                l => l.Contains("return input * 2", StringComparison.Ordinal)) + 1;
            var (setMessage, breakpointId) = await backend.SetBreakpointAsync(site.SourcePath, line);
            Assert.True(breakpointId is not null, setMessage);

            // Binding is asynchronous (the engine suspends the target and sweeps its modules),
            // so a request fired straight away races the arming and sometimes sails through.
            // The user-visible equivalent is the breakpoint turning solid in the editor.
            var armed = await Task.WhenAny(bound.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(armed == bound.Task,
                "The breakpoint never bound:\n" + backend.GetStatus() +
                "\n--- notices ---\n" + string.Join("\n", notices));

            // Requests keep coming until the stop is observed — the runtime occasionally
            // delivers a first breakpoint event stale (thread already ran on), which the engine
            // resumes past, and only the next hit stops. One of these will stop at Compute.
            pinger = Task.Run(async () =>
            {
                while (!pingCts.IsCancellationRequested)
                {
                    try { _ = await http.GetStringAsync(site.Url, pingCts.Token); } catch { }
                    try { await Task.Delay(500, pingCts.Token); } catch { }
                }
            });

            bool stopped = false;
            for (int attempt = 0; attempt < 100 && !stopped; attempt++)
            {
                stopped = backend.CurrentFrame is not null;
                if (!stopped)
                    await Task.Delay(200);
            }
            Assert.True(stopped,
                $"No request ever stopped at the breakpoint (set: {setMessage}):\n" +
                backend.GetStatus() + "\n--- notices ---\n" + string.Join("\n", notices));

            await backend.RemoveBreakpointAsync(breakpointId.Value);
            _ = backend.ContinueAsync();

            // And the site goes back to serving the edited code once the stop is released.
            string last = "";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && last != "30")
            {
                try { last = (await http.GetStringAsync(site.Url)).Trim(); } catch { }
                if (last != "30")
                    await Task.Delay(500);
            }
            pingCts.Cancel();
            await pinger;
            Assert.True(last == "30",
                $"The queued edit never took effect (last answer: '{last}'):\n" +
                backend.GetStatus() + "\n--- notices ---\n" + string.Join("\n", notices));
            AssertSameProcess(backend, site);
        }
        finally
        {
            pingCts.Cancel();
            try
            {
                if (pinger is not null)
                    await pinger.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { await StopSiteAsync(csproj, backend); }
        }
    }

    /// <summary>
    /// The user's actual inner loop: an idle site, an edit, an apply, and a plain request — with
    /// no breakpoint anywhere in the session.
    /// </summary>
    /// <remarks>
    /// Every other case here sets a breakpoint, which is what made the queue look like it worked.
    /// It does not: an ASP.NET site edited between requests has no user-code thread for
    /// <c>ApplyChanges</c> to run from, so the delta is queued, and a queue that waits for a
    /// breakpoint waits forever in a loop that has none. The site kept serving the old code until
    /// the process was restarted, while the editor had already reported the apply as done. The
    /// engine now arms the edited methods themselves, so the next request is the stop the edit
    /// lands at.
    /// </remarks>
    [IisExpressHotReloadFact]
    public async Task AQueuedEditLandsOnTheNextRequestWithoutABreakpoint()
    {
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(DebugRuntime.NetFramework);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        backend.Notice += n => notices.Enqueue($"{n.Kind}: {n.Message} {n.FilePath}:{n.Line}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        string? csproj = null;
        try
        {
            var site = await LaunchSiteAsync(backend, http);
            csproj = site.Csproj;

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await File.WriteAllTextAsync(
                site.SourcePath, BaselineSource.Replace("input * 2", "input * 10"));
            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            // The apply itself still cannot reach an idle site, and saying otherwise would hide
            // the very thing this test exists to check: what makes the edit land is the request.
            Assert.Contains("queued", outcome.Summary, StringComparison.OrdinalIgnoreCase);

            // No breakpoint is ever set. Requests alone have to carry the edit into the process,
            // and the site has to stay up while they do.
            string last = "";
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && last != "30")
            {
                try { last = (await http.GetStringAsync(site.Url)).Trim(); } catch { }
                if (last != "30")
                    await Task.Delay(500);
            }

            Assert.True(last == "30",
                $"The queued edit never reached the running site (last answer: '{last}'):\n" +
                backend.GetStatus() + "\n--- notices ---\n" + string.Join("\n", notices));

            // The stop the edit landed at is the engine's own, so it must never have surfaced as a
            // breakpoint stop: a user with no breakpoints set expects a site that just serves.
            // (The apply's own Break All is reported as a pause and is expected — it is how an
            // apply asks an idle target whether it can take the edit now.)
            Assert.DoesNotContain(notices, n => n.StartsWith("Stopped: breakpoint", StringComparison.Ordinal));
            Assert.Null(backend.CurrentFrame);

            // And the site keeps serving the new code afterwards, rather than stopping again on
            // an entry breakpoint the flush should have taken back out.
            Assert.Equal("30", await GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(30)));
            AssertSameProcess(backend, site);
        }
        finally
        {
            await StopSiteAsync(csproj, backend);
        }
    }

    /// <summary>
    /// The shape a real debugging session is in when the edit is made: a breakpoint already sits
    /// inside the method about to be edited, requests keep arriving, and the user presses Continue
    /// while all of it is going on.
    /// </summary>
    /// <remarks>
    /// The other idle-site cases set their breakpoint after the edit has landed, so the binding
    /// they check was made against the edited version. Here it is made against the version
    /// <c>ApplyChanges</c> is about to replace, and the engine's own flush breakpoint arrives at
    /// the entry of that same method — user binding and engine binding on one method across one
    /// version change, which is the arrangement the reported failure was in.
    /// <para>
    /// What it holds is the outcome and the clock: the edit lands, the worker is still the one
    /// that was serving before, and it happens fast enough that the site reads as reloaded rather
    /// than hung. The budget is the assertion that matters — the report was not of a failure but
    /// of a site that stopped answering, and a stop the engine takes for itself is paid for out of
    /// the request it takes it in.
    /// </para>
    /// <para>
    /// The Continues are noise, not a discriminator, and the name no longer claims otherwise: a
    /// Continue against a running debuggee is refused by the runtime, and the window where one
    /// does damage — between the runtime delivering the flush breakpoint and the session thread
    /// picking the work up — is inside a single flush stop and cannot be aimed at from out here.
    /// The guard in <c>DebugSession.Continue</c> that closes it is not exercised by this test.
    /// </para>
    /// </remarks>
    [IisExpressHotReloadFact]
    public async Task AQueuedEditLandsPromptlyWithABreakpointInsideTheEditedMethod()
    {
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(DebugRuntime.NetFramework);
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Notice += n =>
        {
            notices.Enqueue($"{n.Kind}: {n.Message} {n.FilePath}:{n.Line}");
            if (n.Kind == RoslynMCP.Services.Debugging.DebugNoticeKind.BreakpointBound)
                bound.TrySetResult();
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var pingCts = new CancellationTokenSource();
        Task? pinger = null;
        Task? hammer = null;

        string? csproj = null;
        try
        {
            var site = await LaunchSiteAsync(backend, http);
            csproj = site.Csproj;

            // Set before the edit, on the line the edit changes, which is what makes this
            // different from the other idle-site case: the binding is against the version
            // ApplyChanges replaces, and the engine's own flush breakpoint lands on the same
            // method a moment later.
            int line = Array.FindIndex(
                BaselineSource.ReplaceLineEndings("\n").Split('\n'),
                l => l.Contains("return input * 2", StringComparison.Ordinal)) + 1;
            var (setMessage, breakpointId) = await backend.SetBreakpointAsync(site.SourcePath, line);
            Assert.True(breakpointId is not null, setMessage);

            var armed = await Task.WhenAny(bound.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(armed == bound.Task,
                "The breakpoint never bound:\n" + backend.GetStatus() +
                "\n--- notices ---\n" + string.Join("\n", notices));

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await File.WriteAllTextAsync(
                site.SourcePath, BaselineSource.Replace("input * 2", "input * 10"));
            var outcome = await session!.ApplyAsync();
            Assert.True(outcome.Ok,
                $"{outcome.Summary}\n" + string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            // Requests carry the edit in; the Continues run alongside them for as long as they
            // do, because the window this is about opens and closes inside one flush stop.
            pinger = Task.Run(async () =>
            {
                while (!pingCts.IsCancellationRequested)
                {
                    try { _ = await http.GetStringAsync(site.Url, pingCts.Token); } catch { }
                    try { await Task.Delay(250, pingCts.Token); } catch { }
                }
            });

            hammer = Task.Run(async () =>
            {
                while (!pingCts.IsCancellationRequested)
                {
                    try { _ = backend.ContinueAsync(pingCts.Token); } catch { }
                    try { await Task.Delay(2000, pingCts.Token); } catch { }
                }
            });

            // The edit lands despite all of it, and the process that serves it is the one that
            // was serving before — the crash this replaces took the worker down with it, which the
            // site experiences as never answering again. The user's stop is released by the
            // hammer as fast as it is taken, so what is checked here is the outcome, not the stop.
            string last = "";
            var started = System.Diagnostics.Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && last != "30")
            {
                try { last = (await http.GetStringAsync(site.Url)).Trim(); } catch { }
                if (last != "30")
                    await Task.Delay(250);
            }

            pingCts.Cancel();
            Assert.True(last == "30",
                $"The queued edit never reached the running site (last answer: '{last}', " +
                $"breakpoint set: {setMessage}):\n" +
                backend.GetStatus() + "\n--- notices ---\n" + string.Join("\n", notices) +
                "\n--- engine trace ---\n" + EngineTrace());

            // The budget is the point. A stop the engine takes for itself costs the request it
            // takes it in, and a debuggee that spends minutes suspended is one the user watches
            // hang - which is how this was reported, not as a failure but as a site that stopped
            // answering. Generous enough for a cold IIS worker, far short of what the stall cost.
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(60),
                $"The edit took {started.Elapsed.TotalSeconds:F0}s to land, which is a site the " +
                "user experiences as hung rather than reloaded:\n" +
                backend.GetStatus() + "\n--- engine trace ---\n" + EngineTrace());

            await backend.RemoveBreakpointAsync(breakpointId.Value);
            _ = backend.ContinueAsync();
            AssertSameProcess(backend, site);
            Assert.Equal("30", await GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(60)));
        }
        finally
        {
            pingCts.Cancel();
            foreach (var background in new[] { pinger, hammer })
            {
                if (background is not null)
                    try { await background; } catch { }
            }
            await StopSiteAsync(csproj, backend);
        }
    }

    /// <summary>
    /// The running-target apply again, but with a user-code thread alive in the site's
    /// AppDomain for Break All to adopt.
    /// </summary>
    /// <remarks>
    /// The idle-server apply faults inside <c>ApplyChanges</c>; the breakpoint-stop apply works.
    /// This case sits exactly between them: the target is running and the stop is synthesized,
    /// but the adopted thread is parked in the edited module's own code. If this passes, the
    /// crash is about <em>which thread</em> the synthesized stop adopts, not about IIS hosting —
    /// and a fix can be a better adoption rule rather than refusing running applies outright.
    /// </remarks>
    [IisExpressHotReloadFact]
    public async Task AnEditAppliesToARunningSiteWhenAUserCodeThreadExists()
    {
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(DebugRuntime.NetFramework);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        string? csproj = null;
        try
        {
            var site = await LaunchSiteAsync(backend, http, BusySource);
            csproj = site.Csproj;

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await File.WriteAllTextAsync(
                site.SourcePath, BusySource.Replace("input * 2", "input * 10"));
            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok && outcome.AppliedTo.Count > 0,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            Assert.Equal("30", await GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(30)));
            AssertSameProcess(backend, site);
        }
        finally
        {
            await StopSiteAsync(csproj, backend);
        }
    }

    /// <summary>
    /// The same edit, applied from a breakpoint inside an in-flight request instead of from a
    /// Break All into an idle server.
    /// </summary>
    /// <remarks>
    /// This is the experiment that splits the crash's two candidate causes. At a breakpoint in
    /// <c>Compute</c> the stopped thread is deep in user code — the stop shape the console tests
    /// always had, and the one every shipping .NET debugger requires before allowing an apply. If the
    /// apply survives here but faults from an idle Break All, the bug is the stop context the
    /// engine synthesises when no user-code thread exists; if it faults here too, the problem is
    /// the hosted CLR itself (shadow copy, secondary AppDomain).
    /// </remarks>
    [IisExpressHotReloadFact]
    public async Task AnEditAppliesFromABreakpointInsideARequest()
    {
        var backend = (RoslynMCP.Services.Debugging.PublishingDebugBackend)
            DebugSessionManager.CreateSession(DebugRuntime.NetFramework);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        string? csproj = null;
        try
        {
            var site = await LaunchSiteAsync(backend, http);
            csproj = site.Csproj;

            int line = Array.FindIndex(
                BaselineSource.ReplaceLineEndings("\n").Split('\n'),
                l => l.Contains("return input * 2", StringComparison.Ordinal)) + 1;
            var (setMessage, breakpointId) = await backend.SetBreakpointAsync(site.SourcePath, line);
            Assert.True(breakpointId is not null, setMessage);

            // The request that will sit at the breakpoint while the edit is applied.
            var inFlight = Task.Run(() => GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(60)));

            bool stopped = false;
            for (int attempt = 0; attempt < 100 && !stopped; attempt++)
            {
                stopped = backend.CurrentFrame is not null;
                if (!stopped)
                    await Task.Delay(200);
            }
            Assert.True(stopped, "The request never reached the breakpoint:\n" + backend.GetStatus());

            var (session, message) = await HotReloadService.StartAsync(csproj);
            Assert.True(session is not null, message);

            await File.WriteAllTextAsync(
                site.SourcePath, BaselineSource.Replace("input * 2", "input * 10"));
            var outcome = await session!.ApplyAsync();

            Assert.True(outcome.Ok && outcome.AppliedTo.Count > 0,
                $"{outcome.Summary}\n" +
                string.Join("\n", outcome.Diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.Message}")) +
                string.Join("\n", outcome.Errors) +
                "\n--- engine ---\n" + backend.GetStatus());

            // Release the stopped request. The engine handles FunctionRemapOpportunity, so the
            // frame stopped inside Compute jumps to the edited version on resume — the in-flight
            // request itself answers with the new code, not just the calls after it. (Before the
            // remap support it finished on the old version and answered 6.)
            await backend.RemoveBreakpointAsync(breakpointId.Value);
            _ = backend.ContinueAsync();
            Assert.Equal("30", await inFlight);

            Assert.Equal("30", await GetWithRetriesAsync(http, site.Url, TimeSpan.FromSeconds(30)));
            AssertSameProcess(backend, site);
        }
        finally
        {
            await StopSiteAsync(csproj, backend);
        }
    }

    /// <summary>The engine's own account of the run, which it now leaves on disk beside the
    /// session's state file. Only the tail: a failure is about what happened last.</summary>
    private static string EngineTrace()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "roslyn-sense", "debug", $"{Environment.ProcessId}.log");
        try
        {
            var lines = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, lines[Math.Max(0, lines.Length - 120)..]);
        }
        catch (Exception ex)
        {
            return $"(no engine trace at {path}: {ex.Message})";
        }
    }

    private static void AssertSameProcess(PublishingDebugBackend backend, Site site)
    {
        Assert.Equal(site.Pid, backend.DebuggeePid);
        using var target = Process.GetProcessById(site.Pid);
        Assert.False(target.HasExited, "The IIS Express process must survive hot reload.");
        Assert.Equal(site.StartTicks, target.StartTime.ToUniversalTime().Ticks);
    }

    private static async Task StopSiteAsync(string? csproj, PublishingDebugBackend backend)
    {
        Process? target = null;
        if (backend.DebuggeePid is { } pid)
        {
            try { target = Process.GetProcessById(pid); }
            catch (ArgumentException) { }
        }

        try
        {
            if (csproj is not null && HotReloadService.Get(csproj) is { } session)
                await session.StopAsync();
        }
        finally
        {
            try { backend.Stop(); }
            finally
            {
                DebugSessionManager.DisposeSession();
                if (target is not null)
                {
                    using (target)
                    {
                        if (!target.HasExited)
                            target.Kill(entireProcessTree: true);
                        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
    }

    private static async Task<string> GetWithRetriesAsync(HttpClient http, string url, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        string last = "(no response)";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return (await http.GetStringAsync(url)).Trim();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex.Message;
            }
            await Task.Delay(500);
        }
        return $"(the site never answered: {last})";
    }

    private static int FreePort()
    {
        var listener = TcpListener.Create(0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> BuildAsync(string csproj)
    {
        using var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(csproj),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "build", csproj, "-c", "Debug", "--nologo" },
        })!;

        var output = build.StandardOutput.ReadToEndAsync();
        var error = build.StandardError.ReadToEndAsync();
        try
        {
            await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch
        {
            if (!build.HasExited)
                build.Kill(entireProcessTree: true);
            await build.WaitForExitAsync();
            throw;
        }

        if (build.ExitCode != 0)
            Assert.Fail(await output + "\n" + await error);

        return true;
    }
}
