# Hot reload end-to-end tests

The live tests build a temporary application, launch it through the product's hot reload
transport, edit its source, and verify the running application's output. A successful delta
emission or mocked acknowledgement alone does not prove that the edit reached the runtime.

| Suite | Runtime path | Prerequisites |
| --- | --- | --- |
| `CoreClrHotReloadTests` | Startup hook and `MetadataUpdater.ApplyUpdate`, using an apphost or `dotnet app.dll`; the ICorDebug engine's `ApplyChanges` for a launched and for an attached debuggee; unsaved full-text and ranged edits through the LSP handlers | .NET 10 SDK; netcoredbg for the attached-debugger rejection test |
| `FrameworkHotReloadTests` | Desktop CLR Edit and Continue through x86 and x64 debug workers; portable and Windows PDBs; SDK and classic non-SDK projects | Windows, Visual Studio or Build Tools MSBuild, .NET Framework 4.8 runtime/reference assemblies, both published workers, x86 and x64 .NET 10 runtimes |
| `IisExpressHotReloadTests` | 32-bit IIS Express with shadow-copied assemblies and secondary AppDomains; running, paused, and queued idle-site edits, including one with no breakpoint anywhere in the session | Framework prerequisites plus 32-bit IIS Express |

CoreCLR updates reach a process either through its in-process `MetadataUpdater`, when no
debugger is attached, or through the debugger's `ICorDebugModule2::ApplyChanges` when the
tool's own ICorDebug engine is debugging it, which is the default engine on Windows. The
runtime refuses `MetadataUpdater` while any debugger is attached, so a process debugged with
netcoredbg (`roslynSense.debugger.coreClrEngine` set to `netcoredbg`, or any host other than
Windows) cannot be hot reloaded; the live test verifies that the failure is reported and the
edit stays pending. `AnEditAppliesThroughTheDebuggerThatLaunchedTheProcess` covers the F5 case
on the ICorDebug engine. `AnEditAppliesThroughADebuggerThatAttachedLater` covers attaching to a
process that was started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, as Run with Hot Reload
starts it: the runtime decided the modules were updatable when they loaded, so the debugger's
apply works on them even though the EnC JIT flag can no longer be set, and the test exercises
the edited method both already compiled and not yet compiled at attach time (the former once
regressed, because the engine remapped every call back into the old code). A process started
without the variable reports that hot reload needs it. .NET Framework applies always go through
the debugger and cover breakpoint and stepping behavior after an edit.

An idle ASP.NET site is the one target that cannot take an edit when it arrives: no user-code
thread is stopped in it, so `ApplyChanges` would fault and the delta is queued instead. What
lands it is the site's next request, through breakpoints the engine arms on the edited methods
themselves and removes again once the edit is in.
`AQueuedEditLandsOnTheNextRequestWithoutABreakpoint` is the case that holds this honest: it
sets no breakpoint at all, because a test that sets one proves only that the queue works for a
loop the user is not in. Without the arming it fails with the site still answering from the
built code, which is exactly how the bug was reported.

`AQueuedEditLandsPromptlyWithABreakpointInsideTheEditedMethod` covers what those invisible stops
cost. It sets its breakpoint inside the edited method *before* applying, unlike the cases above,
so the user's binding names a version `ApplyChanges` replaces while the engine's flush breakpoint
sits at that same method's entry, and it holds the clock as well as the outcome: the edit has to
land inside a budget, because the failure it came from was reported as a site that stopped
answering rather than as an apply that failed.

An engine that goes wrong now leaves something to read: every notice except the debuggee's own
console output is appended to `%TEMP%oslyn-sense\debug\<owner-pid>.log`, beside the session's
state file, which is where to look first when an apply reports success and the app disagrees.

`UnsavedEditorChangesReachTheRunningProcessThroughTheLspHandler` calls the actual LSP open,
change, apply, and stop handlers against a live CoreCLR process. It verifies consecutive
full-text and ranged buffer edits reach the runtime while the source file on disk stays
unchanged. `RepeatedEditsApplyToALegacyProjectBuiltWithVisualStudioMsBuild` builds a classic
non-SDK .NET Framework project through `LaunchHandler.BuildAsync` and Visual Studio MSBuild,
then exercises repeated edits through the x86 worker with a full Windows PDB.

Both runtime suites also build using a different Windows drive-letter case from the
workspace. These cases verify that portable and Windows PDB document names are matched
correctly instead of silently treating an edited source file as design-time-only.

From the repository root, build the test payloads explicitly before running the Windows E2Es:

```powershell
dotnet build RoslynMCP.sln --configuration Release -p:BuildDebugWorkers=true
$env:ROSLYNSENSE_TEST_FX_HOTRELOAD = '1'
dotnet test RoslynMCP.Tests/RoslynMCP.Tests.csproj --configuration Release --no-build `
  -p:UseAppHost=false `
  --filter 'FullyQualifiedName~CoreClrHotReloadTests|FullyQualifiedName~FrameworkHotReloadTests|FullyQualifiedName~IisExpressHotReloadTests' `
  --blame-hang --blame-hang-timeout 3m --blame-hang-dump-type mini `
  --logger 'console;verbosity=normal' --logger 'trx;LogFileName=hot-reload-e2e.trx' `
  --results-directory artifacts/test-results/hot-reload
```

`BuildDebugWorkers=true` must be a command-line property so the test project copies both
workers beside its assembly. The x86 worker is a .NET 10 application even though its target
runs on .NET Framework: install the x86 .NET runtime under `C:\Program Files (x86)\dotnet`.
The workers clear inherited runtime overrides, so putting a private runtime only on `PATH`
or setting `DOTNET_ROOT_X86` does not make that runtime available to the worker.

The Framework opt-in remains disabled for ordinary local test runs because native Edit and
Continue can crash its worker. CI enables it in a separate **Hot reload E2E (.NET and .NET
Framework)** step, verifies both worker payloads exist, and rejects missing or skipped results
for either console suite. It also runs and enforces the IIS suite when 32-bit IIS Express is
installed; an absent IIS installation produces an explicit CI warning. TRX files and hang
diagnostics are uploaded with the other test diagnostics.

`HotReloadTests` and `HotReloadLifetimeTests` cover transport and ownership separately. Run
`npm test` in `vscode-extension` for the extension's mocked launch, environment, save/apply,
restart, and owner-cleanup tests. The **Run with Hot Reload** command tests compose the real
task, launch, and hot reload modules to check build-before-baseline ordering, the correct
runtime route, and release after process termination.

`npm run test:integration` also includes a real VS Code hot reload scenario: **Run with Hot
Reload**, an integrated terminal accepting `Console.ReadKey`, two unsaved editor changes
applied to the same runtime PID, and session shutdown. The runner publishes the server and
checks its startup-hook payload, then uses a fresh fixture copy, VS Code profile and server
settings directory for each run. To use an existing build, set `ROSLYNSENSE_TEST_SERVER` to
its executable before running the command. CI runs this suite on Windows stable and Linux
stable/Insiders and uploads editor logs, protocol diagnostics and runtime values. This covers
the direct LSP server mode; daemon routing is a separate path.
