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
