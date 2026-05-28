# RoslynSense

A Model Context Protocol (MCP) server that provides C# code analysis, navigation, refactoring, testing, and debugging capabilities using the Roslyn compiler platform. Includes extensible support for WebForms (ASPX/ASCX) and Razor (.razor/.cshtml) files.

Inspired by [egorpavlikhin/roslyn-mcp](https://github.com/egorpavlikhin/roslyn-mcp).

## Install

### Step 1 — Install the .NET tool

```
dotnet tool install --global RoslynSense
```

Update an existing install:

```
dotnet tool update --global RoslynSense
```

### Step 2 — Configure your agent

<details>
<summary>Claude Code</summary>

Via the plugin marketplace (recommended):

```bash
claude plugin marketplace add GerardSmit/RoslynSense && claude plugin install roslyn-sense@roslyn-sense
```

Or add the MCP server directly (project-scoped):

```bash
claude mcp add RoslynSense --transport stdio -- roslyn-sense
```

For a global installation available in all projects, add `--scope user`:

```bash
claude mcp add --scope user RoslynSense --transport stdio -- roslyn-sense
```

</details>

<details>
<summary>Cursor</summary>

Add to `.cursor/mcp.json` in your project root (or `~/.cursor/mcp.json` for global):

```json
{
    "mcpServers": {
        "RoslynSense": {
            "command": "roslyn-sense"
        }
    }
}
```

</details>

<details>
<summary>Windsurf</summary>

Add to `~/.codeium/windsurf/mcp_config.json`:

```json
{
    "mcpServers": {
        "RoslynSense": {
            "command": "roslyn-sense"
        }
    }
}
```

</details>

<details>
<summary>VS Code (Cline / Continue / Copilot)</summary>

Add to `.vscode/mcp.json` in your project root, or open the command palette and run **MCP: Open User Configuration** to configure it globally:

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

</details>

<details>
<summary>Visual Studio</summary>

Add to `.mcp.json` in your solution root (committed to source control) or `%USERPROFILE%\.mcp.json` for a global configuration:

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

</details>

<details>
<summary>GitHub Copilot CLI</summary>

For a global installation, run `/mcp add` inside Copilot CLI and fill in the interactive form (stores to `~/.copilot/mcp-config.json`). Select **Local/STDIO** as the server type and use `roslyn-sense` as the command.

For a project-scoped configuration, add to `.mcp.json` in your project root (v1.0.22+):

```json
{
    "mcpServers": {
        "RoslynSense": {
            "command": "roslyn-sense"
        }
    }
}
```

</details>

<details>
<summary>Kiro</summary>

Add to `.kiro/settings/mcp.json` in your project root (or `~/.kiro/settings/mcp.json` for global):

```json
{
    "mcpServers": {
        "RoslynSense": {
            "command": "roslyn-sense",
            "args": []
        }
    }
}
```

</details>

<details>
<summary>Other MCP clients</summary>

Use the following server configuration:

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

</details>

### Command-Line Options

| Flag | Description |
|------|-------------|
| `--no-webforms` | Disable WebForms (ASPX/ASCX) support. |
| `--no-razor` | Disable Razor (.razor/.cshtml) support. |
| `--no-debugger` | Disable all debugger tools (see [Debugging](#debugging)). |
| `--no-profiling` | Disable all profiling tools (see [Profiling](#profiling)). |
| `--toon` | Use TOON (Token-Optimized Object Notation) output format instead of markdown. Reduces token usage. |
| `--db <alias>=<provider>:<connstr>` | Register a database connection. Repeatable. Providers: `psql`, `mssql`, `sqlite`. See [Databases](#databases). |
| `--no-db` | Disable all database tools. |
| `--no-auto-db` | Disable auto-discovery of connection strings from `web.config` and `appsettings*.json`. See [Databases](#databases). |
| `--no-preload` | Disable background workspace preloading on startup. |

Example with Razor disabled:

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

### Configuration file (`roslynsense.json`)

Drop a `roslynsense.json` next to your solution (or anywhere up the tree from where the server is launched) to configure RoslynSense per-project without editing every MCP client. The server walks up from the working directory and uses the **nearest** file found.

```json
{
    "tools": {
        "webForms": true,
        "razor": true,
        "debugger": true,
        "profiling": true,
        "database": true
    },
    "database": {
        "autoDiscovery": null,
        "connections": {
            "myapp": "psql:Host=localhost;Database=myapp;Username=dev;Password=dev",
            "reports": {
                "provider": "mssql",
                "connectionString": "Server=.;Database=Reports;Integrated Security=true"
            }
        }
    },
    "tableFormat": "toon",
    "preload": ["./MySolution.sln"]
}
```

**Precedence: CLI flag > config file > default.** Per-field for booleans, per-alias for connections.

<details>
<summary>Field reference</summary>

| Path | Type | Default | Equivalent CLI flag |
|------|------|---------|---------------------|
| `tools.webForms` | bool | `true` | `--no-webforms` forces `false` |
| `tools.razor` | bool | `true` | `--no-razor` forces `false` |
| `tools.debugger` | bool | `true` | `--no-debugger` forces `false` |
| `tools.profiling` | bool | `true` | `--no-profiling` forces `false` |
| `tools.database` | bool | `true` | `--no-db` forces `false` |
| `database.autoDiscovery` | bool? | `null` | `--no-auto-db` forces `false` |
| `database.connections` | object | `{}` | `--db` overrides matching alias |
| `tableFormat` | string? | `null` | `--toon` forces `"toon"` |
| `preload` | string[]? | `null` | `--no-preload` forces `[]` |
| `sharedHost` | bool? | `true` | env `ROSLYNMCP_SHARED_HOST=0` forces off |
| `hostIdleMinutes` | int? | `30` | env `ROSLYNMCP_HOST_IDLE_MINUTES` |
| `maxWorkspaces` | int? | `4` | env `ROSLYNMCP_MAX_WORKSPACES` |

**`preload` semantics:**

- `null` (default) — auto-discovers the first `.sln`/`.slnx` in the working directory and preloads all its projects in the background on startup.
- `["path1.sln", "path2.csproj"]` — preloads exactly the listed solution and/or project files.
- `[]` — disables preloading entirely.

**`autoDiscovery` semantics:**

- `null` (default) — auto-discovery runs **only when no explicit registrations exist** (CLI `--db` or config `connections`).
- `true` — auto-discovery always runs in addition to explicit registrations. Explicit aliases still win on conflict.
- `false` — auto-discovery skipped entirely.

</details>

<details>
<summary>Connection entry formats</summary>

Two equivalent forms — pick whichever reads better. The string form mirrors `--db <provider>:<connstr>` shorthand.

**Shorthand string:**

```json
"connections": {
    "myapp": "psql:Host=localhost;Database=myapp;Username=u;Password=p"
}
```

**Object form:**

```json
"connections": {
    "reports": {
        "provider": "mssql",
        "connectionString": "Server=.;Database=Reports;Integrated Security=true"
    }
}
```

The connection-string portion accepts the same `xml:` / `json:` indirection and `${gitRoot}` / `${solutionRoot}` / `${env:NAME}` placeholders documented under [Referencing connection strings from config files](#referencing-connection-strings-from-config-files).

</details>

<details>
<summary>Loader behavior</summary>

- Walks up from `Directory.GetCurrentDirectory()` to the filesystem root, stopping at the **first** `roslynsense.json` found.
- Lenient JSON: line/block comments, trailing commas, and unknown properties are accepted. Unknown properties are silently ignored for forward compatibility.
- Invalid JSON is logged to stderr; the server starts with defaults.
- Per-connection parse failures (unknown provider, empty value) are logged as warnings and the entry is skipped.

</details>

### Shared host (across chats)

Each MCP client (each chat) normally spawns its own `roslyn-sense` process, so N chats on the same solution load that solution N times. To avoid that, RoslynSense runs a **single shared host process per solution**: thin client processes forward tool calls and resource reads to it over a named pipe, so the solution is loaded once and shared.

- **On by default.** Disable with `ROSLYNMCP_SHARED_HOST=0` (or `"sharedHost": false`), e.g. for debugging — clients then run everything in-process.
- **Automatic fallback.** If the host can't be reached or an IPC call fails, the client transparently runs the call in-process; a host problem never breaks a chat.
- **Scope.** Only applies when the working directory belongs to a multi-project solution; loose `.csproj` use stays in-process.
- **Lifecycle.** The host shuts down after `ROSLYNMCP_HOST_IDLE_MINUTES` (default 30) with no connected clients, and respawns on the next call. Logs go to `%TEMP%/roslyn-mcp-daemon/<hash>/host.log`.

In-process memory is also bounded independently of the host:

- **One workspace per solution** (not per project): opening any project of a solution opens the whole solution once. `ROSLYNMCP_MAX_WORKSPACES` (default 4) caps cached solution workspaces (LRU), on top of the 10-minute idle eviction.

| Env var | Default | Effect |
|---------|---------|--------|
| `ROSLYNMCP_SHARED_HOST` | `1` | `0`/`false`/`off` disables the shared host (pure in-process). |
| `ROSLYNMCP_HOST_IDLE_MINUTES` | `30` | Idle minutes before a clientless host exits. |
| `ROSLYNMCP_MAX_WORKSPACES` | `4` | Max cached solution workspaces (LRU bound). |
| `ROSLYNMCP_INDEX_IDLE_TIMEOUT_SECONDS` | `600` | Idle eviction for ASPX/Razor project-index caches. |
| `ROSLYNMCP_OPEN_PROJECT_TIMEOUT_SECONDS` | `300` | Ceiling on a single project/solution open. |

**Debugging is per-chat.** Debug tools are *not* forwarded to the shared host — they run in-process in each client, so every chat has its own independent debug session (`netcoredbg` subprocess). Multiple chats can debug the same solution at once without colliding. (Trade-off: a debug session loads its own workspace in the client process, but debugging is interactive and infrequent.)

**Single host guarantee.** Exactly one host serves a solution at a time: a daemon acquires an exclusive lock file before it begins listening, so a daemon that loses a startup race exits without serving; the OS releases the lock on process death, so a crash self-heals and the next call respawns.

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
| **ListSourceGeneratedFiles** | List all source-generated files in a project, grouped by generator. |
| **GetSourceGeneratedFileContent** | View the content of a specific source-generated file by hint name. |

### Build

| Tool | Description |
|------|-------------|
| **BuildProject** | Build a .NET project or solution and return structured errors and warnings. Warnings are grouped by code with counts. Set `background: true` to build in the background. |
| **GetBuildWarnings** | Retrieve all warnings for a specific warning code (e.g. `CS0414`) from the last build. Returns each warning's file, line, and message. `projectPath` defaults to the last built project. |

### Refactoring

| Tool | Description |
|------|-------------|
| **RenameSymbol** | Rename a symbol and all references across the project, including ASPX/ASCX and Razor files. Supports dry-run preview and file renames. |
| **ExpandVarTypes** | Return a method's source with all `var` declarations replaced by their resolved explicit types. Use as a first step to understand what types a method works with — reveals return types, collection element types, and destructured results without chasing each call individually. Read-only; supports `hintLine` for overload disambiguation. |

### Testing & Coverage

| Tool | Description |
|------|-------------|
| **RunTests** | Run tests in a .NET test project with optional filter expression and timeout. Set `background: true` to run in the background (builds first, then tests). |
| **DiscoverTests** | Discover all test methods in a project using static Roslyn analysis. Returns test names, frameworks, file paths, and line numbers. |
| **FindTests** | Find test methods that reference a symbol. Optionally uses coverage data for runtime-accurate results. |
| **RunCoverage** | Collect code coverage for a test project using coverlet. Caches results for querying. Set `background: true` for background collection. |
| **GetCoverage** | Query coverage by project, file, class, or method. Shows line and branch coverage with uncovered lines. |
| **GetMethodCoverage** | Get per-line coverage detail for a specific method. Shows every executable line with hit count and source code. Lines marked with `!` have partial branch coverage. |

### Debugging

Debugging uses [netcoredbg](https://github.com/Samsung/netcoredbg), which is auto-provisioned on first use. Disable with `--no-debugger`.

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

Profiling uses [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) with CPU sampling, auto-provisioned on first use. Disable with `--no-profiling`.

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

### Databases

Query configured databases directly from the LLM without needing `psql`, `sqlcmd`, or `sqlite3` installed. Connections are registered at server startup via `--db` (see [Command-Line Options](#command-line-options)). Connections are writable by default.

| Tool | Description |
|------|-------------|
| **DbQuery** | Run a SELECT query on a configured connection. Returns results as a table. Supports parameterized queries via a JSON object. |
| **DbExecute** | Run a non-query SQL statement (INSERT/UPDATE/DELETE/DDL). Returns affected rows. |
| **DbListConnections** | List all configured database connections. |
| **DbListTables** | List tables and views, optionally filtered by schema. |
| **DbDescribeTable** | Show columns, types, nullability, and defaults for a table. |

Example configuration with all three providers:

```json
{
    "servers": {
        "RoslynSense": {
            "type": "stdio",
            "command": "roslyn-sense",
            "args": [
                "--db", "prod=psql:Host=db.example.com;Database=app;Username=app;Password=s3cr3t",
                "--db", "local=sqlite:C:\\dev\\app.db",
                "--db", "reports=mssql:Server=(local);Database=Reporting;Integrated Security=true"
            ]
        }
    }
}
```

Provider tokens: `psql` / `postgres` / `postgresql`, `mssql` / `sqlserver` / `sql`, `sqlite`. Alias prefix is optional (defaults to the canonical provider name).

#### Referencing connection strings from config files

The connection-string portion can be a raw ADO.NET string *or* a reference to an existing config file so the LLM does not need the secret baked into the MCP config.

```json
"args": [
    "--db", "legacy=mssql:xml:./src/WebApp/web.config#SiteSqlServer",
    "--db", "core=mssql:json:./src/CoreApp/appsettings.json#Default",
    "--db", "custom=psql:json:./secrets.json#$.Databases.Primary.ConnStr"
]
```

<details>
<summary>Reference forms and path placeholders</summary>

| Form | Meaning |
|------|---------|
| `xml:<path>#<name>` | `.NET Framework` shorthand — `/configuration/connectionStrings/add[@name='<name>']/@connectionString` |
| `xml:<path>#<xpath>` | Full XPath starting with `/` or `//`. Returns attribute value or element text. |
| `json:<path>#<name>` | `.NET Core` shorthand — `$.ConnectionStrings.<name>` |
| `json:<path>#$.a.b.c` | Dotted JSON path. |

The delimiter between path and query is always `#`. Paths support the following placeholders so config-file references stay portable across machines / CI / committed `.mcp.json`:

| Placeholder | Resolves to |
|-------------|-------------|
| `${gitRoot}` | Nearest ancestor directory containing `.git`. |
| `${solutionRoot}` | Nearest ancestor directory containing `*.sln` or `*.slnx`. |
| `${env:NAME}` | Environment variable `NAME`. |

Example committed to Git:

```json
"args": [
    "--db", "legacy=mssql:xml:${gitRoot}/src/WebApp/web.config#SiteSqlServer",
    "--db", "core=mssql:json:${solutionRoot}/src/CoreApp/appsettings.json#Default"
]
```

Plain relative paths (no placeholder) resolve in this order: CWD → solutionRoot → gitRoot. First existing file wins. This lets a committed `.mcp.json` work on any contributor's machine regardless of where Claude was launched, without requiring a placeholder.

</details>

#### Auto-discovery from project config files

At startup the server scans the working directory tree for `web.config`, `app.config`, and `appsettings*.json` files and registers any connection strings it finds. The alias is `ProjectName_ConnectionStringName` (project name comes from the nearest `*.csproj` walking up; non-alphanumerics are replaced with `_`). Explicit `--db` flags and `roslynsense.json` `connections` always win over auto-discovered aliases with the same name.

Disable the scan entirely with `--no-auto-db` (or `database.autoDiscovery: false` in `roslynsense.json`), or disable the database tools altogether with `--no-db`.

<details>
<summary>Development-first merge order and skipped files</summary>

**Development-first by design.** RoslynSense is a development-time tool, so giving an LLM easy access to a production database is the wrong default. The merge order is:

1. Base file (`appsettings.json`, `web.config`, `app.config`) — applied first.
2. Other environment-specific files — override the base.
3. Development-flavored files (`appsettings.Development.json`, `appsettings.Local.json`, `web.Debug.config`, `app.Debug.config`) — applied last, overriding everything else.

Production-flavored env names are **not loaded at all**: `Production`, `Prod`, `Live`, `Staging`, `Stage`, `Release`, `Publish`. (`Release` is the MSBuild configuration name applied by `dotnet publish -c Release`, almost always to inject prod settings — same risk as `Production`.) If you really need to register prod credentials, do it explicitly with `--db` or `roslynsense.json`.

`web.<env>.config` / `app.<env>.config` are XDT transform files but commonly carry the only real local-dev connection string, so they are parsed alongside the base. The `xdt:` namespace is ignored on attribute reads; `xdt:Transform="Remove"` / `RemoveAll` on either an `<add>` entry or the `<connectionStrings>` section is honored.

Files and entries that are **skipped** with a stderr warning:

- `appsettings.{template,example,sample,dist}.json` and `web.{template,example,sample,dist}.config` — non-runtime templates committed without secrets.
- `appsettings.Production.json`, `web.Production.config`, etc. — production env names (see above).
- `<connectionStrings configProtectionProvider="…">` — encrypted via `aspnet_regiis -pe`; the ciphertext is unusable at runtime.
- `<add xdt:Transform="Remove"/>` and `<connectionStrings xdt:Transform="RemoveAll"/>` inside transform files.
- Empty values and unfilled placeholders: `${VAR}`, `$(VAR)`, `{{VAR}}`, `#{VAR}`, `%VAR%`, `<your connection string>`.

`bin`, `obj`, `node_modules`, `.git`, `.vs`, `.idea`, `packages`, `TestResults`, and other dotted directories are skipped.

</details>

<details>
<summary>Provider resolution order</summary>

The provider for each connection string is resolved in this order — first match wins:

1. `providerName` attribute on `<add>` (web.config) — e.g. `System.Data.SqlClient`, `Npgsql`, `System.Data.SQLite`.
2. Connection-string content — `Host=`/`Port=` → `psql`; `:memory:` / `Filename=` / `Data Source=*.db` → `sqlite`; `Server=` / `Integrated Security=` → `mssql`.
3. Connection-string name hint — anything containing `postgres`/`npgsql`/`psql` → `psql`; `sqlite` → `sqlite`; `sqlserver`/`mssql` → `mssql` (e.g. `SiteSqlServer`).
4. Project's referenced NuGet packages — a single `Npgsql*`, `*.Sqlite`, or `*.SqlClient` / `*.SqlServer` package on the nearest `.csproj` resolves the provider.
5. `web.config` default — `mssql`. (.NET Framework ships SqlClient in the BCL, so historically untyped `<connectionStrings>` entries meant SQL Server.)
6. Otherwise the entry is skipped and a warning is logged on stderr.

</details>

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
