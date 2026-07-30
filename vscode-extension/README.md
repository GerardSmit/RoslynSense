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

## Coexistence with the C# extension / C# Dev Kit

The Microsoft C# extension (`ms-dotnettools.csharp`) runs its own language server. Running
both gives you duplicated hovers/definitions and a second solution load. For the shared-solution
workflow, disable the Microsoft C# extension for the workspace (Extensions → C# → Disable
(Workspace)) and use RoslynSense instead.

## Settings

- `roslynSense.serverPath` — path to the `roslyn-sense` executable (default: `roslyn-sense`).
- `roslynSense.solutionPath` — explicit `.sln`; default resolves the nearest solution from the workspace folder.
- `roslynSense.trace.server` — LSP trace for debugging.

## Building the extension

```bash
cd vscode-extension
npm install
npm run compile
npm run package   # produces the .vsix (requires @vscode/vsce)
```
