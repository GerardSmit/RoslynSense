# RoslynSense for VSCode

C# language support (go to definition, references, hover, rename, diagnostics, completion,
code actions, formatting) served by the **RoslynSense shared daemon** — the same process and
the same loaded Roslyn solution your MCP-connected AI assistant (Claude Code etc.) uses.

Why: one solution load, one workspace. Your editor's unsaved buffers are visible to the
assistant's analysis tools, and the assistant's view is always in sync with what you see.

## Requirements

- The `roslyn-sense` dotnet tool on PATH:

  ```bash
  dotnet tool install -g roslyn-sense
  ```

  Or set `roslynSense.serverPath` to a locally built binary.

## How it works

The extension launches `roslyn-sense --lsp`, which connects to the per-solution shared daemon
(spawning it if needed) over a named pipe and proxies LSP through it. MCP clients connect to
the same daemon, so both share one `MSBuildWorkspace`. When no solution is found, the LSP
server runs standalone in-process.

## Debugging

Press F5. With no `launch.json`, the extension picks the launchable project (asking when there
is more than one), builds it, and starts a debug session — no Microsoft C# extension required.

The adapter is [netcoredbg](https://github.com/Samsung/netcoredbg) in its DAP mode, located on
PATH or downloaded on first use (override with `roslynSense.debuggerPath`). Because it is a real
debugger, you get watch expressions, expandable locals, conditional and function breakpoints,
exception filters (`all` / `user-unhandled`), and variable editing. Hit-count breakpoints,
logpoints, and data breakpoints are not supported by netcoredbg.

.NET Framework projects cannot be debugged this way — netcoredbg is CoreCLR-only. Ask the AI
assistant to start a debug session for those; it uses ICorDebug.

Separately, the `roslynsense-ai` debug type mirrors a debug session an AI chat owns, so you can
watch and control it in the normal debugger UI. See the debug bridge notes in the main README.

## Test Explorer

C# tests appear in the Testing view, grouped by project → class. Discovery runs against the
already-loaded solution (no separate `--list-tests` process) and refreshes when you save.
Run, Debug, and Coverage profiles are all available; Debug attaches to a suspended test host so
breakpoints in the first test are reliable, and Coverage paints the built-in gutters.

## Solution Explorer and packages

The RoslynSense activity-bar view shows the solution's logical structure — solution folders,
per-project Dependencies (Imports, target frameworks, Packages, Projects, Assemblies,
Analyzers), and files nested under the file they belong to (`Form1.cs` owns its designer and
resx). `Ctrl+F` filters the tree, `Ctrl+T` jumps to a node, and Show All Files reveals files
that are on disk but not in the project, dimmed.

"Manage NuGet Packages" opens a panel with Browse / Installed / Updates / Consolidate. Sources
come from your `NuGet.config` chain, so private feeds and credential providers work exactly as
they do on the command line; installs go through `dotnet add package`, which keeps Central
Package Management correct.

Build, rebuild, clean, test, and watch tasks are contributed per project and use the built-in
`$msCompile` problem matcher, so they work as `preLaunchTask` and with `Ctrl+Shift+B`.

## Editor context for AI chats

With `roslynSense.shareEditorContext` on (the default), the extension tells connected AI chats
which file and symbol you are looking at, your selection, and the diagnostics already visible —
so asking "why does this fail?" resolves to what is on your screen. It sends paths, the cursor,
the selection, and those diagnostics; never whole file contents. Turn it off in settings.

## Coexistence with the C# extension / C# Dev Kit

The Microsoft C# extension (`ms-dotnettools.csharp`) runs its own language server. Running
both gives you duplicated hovers/definitions and a second solution load. For the shared-solution
workflow, disable the Microsoft C# extension for the workspace (Extensions → C# → Disable
(Workspace)) and use RoslynSense instead — debugging and tests no longer depend on it.

## Settings

- `roslynSense.serverPath` — path to the `roslyn-sense` executable (default: `roslyn-sense`).
- `roslynSense.solutionPath` — explicit `.sln`; default resolves the nearest solution from the workspace folder.
- `roslynSense.debuggerPath` — path to netcoredbg; empty lets the server find or download it.
- `roslynSense.trace.server` — LSP trace for debugging.

## Building the extension

```bash
cd vscode-extension
npm install
npm run compile
npm run package   # produces the .vsix (requires @vscode/vsce)
```
