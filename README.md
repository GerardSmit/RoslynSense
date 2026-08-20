# RoslynSense

A Model Context Protocol (MCP) server that provides C# code analysis, navigation, refactoring, testing, and debugging capabilities using the Roslyn compiler platform. Includes extensible support for WebForms (ASPX/ASCX), Razor (.razor/.cshtml), Protobuf (.proto) and LINQ to SQL (.dbml) files.

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
| `--no-proto` | Disable Protobuf/gRPC (.proto) support: no navigation between a `.proto` and the C# protoc generated from it. |
| `--no-resources` | Disable `.resx` support: no resource catalog, no key navigation. |
| `--no-dbml` | Disable LINQ to SQL (`.dbml`) support: no navigation between a model and the C# SqlMetal generated from it, no reference counts, no table refresh. |
| `--no-appsettings` | Disable `appsettings*.json` support: no reference counts, override or external-reference lenses, no navigation between a key and the code reading it. |
| `--no-webconfig` | Disable `web.config` / `app.config` settings support: the same for `<appSettings>` and `<connectionStrings>`. Binding-redirect checking — warnings, fixes, the lens and the hover — is unaffected. |
| `--no-logging` | Disable logging message templates: no placeholder colouring, completion, hover or template diagnostics. |
| `--no-debugger` | Disable all debugger tools (see [Debugging](#debugging)). |
| `--no-debugger-display` | Show values as their type name rather than through `[DebuggerDisplay]`. See [Debugging](#debugging). |
| `--no-type-proxy` | Expand values as their own fields rather than through `[DebuggerTypeProxy]`. |
| `--no-debugger-browsable` | List every field, ignoring `[DebuggerBrowsable]`. |
| `--no-raw-view` | Omit the **Raw View** child that a proxy or a hidden member would otherwise add. |
| `--no-just-my-code` | Step into `[DebuggerStepThrough]` code, framework modules, and frames with no symbols. |
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

Drop a `roslynsense.json` next to your solution (or anywhere up the tree from where the server is launched) to configure RoslynSense per-project without editing every MCP client.

```json
{
    "tools": {
        "webForms": true,
        "razor": true,
        "proto": true,
        "resources": true,
        "dbml": true,
        "debugger": true,
        "profiling": true,
        "database": true
    },
    "resources": {
        "preset": "dnn"
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

#### Settings layers

More than one file can apply at once. They are merged **per field**, weakest first, so a setting is only overridden by a layer that actually names it:

| # | Scope | File | For |
|---|-------|------|-----|
| 1 | Global | `~/.roslynsense/roslynsense.json` | What you want on every machine-wide checkout. |
| 2 | Repository | `<dir>/roslynsense.json`, from the filesystem root down to the working directory | The team's settings, committed with the code. |
| 3 | Repository, personal | `<dir>/roslynsense.local.json`, beside each of the above | Your overrides for this checkout. **Gitignore it.** |
| 4 | Personal, out of repo | `~/.roslynsense/projects/<mangled-path>/roslynsense.json` | The same, for a checkout you would rather not write into. |

Later rows win. CLI flags and environment variables still win over all of them.

`<mangled-path>` is the working directory flattened to one segment — every character that is not a letter or a digit becomes a dash — followed by eight hex digits of the path, so `D:\Sources\RoslynSense` becomes `D--Sources-RoslynSense-1f0c2a9b`. The flattened half is lossy on purpose (it is there to be read); the hash is what keeps two checkouts apart. Set `ROSLYNSENSE_HOME` to move `~/.roslynsense` somewhere else.

In VS Code, **RoslynSense: Settings** — from the command palette, or the `⋯` menu at the top of the Solution Explorer — edits all four scopes with a form and shows which layer each effective value comes from.

<details>
<summary>Field reference</summary>

| Path | Type | Default | Equivalent CLI flag |
|------|------|---------|---------------------|
| `tools.webForms` | bool | `true` | `--no-webforms` forces `false` |
| `tools.razor` | bool | `true` | `--no-razor` forces `false` |
| `tools.proto` | bool | `true` | `--no-proto` forces `false` |
| `tools.resources` | bool | `true` | `--no-resources` forces `false` |
| `tools.dbml` | bool | `true` | `--no-dbml` forces `false` |
| `tools.appSettings` | bool | `true` | `--no-appsettings` forces `false` |
| `tools.webConfig` | bool | `true` | `--no-webconfig` forces `false` |
| `webConfig.additionalFiles` | string[]? | `null` | — (extra file names the `web.config` pack claims) |
| `tools.logging` | bool | `true` | `--no-logging` forces `false` |
| `logging.templateSyntax` | bool | `true` | — (LOG0001, a malformed template) |
| `logging.unknownPlaceholder` | bool | `true` | — (LOG0002, a `[LoggerMessage]` placeholder matching no parameter; SYSLIB1014's claim) |
| `logging.valueCount` | bool | `true` | — (LOG0003, placeholders and values disagreeing in count) |
| `logging.unusedValue` | bool | `true` | — (LOG0004, a value no placeholder prints; SYSLIB1015's claim) |
| `logging.exceptionPosition` | bool | `true` | — (LOG0005, an exception passed as a value rather than first) |
| `tools.debugger` | bool | `true` | `--no-debugger` forces `false` |
| `tools.profiling` | bool | `true` | `--no-profiling` forces `false` |
| `tools.database` | bool | `true` | `--no-db` forces `false` |
| `resources.preset` | string? | `null` | — (`webforms`, `dnn`, `dotnet`, `none`; omitted merges all three) |
| `resources.include` / `.exclude` | string[] | `[]` | — (globs, discovery only) |
| `resources.overrides` / `.conventions` / `.lookups` | object[] | preset | — (raw escape hatch; prefer `preset`) |
| `resources.markupBindings` | string[] | preset | — (key shapes composed from a markup attribute, such as `Header[Control.UniqueName].Text`) |
| `resources.missingKeyDiagnostic` | bool | `false` | — (reports a key no file of its family declares; only where the resource file is known for certain) |
| `database.autoDiscovery` | bool? | `null` | `--no-auto-db` forces `false` |
| `database.connections` | object | `{}` | `--db` overrides matching alias |
| `tableFormat` | string? | `null` | `--toon` forces `"toon"` |
| `preload` | string[]? | `null` | `--no-preload` forces `[]` |
| `sharedHost` | bool? | `true` | env `ROSLYNMCP_SHARED_HOST=0` forces off |
| `hostIdleMinutes` | int? | `30` | env `ROSLYNMCP_HOST_IDLE_MINUTES` |
| `maxWorkspaces` | int? | `4` | env `ROSLYNMCP_MAX_WORKSPACES` |

**`webConfig.additionalFiles` semantics:**

The `web.config` pack claims `web.config` and `app.config` by exact name — never the `.config` extension, which would take `packages.config` and `nuget.config` with it, and never the `Web.Release.config` XDT transforms, whose `<add>` elements are edits to apply at publish rather than settings that exist.

Some frameworks invent their own name for the same thing. DotNetNuke keeps a `release.config` and a `development.config` beside its `web.config` — each a whole `<configuration>` document, not a transform — and its installer copies one of them over `web.config`. Name those files here and they get the same completion, hover, go-to-definition and find-usages as `web.config` itself:

```json
{
    "webConfig": {
        "additionalFiles": ["release.config", "development.config"]
    }
}
```

- File names only, matched case-insensitively. A path or a glob is rejected with a warning, as are `packages.config` and `nuget.config`.
- The extra files answer for themselves and **do not join the override chain** — that chain is what a nested `web.config` in a subdirectory builds, and a differently-named sibling is not a nearer version of it.
- They are **not** scanned for connection strings by database auto-discovery. A `release.config` usually carries production credentials; register those explicitly with `--db` if you really want them.

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
<summary>Resource presets</summary>

`.resx` support answers "which resource files does this key live in?" — for F12, hover and rename on a key in C#, in `<%$ Resources: … %>` and in `meta:resourcekey`. To do that it has to know which call shapes carry a resource key and how a call site turns into a resx base name. A **preset** is one shipped answer to both.

| Preset | Covers |
|--------|--------|
| `webforms` | Stock ASP.NET: `App_LocalResources` beside the page, `App_GlobalResources` at the app root, and the `Get*ResourceObject` methods that read them. |
| `dnn` | DotNetNuke: the seven `Localization.GetString` overloads (told apart by parameter type — three of them take two arguments and only one carries a root), `PortalModuleBase.LocalizeText` / `LocalizeString`, the implicit `.Text` key suffix, and the `local` → `localShared` → `global` fallback chain DNN itself walks. |
| `dotnet` | Modern .NET: `IStringLocalizer<T>` and the `ResourceManager` a `*.Designer.cs` wraps. |
| `none` | Nothing but what `roslynsense.json` declares itself. |

**Omitting `preset` merges all three, which is the recommended setting.** Every built-in lookup names a fully-qualified containing type, so the DNN set is inert in a solution with no DNN reference and the `IStringLocalizer` set is inert in a WebForms one — an unused preset costs one failed metadata lookup per containing type, once per compilation. Name a preset when you want to be explicit, or to keep another one's conventions from being offered.

`overrides` is not part of a preset: the `.Portal-{id}` and `.Host` customization files DNN puts beside a base `.resx` are recognised whichever preset is in force, because grouping a directory's file names happens before any lookup does. The defaults are `Portal-*` at rank 2 and `Host` at rank 1 — explicit ranks, because sorting those two patterns alphabetically gets the precedence backwards.

`conventions`, `lookups`, `markupBindings` and `overrides` layer on top of the preset, each with its own merge rule: lookups append, conventions merge by `id`, markup bindings append and dedupe, and overrides replace the preset's set wholesale (a rank scheme only means anything as a whole). A malformed entry is warned about and dropped rather than failing the load. The field shapes and the design behind them are in [`RoslynMCP/Languages/README.md`](RoslynMCP/Languages/README.md#resources-the-one-pack-whose-model-needs-explaining).

`--no-resources` (or `"tools": { "resources": false }`) removes the whole feature from the daemon: the pack is never registered, no catalog is built, and every resource-key feature on both the MCP and LSP surfaces stops answering. Editors can also switch it off per window with `roslynSense.languages.resx`, which leaves the daemon — and any AI session attached to it — untouched.

</details>

<details>
<summary>LINQ to SQL (<code>.dbml</code>)</summary>

A `.dbml` is the model and `Foo.designer.cs` is SqlMetal's re-emission of it, so F12 on a generated entity property lands on the `<Column>` rather than in a file the next regeneration overwrites — and the designer's own line is withdrawn, so it is a jump and not a picker. Shift+F12 does the same, which is the point: the two features cannot disagree about which file is generated.

Everything is joined by the anchors SqlMetal left in its own output — `[Table(Name=…)]`, `[Column(Name=…)]`, `[Association(Name=…)]`, `[Function(Name=…)]` — so no name is predicted. The path from `Foo.designer.cs` back to `Foo.dbml` is only ever a *candidate*: LINQ to SQL replaces the extension where WebForms appends to it, so the same spelling is what a `.resx` and a `.settings` produce. The binding is what confirms it, and nothing is withdrawn from F12 on the strength of a path.

Inside the file: an outline, hover showing the database half and the generated C# signature together, F12 into the generated member, Shift+F12 to every call site, and an **N references** lens over each table, type, column, association and function — counting call sites with the designer's own five or six mentions of each property excluded. The counts are absent rather than zero when the project has not been built, and an informational diagnostic on the root says why.

**Refresh table** re-syncs one `<Table>` against a registered RoslynSense connection: columns added and updated, foreign keys generated as `<Association>` pairs, and removals confirmed modally before anything is deleted. The connection is picked from RoslynSense's own registered connections — the model's `<Connection>` element is deliberately never used, since it commonly names a machine that no longer exists. The file is written to disk first so the watcher regenerates the designer from it, and a dirty buffer that differs from disk is refused rather than clobbered.

`--no-dbml` (or `"tools": { "dbml": false }`) removes the pack from the daemon; editors can switch it off per window with `roslynSense.languages.dbml`.

</details>

<details>
<summary>Configuration files (<code>appsettings*.json</code>, <code>web.config</code>, <code>app.config</code>)</summary>

A settings file is the one place in a solution where a name is written down with nothing checking that anything still reads it, and nothing checking that everything read is written down. Both packs answer those two questions in the file itself.

**Over every key, an `N references` lens.** For `appsettings.json` that counts the literal reads (`config["Widget:Retries"]`, `GetValue<int>`, `GetSection`, `GetConnectionString`) and, where a section is bound to an options type, the references to the property the key becomes — so a key inside a bound section reports how often that property is used, not how often the section is bound. For `web.config` and `app.config` it counts `ConfigurationManager.AppSettings["…"]`, `WebConfigurationManager`, `ConnectionStrings["…"]`, and the `<%$ AppSettings: … %>` expressions in markup. A zero is the point of the feature: keys outlive the code that read them, and there is otherwise no way to see it.

**The reads a codebase wrapped in a method of its own count as reads.** Almost every long-lived solution has one — `Config.GetSetting("Timeout")` over a `ConfigurationManager.AppSettings[setting]`, or a `GetSetting` over `IConfiguration`, written once for a default value or a log line and called everywhere after that. Nothing at those call sites names a configuration API, so a scan that knows only the framework's shapes reports every setting in the file as unused and every name in the code as unknown. RoslynSense finds the wrapper in the same pass that finds the framework reads — a method is one when a parameter it was handed reaches the key position of a read, decided by binding rather than by name, so a `GetSetting` over a dictionary of its own is not one and a wrapper called `Q` is — then goes looking for its callers across the whole closure, including the projects scanned before that wrapper was known. Wrappers of wrappers resolve by recursion, three deep. A wrapper rooted in a section (`config.GetSection("Widget")[key]`) puts its callers' keys inside that section, and the counts, F12 in both directions, the completion list and the hover all follow from the same index, so they agree.

**Where a key is declared more than once, `↑ overrides` and `↓ overridden`.** The chain is the one the runtime actually composes — `appsettings.json`, then each `appsettings.{Environment}.json` overlay, then user secrets; for `web.config`, the root file and then each subdirectory's. The hover lists the chain with the value each file gives the key, so `appsettings.json` is no longer read as the value that runs when the Development overlay quietly replaces it. Objects are left out of the chain deliberately: sections merge, only leaves are overridden.

**F12 works both ways.** From a key to the C# reading it, and from `GetValue("Host")` or `ConfigurationManager.AppSettings["Host"]` to the entry declaring it — in every file of the chain, since which one applies depends on the environment.

**Completion offers what the code wants and the file lacks.** The properties of a bound options type; the values a `bool` or an enum admits; and, at any depth, the keys the application reads but this file does not declare — the mirror image of the reference count, since a read with no entry behind it fails as a null at runtime and as nothing at all before then.

**`N external references` covers what only compiled code reads.** `Kestrel`, `Logging`, a NuGet package binding its own section, a Framework library calling `ConfigurationManager.AppSettings["Timeout"]` — none of which the solution's own source ever mentions. Those are found by scanning the IL of referenced assemblies for a string handed to a configuration API and then confirming it against the type system: the receiver has to be an `IConfiguration` (or a first parameter that is, for the static extensions), or one of `ConfigurationManager` / `WebConfigurationManager`, which is what separates a real read from `RegistryKey.GetValue`. A read the package wrapped once and then calls everywhere is followed too — a platform shipping `Config.GetSetting(name)` compiled into its own assembly hides the key from both sides, since the IL scan finds a method naming no string and the source scan finds a call to a method it has no body for. Methods handing a parameter to a configuration API are collected by the same pass that finds the literals and confirmed by the same rules, a second pass then credits every call to one, and the wrappers are published to the source-side index as well, so the solution's own `Config.GetSetting("InstallationDate")` counts as a read of `InstallationDate` rather than as nothing at all. Assemblies a project in the solution builds are skipped, since their source is already indexed; the rest are filtered by the metadata tables before a single method body is read, and cached per assembly against its timestamp. The lens is kept separate from the reference count because clicking it opens decompiled source rather than yours — the decompilation happens on the click, and the landing position is refined to the line holding the literal. Sections discovered this way are offered in completion too, labelled with the assembly that wants them.

`--no-appsettings` and `--no-webconfig` (or `"tools": { "appSettings": false, "webConfig": false }`) remove the packs from the daemon. Turning off `web.config` support leaves the binding-redirect diagnostics alone; they belong to the assembly-binding feature, not to this pack.

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

- Every layer that applies is merged, weakest first (see [Settings layers](#settings-layers)). Within the repository, files are walked from the filesystem root down to `Directory.GetCurrentDirectory()`, so an outer file sets defaults and a nearer one overrides the fields it names.
- Merging is per field, on the raw JSON. Objects merge key by key; arrays, strings, numbers and booleans replace outright, so `"preload": ["a.sln"]` in a nearer layer means exactly that one path. An explicit `null` replaces too, which is how a nearer layer puts a setting back to its default.
- Lenient JSON: line/block comments, trailing commas, and unknown properties are accepted. Unknown properties are silently ignored for forward compatibility.
- A layer that does not parse is logged to stderr and **skipped**; the other layers still apply.
- Per-connection parse failures (unknown provider, empty value) are logged as warnings and the entry is skipped.
- **Live reload.** The shared host watches every directory a layer could occupy and applies edits without a restart: feature toggles, database connections, resource settings, debugger view settings (which reach a session that is stopped right now), `maxWorkspaces`, `hostIdleMinutes`. The host log names what changed. Running apps, background tasks and profiling sessions survive the reload; a file that stops parsing keeps the current settings until it parses again. Already-connected MCP chats keep their advertised tool *list* until the chat restarts (behavior behind the tools updates immediately), and an editor's advertised capabilities update on its next reconnect. Without the shared host (in-process mode), a change is detected and logged but needs a restart to apply.

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

**Debugging and running are per-chat.** Debug and run tools are *not* forwarded to the shared host — they run in-process in each client, so every chat has its own independent debug session and its own launched applications. Multiple chats can debug the same solution at once without colliding, and a launched app is torn down with the client that started it rather than being orphaned. (Trade-off: a debug session loads its own workspace in the client process, but debugging is interactive and infrequent.)

Designer regeneration is the exception: it is a side effect on the shared source tree, so `OpenSolution` runs a single watcher in the host rather than one per chat.

**Single host guarantee.** Exactly one host serves a solution at a time: a daemon acquires an exclusive lock file before it begins listening, so a daemon that loses a startup race exits without serving; the OS releases the lock on process death, so a crash self-heals and the next call respawns.

## LSP server (editor integration)

RoslynSense is also a Language Server Protocol server: run `roslyn-sense --lsp` and any LSP editor (VSCode, Neovim, Helix, …) gets C# language features from the **same shared daemon** the MCP clients use. One solution load serves both the editor and the AI assistant — and document sync means the assistant's analysis tools (diagnostics, find usages, rename, …) see the editor's **unsaved buffers**, while LSP rename/code actions are returned as workspace edits applied in the editor (never disk writes under a dirty buffer).

```
editor ──stdio──> roslyn-sense --lsp ──named pipe──> per-solution daemon <──named pipe── MCP clients (chats)
```

The `--lsp` process is a thin proxy: it connects to (or spawns) the per-solution daemon and forwards LSP JSON-RPC over the daemon's pipe. Without a resolvable solution or reachable daemon it hosts the LSP session in-process.

Capabilities: definition (incl. type definition), references, implementation, hover, document/workspace symbols, document highlight, rename (with prepare), diagnostics (push, or pull for LSP 3.17 clients), completion, code actions (quick fixes + refactorings), document + range formatting, folding ranges, call hierarchy, type hierarchy, semantic tokens, inlay hints (parameter names + implicit types), code lens (reference counts, override/implements links, run/debug test), watched-file sync (`workspace/didChangeWatchedFiles`), progress reporting (`$/progress`), workspace commands (`roslynSense.restore`, `roslynSense.reloadWorkspace`, `roslynSense.build`, `roslynSense.completionAccepted`, `roslynSense.fixBindingRedirects`), doc-comment generation on `///` (custom `roslynSense/onAutoInsert`), and ranked one-box search (custom `roslynSense/searchEverywhere`, plus `roslynSense/searchText` for the literal scan behind its Text tab). Position encoding is UTF-16.

**Completion is ranked, not alphabetical.** Roslyn decides what is in scope; the order is computed here, ReSharper-style. What you type is matched with a CamelHumps matcher (`sb` → `StringBuilder`, `tolower` → `ToLowerInvariant`, one typo tolerated on longer words — and typo hits vanish the moment a clean one exists), and the match quality feeds a 64-bit relevance word whose bit order *is* the ranking: match quality first, then target-type fit, then kind (locals and parameters > fields and properties > methods > extension methods > keywords > types), then provenance: the type's own members beat inherited ones, `object`'s members (`ToString`, `GetHashCode`, …) sink below every real member, `[Obsolete]` sinks below its peers, and unimported items sink below everything already in scope. Among equals, the local declared nearest above the caret wins. Declaring types and that nearest local come from one pass over the type being completed on, not a symbol resolve per item. Unimported extension methods are offered too (`value.Shout` finds an extension in a namespace you have not imported) and commit adds the `using`, same as unimported types. Accepted items are remembered per context (`roslynSense.completionAccepted`) and promote one item inside its tier only, so usage never reorders across tiers. The order also survives the client: editors sort by their own fuzzy score before `sortText`, so each item's `filterText` is prefixed with the typed characters — every item then scores identically there and the server's ranking is what you see.

**Search Everywhere (Ctrl+T).** One box over types, members, files, IDE actions and plain text, ranked server-side the way ReSharper's Go to Everything ranks: match quality feeds a tier, and one tier step outweighs every possible match score, so an exact type beats an exact member beats a fuzzy type, with the shorter name winning ties (`List` before `ListView`). `Namespace.Type.Member` and `dir/file` narrow — each word but the last must match a container segment — and `t:`, `m:`, `f:` restrict to types, members or files. Names are matched before symbols are materialised, so a query costs a pass over declaration names rather than a pass over symbols. The VSCode extension renders a Rider-style popup (`roslynSense/searchEverywhere`): tabs for All / Classes / Files / Symbols / Actions / Text (Tab cycles them, and each tab forces its kind server-side via the request's `only` field), a preview pane under the list, Ctrl+Enter or "Open in Right Split", and an **include non-solution items** switch that also searches the public types of every referenced assembly — read straight from their metadata tables, ranked below all solution code, and opened as decompiled source through the `roslynsense-metadata` scheme. The Text tab is a literal case-insensitive scan (`roslynSense/searchText`) over the same directory walk the file search uses, so a hit in a `.proto` or a `.config` is as reachable as one in a `.cs`. `workspace/symbol` uses the same ranking, so clients without the extension get the order too.

**Settings page (`RoslynSense: Settings`).** A form over `roslynsense.json`, with a tab per scope — Solution, Solution (personal), Personal, Global. The form is generated from the schema the server emits, so every setting appears without the panel being taught about it, and each row shows the value **in effect** with a chip naming which layer it came from — the one question a layered file cannot answer by being opened. Booleans are three-state (Default / On / Off): unsetting a value is how you stop overriding a weaker layer, which a checkbox cannot express. Writes are surgical (`jsonc-parser` edits the text around the value), so comments, key order and indentation in a file a team maintains by hand survive being touched from a form. Anything the form cannot render — the raw resource-lookup escape hatch — offers to open the file instead. The same schema is registered for `roslynsense.json` and `roslynsense.local.json`, so hand-editing gets completion and validation too.

**Diagnostics include analyzers.** Project analyzers (StyleCop, Roslynator, in-house) and Roslyn's built-in `IDE0xxx` code-style rules run alongside compiler diagnostics, with `.editorconfig` / `.globalconfig` severities honored. They are computed off the typing loop: compiler squiggles publish after ~400 ms as before, analyzer results follow once typing pauses, cached per document version. Turn them off with `ROSLYNMCP_ANALYZER_DIAGNOSTICS=0` (all analyzers) or `ROSLYNMCP_CODE_STYLE_DIAGNOSTICS=0` (IDE rules only); `ROSLYNMCP_ANALYZER_TIMEOUT_SECONDS` caps a pass (default 15).

**Configuration files are joined to the code that reads them.** `appsettings.json` (with its environment overlays and the user-secrets store) and `web.config` / `app.config` (`<appSettings>` and `<connectionStrings>`) each get an **N references** lens over every key, counting the literal reads, the binding sites, and — where a section is bound to an options type — the references to the property the key becomes. A zero is the finding rather than noise: a settings file accumulates keys for code deleted years ago, and the zeros are how they are found. F12 works both ways, from a key to the C# reading it and from `GetValue("Host")` or `ConfigurationManager.AppSettings["Host"]` to the entry declaring it — including through a reading method the solution wrote for itself, so `Config.GetSetting("Host")` is a reference like any other — and completion offers the keys the code reads but the file does not declare — plus the properties of a bound options type, and the values a bool or enum admits.

Where a key is declared more than once, an **↑ overrides** / **↓ overridden** lens marks each declaration and the hover shows the chain with the value each file gives it, so `appsettings.json` no longer reads as the value that runs when `appsettings.Development.json` quietly replaces it (the same for a `web.config` in a subdirectory). Keys that only compiled code reads — `Kestrel`, `Logging`, a NuGet package binding its own section, a Framework library calling `ConfigurationManager.AppSettings["Timeout"]` — get an **N external references** lens, kept separate from the count of real references because clicking it opens decompiled source rather than yours. Those reads are found by scanning the IL of referenced assemblies for a string handed to a configuration API, confirmed against the type system (the receiver must be `IConfiguration` or one of the configuration managers, which is what tells a real read from `RegistryKey.GetValue`), and skipped entirely for assemblies a project in the solution builds, whose source is already indexed. A platform that wraps the read once — `Config.GetSetting(name)`, compiled, with every caller naming the wrapper — is followed through the wrapper, on both sides: the package's own calls to it, and the solution's. The scan is filtered by the metadata tables before any method body is read — three quarters of a typical reference set is rejected outright — and cached per assembly against its timestamp. Sections discovered this way are offered in completion too, labelled with the assembly that wants them.

**Binding redirects are checked against what ships (.NET Framework).** Updating a package rewrites the reference and leaves the `<bindingRedirect>` in `web.config` / `app.config` naming the version that was there before — nothing fails at build, and the first symptom is a `FileLoadException` from a code path nobody exercised before shipping. The config is compared against the assemblies the project actually ships (read from `bin`, or from the extracted packages when the last build is stale), so what gets reported is what the runtime will do: a redirect naming the wrong version or one whose `oldVersion` range no longer reaches what binds are warnings, and a redirect for an assembly that ships nowhere — or for an unsigned one, which the runtime ignores — is a hint. It comes from the solution sweep as well as from the open file, because nobody opens a config to find out that a redirect went stale. Each one carries a quick fix; hovering an `<assemblyIdentity>` name reports the version installed and where it was read from; and a lens at the top of the file brings every stale redirect up to date at once (`roslynSense.fixBindingRedirects`), through `workspace/applyEdit` so it stays undoable.

**Debugging and tests in the editor.** The VSCode extension contributes a `roslynsense` debug type backed by netcoredbg's DAP mode, so F5 builds and launches your app — with watch, locals, conditional breakpoints, exception filters, and variable editing — without the Microsoft C# extension. It also registers a Test Explorer (discovery via Roslyn against the loaded solution, runs via `dotnet test`, plus Debug Test and coverage gutters). Custom methods behind these: `roslynSense/debuggerPath`, `roslynSense/launchTargets`, `roslynSense/attachTargets`, `roslynSense/testProjects`, `roslynSense/testDiscover`, `roslynSense/testRun`, `roslynSense/testDebug`, `roslynSense/testCoverage`.

**Test impact and coverage.** Each member carries an "N tests" code lens — how many tests are known to execute it — and clicking it lists them and offers to run the lot. A **Run Tests for Git Changes** button in the Testing view's toolbar runs only the tests your working copy's changes can affect, and a **Coverage** view in the RoslynSense sidebar shows namespace → class → method coverage with statement counts, worst first. All three read a per-test coverage map (built once per test class, refreshed incrementally) rather than an ordinary coverage report, which cannot say which test hit which line. Custom methods: `roslynSense/testsCovering`, `roslynSense/impactedTests`, `roslynSense/buildCoverageMap`, `roslynSense/coverageSnapshot`.

The VSCode extension also shows a status-bar counter of applications launched by AI chats (`run_project`), with click-to-inspect and kill (custom `roslynSense/runningProcesses` / `roslynSense/killProcess`, backed by a cross-process registry — launches stay per-chat, visibility is machine-wide).

Options: `--solution <path>` pins the solution explicitly; otherwise the nearest solution to the working directory is used.

A minimal VSCode extension lives in [`vscode-extension/`](vscode-extension/) — see its README for setup and coexistence notes with the Microsoft C# extension.

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

### Solution Session

| Tool | Description |
|------|-------------|
| **OpenSolution** | Load a solution's projects, report each project's framework and run kind plus the .NET Framework toolchain (MSBuild, IIS Express, SqlMetal), and start watching markup so `.designer.cs` files regenerate on save. Omit `solutionPath` to auto-discover. |
| **CloseSolution** | Stop the designer watcher and release the session. |
| **GetSolutionStatus** | Report the open solution, watcher state, and recent automatic designer regenerations. |

### Generated Files

Visual Studio maintains `*.aspx.designer.cs`, `*.ascx.designer.cs`, `*.master.designer.cs` and
`*.dbml.designer.cs` through custom tools an agent does not have. Rather than hand-editing those
generated files — where the edit is lost on the next regeneration — edit the markup or model and
regenerate.

| Tool | Description |
|------|-------------|
| **RegenerateDesigner** | Regenerate the `.designer.cs` for WebForms markup (`.aspx`/`.ascx`/`.master`) or a LINQ to SQL model (`.dbml`). Accepts a file, `.csproj`, `.sln`, or directory. Set `dryRun: true` to preview. |

WebForms designers are generated from the resolved control tree, so each server control with an
`ID` gets a correctly typed field. Controls nested in a template get no field (they are reached via
`FindControl`), and a control whose field is already declared by hand in the code-behind is skipped
so no duplicate member is emitted. `.dbml` regeneration shells out to `SqlMetal.exe` from the
Windows SDK.

Template-nested controls stay navigable despite having no field: F12 on the `"id"` literal in a
`FindControl("id")` call jumps to the `ID` in the markup — scoped to the right naming container
when the containing method is wired as a control's event handler (`OnItemDataBound="list_ItemDataBound"`
means the lookup searches `list`'s templates first) — and F12 or Shift+F12 on the template-nested
`ID` itself lists the `FindControl` call sites. Wrapper methods that forward a string parameter to
`FindControl` (`FindControl<T>(control, id)`, `SetText(control, id, …)`, and the like) are
discovered by scanning the project and its referenced projects, so the same navigation works
through a shared utility library's extension methods.

### Running Applications

Applications are per-chat: they are launched by the client that asked for them and torn down with
it, so two chats never fight over one process.

| Tool | Description |
|------|-------------|
| **RunProject** | Build and run a project, leaving it running. ASP.NET Core and .NET/.NET Framework console apps launch directly; legacy ASP.NET sites launch under IIS Express using the port and virtual path from the project's `WebProjectProperties`. Waits for the port to accept connections and returns the URL and PID. Builds first by default, like Visual Studio; pass `build: false` to launch existing output. |
| **StopProject** | Stop by session ID, project path, or `all`. Kills the whole process tree. |
| **ListRunningProjects** | List applications started in this chat with state, PID, URL and uptime. |
| **GetProjectOutput** | Read captured stdout/stderr for a session. |

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
| **BuildCoverageMap** | Build the per-test coverage map — which tests execute which lines. Compiles once, runs coverage per test class (concurrently where the collector allows), then rebuilds only the classes whose source changed. |
| **RunImpactedTests** | Run only the tests your current git changes can affect. Matches changed lines against the coverage map, and walks references for code the map has not seen yet. `scope`: `uncommitted` (default), `branch`, or `ref`. Set `dryRun: true` to see the selection and why each test was picked. |

### Debugging

Disable with `--no-debugger`.

The debug engine is selected automatically from the target — you never choose it:

| Target | Engine |
|--------|--------|
| .NET / .NET Core | [netcoredbg](https://github.com/Samsung/netcoredbg), auto-provisioned on first use |
| .NET Framework | ICorDebug, built in |

No single engine covers both: netcoredbg speaks only to CoreCLR, and ICorDebug is the only way into
.NET Framework. `DebugStartTest` picks from the project's target framework; `DebugAttach` has no
project to consult, so it picks from the CLR the target process actually loaded — which is how
attaching to `iisexpress.exe` or `w3wp.exe` resolves to the .NET Framework engine.

.NET Framework debugging binds breakpoints through Windows PDBs, and pending breakpoints rebind as
modules load — so breakpoints land in shadow-copied `bin` assemblies and in the generated
`App_Web_*` assemblies produced from inline ASPX code.

Expression evaluation resolves arguments, locals, fields and array elements directly, and calls
into the debuggee for computed properties and parameterless methods (`order.Total`,
`order.Describe()`). `DebugSetVariable`-style assignment works for primitives and booleans.

**Debugger attributes.** The ICorDebug engine honours the `System.Diagnostics` attributes a type
uses to describe itself to a debugger, so a value looks the way its author meant it to rather than
the way it is stored:

- `[DebuggerDisplay("…{Member}…")]` renders the value. Placeholders are member paths evaluated in
  the target, so a computed property named by one runs its getter; `,nq` drops the quotes around a
  string. Breakpoint conditions deliberately compare the *raw* value, never the display string.
- `[DebuggerTypeProxy(typeof(View))]` expands the value through its view type — constructed in the
  debuggee, so a dictionary shows its entries instead of its bucket arrays.
- `[DebuggerBrowsable]` hides a member (`Never`) or replaces it with its own children
  (`RootHidden`, which is how a `List<T>` shows elements rather than an `_items` array).
- `[DebuggerStepThrough]`, `[DebuggerHidden]` and `[DebuggerNonUserCode]` are stepped past, along
  with framework modules and code with no symbols — Just My Code.

Whenever a proxy or a hidden member means the listed children are not the object's own fields, a
**Raw View** child is offered beside them; expanding it gives the unfiltered fields. Every one of
these is a switch, because each hides something and the day the attribute is what is wrong is the
day you need it off:

```json
"debugger": {
    "debuggerDisplay": true,
    "typeProxy": true,
    "browsable": true,
    "justMyCode": true,
    "rawView": true,
    "maxChildren": 100
}
```

The same switches exist as `--no-debugger-display`, `--no-type-proxy`, `--no-debugger-browsable`,
`--no-just-my-code`, `--no-raw-view`, and in the editor as `roslynSense.debugger.*` — where a
change reaches a session that is stopped right now, without restarting it. On CoreCLR targets
netcoredbg implements the display attributes itself with no switch to turn them off, so there only
`justMyCode` is forwarded.

**Cross-architecture targets.** ICorDebug cannot attach across x86/x64, so a target whose bitness
differs from the server is debugged through a matching worker process running the same engine —
this is what makes a 32-bit IIS Express app pool debuggable. Selection is automatic. The workers
are framework-dependent, so debugging a 32-bit target needs the x86 .NET runtime installed; when it
or the worker is missing, the error says so rather than failing at attach.

One current limit: `DebugStartTest` is not supported for .NET Framework test projects — run the
tests, then `DebugAttach` to the test host.

| Tool | Description |
|------|-------------|
| **DebugStartTest** | Start debugging a .NET test project. Builds, launches the test host, and attaches the debugger. |
| **DebugAttach** | Attach the debugger to a running .NET or .NET Framework process by PID. |
| **DebugSetBreakpoint** | Set a breakpoint at a file and line. Supports conditions and batch mode. |
| **DebugRemoveBreakpoint** | Remove a breakpoint by ID. Supports batch removal. |
| **DebugContinue** | Continue, step in/over/out, pause, run until/to a line, or move the instruction pointer — selected with the `action` parameter. |
| **DebugEvaluate** | Evaluate expressions in the current debug context. Supports batch evaluation with semicolons. |
| **DebugStatus** | Get debugger status, breakpoints, and current pause position with optional locals and stack trace. |
| **DebugStop** | Stop the debug session and clean up. The debuggee shuts down cleanly, and is killed only if it will not exit. |

### Profiling

Profiling uses [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) with CPU sampling, auto-provisioned on first use. Disable with `--no-profiling`.

| Tool | Description |
|------|-------------|
| **ProfileTests** | Profile a test project to find CPU hotspots. Returns top methods ranked by self-time. |
| **ProfileApp** | Profile a .NET application for a specified duration. |
| **ListProfilingSessions** | List recent profiling sessions with IDs for investigation. |
| **ProfileSearchMethods** | Search profiled methods by name pattern. |
| **ProfileCalls** | Show the direct callers or callees of a method and how much time was spent in each. |
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

#### SQL Server: TrustServerCertificate

SQL Server connection strings get `TrustServerCertificate=True` unless they already specify it.

This exists because the same connection string behaves differently in the two SqlClients. A
.NET Framework app's `System.Data.SqlClient` defaults to `Encrypt=false`, so a `web.config` string
pointing at a development server with a self-signed certificate works. `Microsoft.Data.SqlClient`,
which this server uses, has defaulted to `Encrypt=true` since v4.0 — so the identical string fails
with a certificate-trust error here while the application it was copied from connects fine.

Note that this does weaken transport security: the connection is still encrypted, but an
unvalidated certificate offers no protection against interception. It is a development-time default.
To opt back into validation — for a production server, say — state it explicitly, and it is
respected (the spaced synonym `Trust Server Certificate` works too):

```
--db prod=mssql:Server=db.example.com;Database=App;TrustServerCertificate=False
```

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

## Skills

Installing via the Claude Code plugin also installs a set of skills (`skills/*/SKILL.md`). Claude
loads them automatically when the work matches; you can also invoke one explicitly, e.g.
`/roslyn-sense:csharp`.

| Skill | Covers |
|-------|--------|
| **`csharp`** | The core: C#/.NET conventions plus navigation, editing, refactoring, building, packages, and running apps — which tool to reach for instead of grep or a shell build, and when to regenerate designer files rather than editing them. Points at the skills below. |
| **`csharp-testing`** | Test conventions, discovering and running tests, coverage, and impacted-test selection. |
| **`csharp-debugging`** | Breakpoints, stepping, evaluating, watching values — on either runtime, including IIS Express. |
| **`csharp-profiling`** | CPU sampling and heap snapshots: hot paths, callers/callees, and leak hunting. |

Skills are a Claude Code feature. On other MCP clients, point your agent at those files directly —
they are plain Markdown with no Claude-Code-specific syntax in the body.

### Keeping the tool up to date

The plugin declares the MCP server as `roslyn-sense`, which is the [.NET global tool](#install) —
installing the plugin does not install the tool.

The server checks NuGet for a newer version itself and mentions one in `OpenSolution`'s output. The
check costs nothing at session start: it runs on a background task, caches the answer for 24 hours,
and makes no request at all when that cache is fresh. Updating is left to you, since a running
server holds its own binary and cannot replace it in place.

Deliberately *not* a `SessionStart` hook running `dotnet tool update`: that takes about five seconds
even when there is nothing to update, on every single session.

| Env var | Effect |
|---------|--------|
| `ROSLYNMCP_NO_UPDATE_CHECK` | `1`/`true`/`on` disables the version check entirely. |

If the tool is missing altogether, the `csharp` skill tells the agent to install it and ask for a restart.

## Markup Snippet Convention

Many tools use a `markupSnippet` parameter with `[| |]` delimiters to identify a target symbol:

```
var x = [|Foo|].Bar();          // targets Foo
public interface [|IService|]    // targets IService
void [|ProcessData|](int x)     // targets ProcessData
```

The snippet is matched against the file content. Whitespace differences are tolerated.
