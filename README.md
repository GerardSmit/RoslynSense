# RoslynSense

A Model Context Protocol (MCP) server that provides C# code analysis, navigation, refactoring, testing, and debugging capabilities using the Roslyn compiler platform. Includes extensible support for WebForms (ASPX/ASCX) and Razor (.razor/.cshtml) files.

Inspired by [egorpavlikhin/roslyn-mcp](https://github.com/egorpavlikhin/roslyn-mcp).

## Install

Install from NuGet as a global .NET tool:

```
dotnet tool install --global RoslynSense
```

Update an existing install:

```
dotnet tool update --global RoslynSense
```

## MCP Configuration

```json
{
    "servers": {
        "RoslynSense": {
            "type": "stdio",
            "command": "roslyn-sense"
        }
    }
}
```

### Command-Line Options

| Flag | Description |
|------|-------------|
| `--no-webforms` | Disable WebForms (ASPX/ASCX) support. |
| `--no-razor` | Disable Razor (.razor/.cshtml) support. |

Example configuration with Razor disabled:

```json
{
    "servers": {
        "RoslynSense": {
            "type": "stdio",
            "command": "roslyn-sense",
            "args": ["--no-razor"]
        }
    }
}
```

## Tools

### Code Analysis

| Tool | Description |
|------|-------------|
| **GetRoslynDiagnostics** | Get diagnostics for a C# file, ASPX/ASCX file, Razor file, or entire project. Returns a compact markdown table with severity counts. Accepts a severity filter (error, warning, info, hidden, all). Supports multiple files separated by semicolons. |
| **GetCodeActions** | List available code fixes for a diagnostic. Optionally apply a fix by index. Also discovers refactorings (Extract Method, Introduce Variable, etc.). |

### Navigation

| Tool | Description |
|------|-------------|
| **GoToDefinition** | Navigate to a symbol's definition with code context, or auto-decompile referenced assembly symbols. For type definitions, shows a members table. Works with C#, ASPX, and Razor files. |
| **FindUsages** | Find all references to a symbol across a project. Also searches Razor source-generated files and ASPX inline code. |
| **SemanticSymbolSearch** | Ranked symbol search combining name, signature, docs, and source cues. Supports phrase-style queries (e.g. "calculate tax", "user repository"). |
| **FindImplementations** | Find all implementations of an interface, abstract class, or virtual/abstract member. |
| **GetCallHierarchy** | Show callers and/or callees of a method or property. |
| **GetTypeHierarchy** | Show the full type hierarchy (base classes, interfaces, derived types). |

### Structure

| Tool | Description |
|------|-------------|
| **GetProjectStructure** | Get an overview of a project: target framework, references, source files, and types by namespace. |
| **GetFileOutline** | Get a compact outline of a C#, ASPX, or Razor file with namespaces, types, members, and line ranges (start-end for multi-line members). Supports multiple files separated by semicolons. |
| **ListProjects** | Discover all projects loaded in the workspace. |

### Build

| Tool | Description |
|------|-------------|
| **BuildProject** | Build a .NET project or solution and return structured errors and warnings. Set `background: true` to build in the background. |

### Refactoring

| Tool | Description |
|------|-------------|
| **RenameSymbol** | Rename a symbol and all references across the project, including ASPX/ASCX and Razor files. Supports dry-run preview and file renames. |

### Testing & Coverage

| Tool | Description |
|------|-------------|
| **RunTests** | Run tests in a .NET test project with optional filter expression and timeout. Set `background: true` to run in the background (builds first, then tests). |
| **DiscoverTests** | Discover all test methods in a project using static Roslyn analysis. Returns test names, frameworks, file paths, and line numbers. |
| **FindTests** | Find test methods that reference a symbol. Optionally uses coverage data for runtime-accurate results. |
| **RunCoverage** | Collect code coverage for a test project using coverlet. Caches results for querying. Set `background: true` for background collection. |
| **GetCoverage** | Query coverage by project, file, class, or method. Shows line and branch coverage with uncovered lines. |

### Debugging

Debugging uses [netcoredbg](https://github.com/Samsung/netcoredbg), which is auto-provisioned on first use.

| Tool | Description |
|------|-------------|
| **DebugStartTest** | Start debugging a .NET test project. Builds, launches the test host, and attaches the debugger. |
| **DebugAttach** | Attach the debugger to a running .NET process by PID. |
| **DebugSetBreakpoint** | Set a breakpoint at a file and line. Supports conditions and batch mode. |
| **DebugRemoveBreakpoint** | Remove a breakpoint by ID. Supports batch removal. |
| **DebugContinue** | Continue, step in, step over, or step out. |
| **DebugRunUntil** | Run until a specific location, with optional condition. |
| **DebugEvaluate** | Evaluate expressions in the current debug context. Supports batch evaluation with semicolons. |
| **DebugStatus** | Get debugger status, breakpoints, and current pause position with optional locals and stack trace. |
| **DebugStop** | Stop the debug session and clean up. |

### Profiling

Profiling uses [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) with CPU sampling, auto-provisioned on first use.

| Tool | Description |
|------|-------------|
| **ProfileTests** | Profile a test project to find CPU hotspots. Returns top methods ranked by self-time. |
| **ProfileApp** | Profile a .NET application for a specified duration. |
| **ListProfilingSessions** | List recent profiling sessions with IDs for investigation. |
| **ProfileSearchMethods** | Search profiled methods by name pattern. |
| **ProfileCallers** | Show which methods called a given method and how much time was spent. |
| **ProfileCallees** | Show which methods a given method calls and how much time was spent. |
| **ProfileHotPaths** | Show the hottest call paths through a method. |

### Background Tasks

| Tool | Description |
|------|-------------|
| **GetBackgroundTaskResult** | Check status and results of a background task by task ID. |
| **ListBackgroundTasks** | List all background tasks with their statuses. |

## Resources

| Resource | URI Pattern | Description |
|----------|-------------|-------------|
| **project-structure** | `roslyn://project-structure/{filePath}` | Project file/folder structure grouped by directory. |
| **file-outline** | `roslyn://file-outline/{filePath}` | Structural outline of a C# file (same as GetFileOutline tool). |

## Prompts

| Prompt | Description |
|--------|-------------|
| **validate-after-edit** | Step-by-step instructions to validate a C# file after editing. |
| **investigate-symbol** | Multi-step investigation workflow for a symbol. |

## Markup Snippet Convention

Many tools use a `markupSnippet` parameter with `[| |]` delimiters to identify a target symbol:

```
var x = [|Foo|].Bar();          // targets Foo
public interface [|IService|]    // targets IService
void [|ProcessData|](int x)     // targets ProcessData
```

The snippet is matched against the file content. Whitespace differences are tolerated.
