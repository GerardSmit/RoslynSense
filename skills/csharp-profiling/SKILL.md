---
name: csharp-profiling
description: Profile CPU hotspots in C#/.NET code with the RoslynSense profiling tools — sample tests, applications, or running processes, and drill into hot paths and their callers. Use for performance questions ("why is this slow", "where does the time go") on modern .NET or .NET Framework (including IIS Express / w3wp).
---
# C# Profiling with RoslynSense

Measure before optimizing — profile the scenario, find the hot methods, and only then change code.

CPU sampling works on both runtimes and is auto-provisioned on first use: modern .NET uses
[dotnet-trace](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace), .NET Framework
uses the free JetBrains dotTrace command-line profiler. Both feed the same session store, so the
investigation tools work identically.

## Collecting a profile

- **ProfileTests** — profile a test project's execution. Use `filter` to target specific tests. Returns the hottest methods by self-time and a **session ID** for follow-up investigation. Modern .NET only — for .NET Framework tests, run them and attach with **ProfileProcess**.
- **ProfileApp** — profile an application's execution. Modern .NET apps run under dotnet-trace; legacy ASP.NET sites are launched under IIS Express, sampled with dotTrace, and stopped again. Returns the same hot methods table and session ID.
- **ProfileProcess** — attach to an already-running process by PID (e.g. from **RunProject**), .NET Framework or modern .NET alike. This is the way to profile `iisexpress.exe`/`w3wp.exe` hosting a running site. Blocks for the whole duration; use `hitUrls` for traffic, or use ProfileStart/ProfileStop instead when you want to drive the app yourself.
- **ProfileStart** / **ProfileStop** — recording mode: ProfileStart attaches and returns immediately, you exercise the app yourself (curl the endpoints, run the scenario, click through pages), then ProfileStop collects and returns the hot methods. Works on both runtimes. A recording stops collecting by itself after `maxDurationSeconds` (default 600) but its data stays available for ProfileStop.

For web apps, pass `hitUrls` (semicolon-separated) so the pages under investigation are actually
requested during the profiling window — profiling an idle server measures nothing. ProfileApp
defaults to hammering the app's root URL for web projects.

By default the hot-methods table shows **only the solution's own code** — framework and
third-party methods (System.*, SQL client internals, CMS plumbing) are hidden and counted in a
note. Pass `ownCodeOnly=false` to see everything. For ProfileProcess, pass `projectPath` so the
right solution defines what counts as own code. The stored session always keeps every method, so
the investigation tools below search the full profile regardless of this setting.

ProfileTests and ProfileApp use existing build output, so build the project first if needed.

## Investigating profile results

After profiling, use the session ID to drill into the results:

1. **ListProfilingSessions** — list active profiling sessions (retained for 30 minutes).
2. **ProfileSearchMethods** — search for methods by name pattern (substring or regex) in a session.
3. **ProfileCalls** — show who calls a hot method (`direction: "callers"`) or what it calls (`direction: "callees"`), and how much CPU time flows through each.
4. **ProfileHotPaths** — show the hottest execution paths through a method (call chains).

## Profiling tips

- Profile with a focused test filter first to reduce noise.
- Methods with high **Self%** spend time in their own code — these are the optimization targets.
- Methods with high **Total%** but low **Self%** are on hot call paths — optimize their callees instead.
- Use **ProfileCalls** with `direction: "callers"` to trace upward from a hot method to understand *why* it's being called, or `direction: "callees"` to trace downward to find *where* time is actually spent.
- Combine with **GoToDefinition** to navigate to a hot method's source code.

## Tool selection

| Task | Preferred Tool | Avoid |
|------|---------------|-------|
| Profile CPU hotspots | **ProfileTests** or **ProfileApp** | Manual dotnet-trace |
| Profile a running (IIS Express) site | **ProfileProcess** with the RunProject PID | Assuming .NET Framework cannot be profiled |
| Investigate hot methods | **ProfileCalls** | Guessing without data |
