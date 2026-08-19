---
name: csharp-debugging
description: Debug C#/.NET code with the RoslynSense debugger tools — launch or attach a debugger, set breakpoints, step, evaluate expressions, inspect and change state, and watch values. Use when a test or application misbehaves and the cause isn't clear from the error message, on modern .NET or .NET Framework (including IIS Express / w3wp).
---
# C# Debugging with RoslynSense

Use the debugger to observe real behaviour instead of guessing from source. If a test is failing and the cause isn't clear from the error message, debug it rather than adding logging.

The debug engine is selected automatically from the target — never pick one:

| Target | Engine |
|--------|--------|
| .NET / .NET Core | netcoredbg, auto-provisioned on first use |
| .NET Framework | ICorDebug, built in |

`DebugStartTest` decides from the project's target framework; `DebugAttach` decides from the CLR the target process actually loaded, which is how attaching to `iisexpress.exe` or `w3wp.exe` resolves to the .NET Framework engine. A target of a different bitness (a 32-bit app pool) is driven through a matching worker process automatically.

## Starting a debug session

- **DebugStartTest** — debug a test project. Builds, launches the test host, and attaches the debugger. Use `filter` to target specific tests. Use `initialBreakpoints` to set breakpoints before execution starts (e.g., `"MyService.cs:42;MyTest.cs:10"`). Not supported for .NET Framework test projects — run the tests, then **DebugAttach** to the test host.
- **DebugLaunch** — start a project under the debugger, stopped at breakpoints that attaching would miss (Main, static constructors). Web projects start under IIS Express; open the reported URL to reach a breakpoint.
- **DebugAttach** — attach to a running .NET or .NET Framework process. Omit the PID to list available processes.
- To debug a web app: **RunProject** to start it, then **DebugAttach** with the returned PID.

## Controlling execution

1. **DebugSetBreakpoint** — set breakpoints. Supports conditions (e.g., `condition: "i == 42"`) and batch mode (semicolon-separated `file:line` pairs).
2. **DebugContinue** — control execution via `action`: `continue`, `step_in`, `step_over`, `step_out`, `pause`, `run_until` (temporary breakpoint at `filePath`:`line` with optional condition, auto-removed once hit), `run_to_cursor`, or `set_next_statement`.
3. **DebugEvaluate** — evaluate expressions at the current pause point. Separate multiple expressions with semicolons (e.g., `"x;y;list.Count"`).
4. **DebugStatus** — check debugger state, breakpoints, and current position. Use `includeLocals: true` for local variables and `includeStackTrace: true` for the call stack.
5. **DebugExpand** / **DebugSelectFrame** / **DebugSetVariable** — list and expand a frame's variables, switch stack frames to inspect a caller's state, and assign a new value to drive a hard-to-reach branch without editing code.

## Ending a debug session

- **DebugRemoveBreakpoint** — remove breakpoints by ID. Supports batch removal.
- **DebugDetach** — stop debugging but leave the process running. Use for a web app or service that was only being inspected and should not die with the session.
- **DebugStop** — stop the debug session and clean up all debugger processes. The debuggee is asked to shut down cleanly first (hosted services get their `StopAsync`, `finally` blocks run) and killed only if it does not exit within ten seconds.

## Debugging tips

- Always set breakpoints **before** calling DebugContinue.
- Use conditional breakpoints to avoid stopping on every iteration of a loop.
- Evaluate expressions to inspect state without modifying code.
- **DebugWatchValue** breaks when a value *changes* rather than when a line is reached — the answer to "what is setting this field to null?". It slows execution; `action: "clear"` drops the watches and restores full speed.
- If a breakpoint never binds, **DebugModules** shows whether the assembly has symbols — without a PDB, no breakpoint in that assembly can bind, however correct the file and line are.
- In .NET Framework code, breakpoints in ASPX inline code bind once the generated `App_Web_*` assembly loads — hit the page after setting the breakpoint rather than assuming it failed.

## Tool selection

| Task | Preferred Tool | Avoid |
|------|---------------|-------|
| Debug a failing test | **DebugStartTest** | Adding Console.WriteLine |
| Debug a legacy ASP.NET site | **RunProject** then **DebugAttach** with the PID | Assuming .NET Framework cannot be debugged |
| Find what mutates a field | **DebugWatchValue** | Breakpoints on every write site |
