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

The connection-string portion can be a raw ADO.NET string *or* a reference to an existing config file so the LLM does not need the secret baked into the MCP config:

| Form | Meaning |
|------|---------|
| `xml:<path>#<name>` | `.NET Framework` shorthand — `/configuration/connectionStrings/add[@name='<name>']/@connectionString` |
| `xml:<path>#<xpath>` | Full XPath starting with `/` or `//`. Returns attribute value or element text. |
| `json:<path>#<name>` | `.NET Core` shorthand — `$.ConnectionStrings.<name>` |
| `json:<path>#$.a.b.c` | Dotted JSON path. |

Examples:

```json
"args": [
    "--db", "legacy=mssql:xml:./src/WebApp/web.config#SiteSqlServer",
    "--db", "core=mssql:json:./src/CoreApp/appsettings.json#Default",
    "--db", "custom=psql:json:./secrets.json#$.Databases.Primary.ConnStr"
]
```

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

#### Auto-discovery from project config files

At startup the server scans the working directory tree for `web.config`, `app.config`, and `appsettings*.json` files and registers any connection strings it finds. The alias is `ProjectName_ConnectionStringName` (project name comes from the nearest `*.csproj` walking up; non-alphanumerics are replaced with `_`). Aliases registered via explicit `--db` flags always win over auto-discovered ones with the same name.

**Development-first by design.** RoslynSense is a development-time tool, so giving an LLM easy access to a production database is the wrong default. The merge order is:

1. Base file (`appsettings.json`, `web.config`, `app.config`) — applied first.
2. Other environment-specific files — override the base.
3. Development-flavored files (`appsettings.Development.json`, `appsettings.Local.json`, `web.Debug.config`, `app.Debug.config`) — applied last, overriding everything else.

Production-flavored env names are **not loaded at all**: `Production`, `Prod`, `Live`, `Staging`, `Stage`, `Release`, `Publish`. (`Release` is the MSBuild configuration name applied by `dotnet publish -c Release`, almost always to inject prod settings — same risk as `Production`.) If you really need to register prod credentials, do it explicitly with `--db`.

`web.<env>.config` / `app.<env>.config` are XDT transform files but commonly carry the only real local-dev connection string, so they are parsed alongside the base. The `xdt:` namespace is ignored on attribute reads; `xdt:Transform="Remove"` / `RemoveAll` on either an `<add>` entry or the `<connectionStrings>` section is honored.

Files and entries that are **skipped** with a stderr warning:

- `appsettings.{template,example,sample,dist}.json` and `web.{template,example,sample,dist}.config` — non-runtime templates committed without secrets.
- `appsettings.Production.json`, `web.Production.config`, etc. — production env names (see above).
- `<connectionStrings configProtectionProvider="…">` — encrypted via `aspnet_regiis -pe`; the ciphertext is unusable at runtime.
- `<add xdt:Transform="Remove"/>` and `<connectionStrings xdt:Transform="RemoveAll"/>` inside transform files.
- Empty values and unfilled placeholders: `${VAR}`, `$(VAR)`, `{{VAR}}`, `#{VAR}`, `%VAR%`, `<your connection string>`.

The provider for each connection string is resolved in this order — first match wins:

1. `providerName` attribute on `<add>` (web.config) — e.g. `System.Data.SqlClient`, `Npgsql`, `System.Data.SQLite`.
2. Connection-string content — `Host=`/`Port=` → `psql`; `:memory:` / `Filename=` / `Data Source=*.db` → `sqlite`; `Server=` / `Integrated Security=` → `mssql`.
3. Connection-string name hint — anything containing `postgres`/`npgsql`/`psql` → `psql`; `sqlite` → `sqlite`; `sqlserver`/`mssql` → `mssql` (e.g. `SiteSqlServer`).
4. Project's referenced NuGet packages — a single `Npgsql*`, `*.Sqlite`, or `*.SqlClient` / `*.SqlServer` package on the nearest `.csproj` resolves the provider.
5. `web.config` default — `mssql`. (.NET Framework ships SqlClient in the BCL, so historically untyped `<connectionStrings>` entries meant SQL Server.)
6. Otherwise the entry is skipped and a warning is logged on stderr.

`bin`, `obj`, `node_modules`, `.git`, `.vs`, `.idea`, `packages`, `TestResults`, and other dotted directories are skipped. Disable the scan entirely with `--no-auto-db`, or disable the database tools altogether with `--no-db`.

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
