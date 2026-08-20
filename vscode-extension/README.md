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
resx). Solution folders sort before projects, both alphabetically. Show All Files reveals files
that are on disk but not in the project, dimmed.

Solution folders are editable, not just visible: create, rename and dissolve them, drag a project
between them (nothing moves on disk — only the solution file changes), and attach existing files
to them as solution items, including by dragging from the OS file explorer. Both `.sln` and
`.slnx` are written.

Add Existing Item on a project reads the surrounding folder: a file already inside the project's
directory is included where it lies, and only a file from elsewhere is copied in.

The toolbar carries three buttons — Search the solution, Focus Current File, and Collapse All. The
rest of the view's toggles (Follow Current File, Show All Files, Show Ignored Folders, file nesting,
Refresh) live in the `…` menu beside them.

Every row is drawn with an icon of its own, so a folder and the files inside it line up. A project
carries a language badge — C#, F#, Visual Basic — and the files inside it carry a glyph tinted in
the same family, which keeps the project the row that stands out. `.proto` keeps a badge of its own,
having no project to put one on. Set
`roslynSense.solutionExplorer.fileIcons` to `theme` to have files drawn by your file icon theme
instead — at the cost of that alignment wherever the theme has nothing for an extension.

### Keyboard

| Key | Action |
| --- | --- |
| `F2` | Rename — a file, a folder, or a solution folder |
| `Delete` | Delete a file; detach a solution item; dissolve a solution folder; take a project out of the solution |
| `Alt+Enter` / `F4` | Edit project file |
| `Ctrl+Shift+A` | New file |
| `Shift+Alt+A` | Add existing item |
| `Alt+Insert` | New… (file, folder, class, interface, record, enum) |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` / `Ctrl+D` | Copy, cut, paste, duplicate |
| `Ctrl+F` | Filter the tree |
| `Ctrl+T` | Search the solution |
| `Ctrl+Left` | Collapse all descendants |
| `Ctrl+Shift+B` | Build the selected project |
| `Ctrl+Shift+F` | Find in the selected folder |
| `F5` | Set as startup project and debug |

All of these apply only while the Solution Explorer has focus, and all are rebindable —
**Preferences: Open Keyboard Shortcuts**, search for `roslynSense.solutionExplorer`. A binding you
set yourself always wins over the default.

One gap worth naming: holding Ctrl while dragging to copy instead of move is not possible, because
VS Code's tree drag-and-drop API reports no modifier state. A drag that crosses project boundaries
asks whether to move or copy; use Copy and Paste for the rest.

"Manage NuGet Packages" opens a panel with Browse / Installed / Updates / Consolidate. Sources come
from your `NuGet.config` chain, including disabled ones and Package Source Mapping, and a search
tells you which feeds answered rather than quietly returning a short list. Private feeds use NuGet's
own credential providers; if none of them can answer, the panel asks you to sign in and keeps the
credential in the OS keychain.

Central Package Management is understood on both sides: versions resolve from
`Directory.Packages.props` without a restore, the details pane names the file a version lives in,
and a batch update edits that file in place instead of running the CLI once per project.

The Updates tab selects several packages at once and applies them in one pass, with a version lock
— any newer version, same major only, same minor only, or match target framework — that picks the
newest version *within* the bound the way `dotnet-outdated` does.

"Match target framework" holds the families that ship with the platform to the .NET major the
project targets: a net8.0 project is offered the newest 8.x of `Microsoft.Extensions.*` rather than
the 9.x band built for a runtime it does not target, and a reference left behind on 6.x is pulled
forward to it. A package that does not version this way is not capped, and candidates whose
dependency groups have nothing for the project's frameworks are skipped rather than left to fail as
an NU1202 at restore.

Updating a package that has outgrown one of your other references is planned rather than discovered:
the Dependencies setting bumps those references to the minimum the new version requires, or to the
newest version the lock allows, and always shows exactly what it intends to move before writing
anything. Left at "selected only" nothing else is touched — and the NU1605 downgrade error that
follows is restore's to report, as before.

Selecting a package shows its README, license, dependency
groups per target framework, and any known vulnerabilities or deprecation, including ones reached
through a transitive dependency. Installing a package that does not support a project's target
framework warns first, rather than failing at restore.

A Sources tab manages the feeds themselves — add, retarget, rename, remove, enable and reorder,
with each feed showing the `NuGet.config` that declares it. Order is editable because it is not
cosmetic: it decides which feed answers first, and so which one a package published to two feeds
resolves from. Feeds configured machine-wide can be disabled but not rewritten, which is what NuGet
itself allows without elevation.

Nothing in the panel fetches remote content: icons are proxied by the server as data URIs, README
links open through VS Code rather than being navigable in the page, and remote README images are
shown as placeholders instead of being fetched.

Build, rebuild, clean, test, and watch tasks are contributed per project and use the built-in
`$msCompile` problem matcher, so they work as `preLaunchTask` and with `Ctrl+Shift+B`.

## WebForms

`.aspx`, `.ascx`, `.master`, `.asax`, `.ashx` and `.asmx` open as the **WebForms** language,
with syntax highlighting for markup, directives, embedded C#, and the JavaScript/CSS/SQL inside
them.

The language features are the same ones C# gets, resolved against the markup's own parse tree:

- **Go to definition (F12)** on a tag jumps to the control class, on an attribute to the
  property, on `OnClick=` to the handler method, on `ID=` to the code-behind field, and on
  `Inherits=` / `MasterPageFile=` to the class or the file.
- **Find references (Shift+F12)** and **rename (F2)** span both halves: renaming a handler in
  the code-behind rewrites the `OnClick=` that names it, and vice versa. Code blocks count too,
  and they are bound rather than text-matched — a rename never touches the same word sitting in
  a comment or a string.
- **Completion** offers registered tag prefixes and their controls, then that control's
  properties and events, then enum and boolean values. Inside a `<% %>` block you get real C#
  completion, signature help and hover, because the block is bound as part of the page class.
- **Diagnostics** flag unknown controls and properties, unbalanced tags, and event attributes
  naming a handler that does not exist. They reach the Problems panel whether or not the file is
  open, so a bad `OnClick=` in a page you have never visited still shows up.
- **Outline and folding** follow the control tree, showing each control under its ID.
- **Call and type hierarchy** work from markup. A `<script runat="server">` block is projected at
  class-member level inside the page's partial class, so a method declared there — including an
  `override` of `OnLoad` — is a real member: Ctrl+Alt+H finds its callers, and the type hierarchy
  from the page walks up to `Page` and `Control`. Every item comes back pointing at the `.aspx`
  itself rather than at the generated projection, so the result is a file you can open.
- **Go to symbol in workspace (Ctrl+T)** sees markup: control IDs, the page and control classes
  named by `Inherits`, and user controls registered with `<%@ Register %>`.
- **A pasted runtime ID** resolves to the control that declares it. `dnn_ctr1848_Orders_View_btnGo`
  — or the `$`-separated `UniqueID` form — is what you have in front of you when a rendered page
  misbehaves, and it is the one thing the ordinary search cannot answer: its generated segments
  match no declaration, and a control inside an `<ItemTemplate>` has no code-behind field to match
  in the first place. The containers are matched as well as the ID, so a `lblTotal` declared under
  three different repeaters resolves to the right one. An ID whose containers match nothing
  resolves to nothing rather than to the nearest same-named control, and an ID that stops at a
  container names the file.
- **Linked editing** ties an open tag to its closing tag — renaming `<asp:Panel>` retypes
  `</asp:Panel>` as you go. Typing the `>` that finishes an open tag writes the closing tag for
  you in the first place, and `<%--` finishes its own comment.
- **Document links** make the paths a page names Ctrl-clickable: `MasterPageFile`, `CodeBehind`,
  a user control's `Src`, and ordinary `<script src>` / `<link href>`. Only targets that exist on
  disk are underlined — a CDN URL or a runtime-substituted path stays plain rather than becoming a
  link that fails on click.
- **Expand selection (Shift+Alt+→)** grows outward through the markup's own ancestor chain —
  attribute value, attribute, tag, element with its children, and so on.
- **Semantic highlighting** colours by what the markup *binds to* rather than by what it looks
  like, so a control whose type does not resolve reads differently from one that does, and a
  recognised property reads differently from an attribute nobody claims.

### Data expressions in markup

`Eval("Buyer.Name")` is a string the runtime reflects over, so a misspelling in it is not a build
error, not a test failure, and not anything at all until the page renders — where it throws at the
visitor rather than at whoever wrote it. RoslynSense resolves the path against the bound item, which
comes from an ancestor's `ItemType` or from a `DataSource` traced through the code-behind:

- **Hover** on a segment describes the property it names, not the string it sits in.
- **F12** goes to that property.
- **Colour** marks the segments that resolve, so the one that does not stands out.
- **`WFB0001`**, a warning, names the member the item type does not have. Silent when the item type
  is unknown, and only the segment that broke the path is reported.

Control libraries carry the same idea in attributes — a grid column's `SortExpression` and
`DataField` hold a member path, and its `DataFormatString` holds a composite format string. Which
attributes those are comes from the library rather than from the framework, so **nothing is read
this way until it is listed**:

```json
{
  "webForms": {
    "dataExpressions": [
      { "tag": "grid:GridBoundColumn", "attribute": "DataField" },
      { "tag": "grid:GridBoundColumn", "attribute": "SortExpression" },
      {
        "tag": "grid:GridBoundColumn",
        "attribute": "DataFormatString",
        "kind": "format",
        "source": "DataField"
      }
    ]
  }
}
```

`tag` is the tag as written — or `*` for any — matched on the spelling rather than on the control's
type, so it keeps working in a site whose vendor assembly does not resolve. `kind` is `member` (the
default) or `format`. A `format` entry's `source` names the **sibling attribute** holding the value
being formatted: the column's `DataField` is resolved against the bound item, and its type is what
tells `{0:MM}` whether `MM` is a two-digit month or two literal Ms. See
[Format strings](#format-strings) for what that buys.

The registry ships empty on purpose. `SortExpression` holds a member path on one vendor's grid and a
SQL fragment on another's, so a guess would turn every use of it into a warning.

### Generating event handlers

Typing `OnClick="` offers the name the designer would have used (`btnSave_Click`) at the top of
the list; committing it writes the method into the code-behind with the signature the event's
delegate requires — `async Task` when the event is asynchronous.

The same thing is available as a quick fix on the "event handler not found" warning, and as a
refactoring on the control itself ("Wire *Click* to …"), which adds both the attribute and the
method in one edit.

### Breakpoints in markup

The gutter takes breakpoints in a WebForms file, on a `<% %>` block or a `<script runat="server">`
member, and they are shared with the chat the same way C# breakpoints are. ASP.NET compiles a page
into generated C# whose `#line` directives point back at the markup, so the document recorded in the
PDB is the `.aspx` itself and the debugger binds straight to it — no mapping layer involved.

Binding is deferred, exactly as it is in Visual Studio. A breakpoint is bound when its module loads,
and the `App_Web_*` assembly holding a page does not exist until that page is first requested, so the
dot stays hollow ("pending") after the site starts and fills in on the first request.

### Turning it off

`roslynSense.languages.webforms` switches the WebForms language features off for one window. The
grammar stays — markup still highlights, because VS Code cannot contribute a grammar conditionally
and colour without navigation is better than a wall of black text — but the server stops answering
about `.aspx` files and stops advertising the markup-specific pieces: the `<` and `:` completion
triggers, the event-handler command, the markup file operations.

Two properties are worth knowing, because they follow from the server being shared:

- **It is per window.** Another window on the same solution keeps its own answer, and so does any AI
  chat connected to the same daemon — the WebForms MCP tools are unaffected. Removing those is a
  server-side decision: `--no-webforms`, or `"tools": { "webforms": false }` in `roslynsense.json`.
- **It takes a window reload.** The capabilities were advertised when the connection was
  initialized and the protocol gives no way to withdraw them afterwards, so changing the setting
  offers a reload rather than taking effect in place.

## Resources

`.resx` opens as the **resx** language, and resource keys become navigable everywhere they are
written — in C#, in WebForms markup, and in the `.resx` files themselves.

The unit is the **family**: every `.resx` sharing a base name in one directory, so `Strings.resx`,
`Strings.nl-NL.resx` and `Strings.nl-NL.Portal-3.resx` are one thing. Nothing here guesses which of
them wins at runtime — that depends on the portal, the thread culture and a fallback locale stored in
a database, none of which exists in an editor — so every feature answers with the whole family
instead.

- **Go to definition (F12)** on a key opens the entries that declare it, one location per file, in
  probe order: the neutral file first, then translations, then customizations. It works from a C#
  lookup (`Localization.GetString`, `IStringLocalizer`, a helper you configured), from
  `<%$ Resources: Strings, Title %>` and `<%$ dnnLoc:Key %>` in markup, and from `meta:resourcekey`.
  F12 on the *prefix* of an expression builder opens the files that builder reads.
- **Hover** shows the value and names every file that declares the key — `Strings.resx`,
  `Strings.nl-NL.resx` — so a missing translation is visible without opening anything. On a call
  whose resource file could only be guessed from proximity, the hover says so.
- **Completion** offers the union of keys across the family, inside the C# literal, inside a builder
  argument, and in a `meta:resourcekey` / `resourcekey` attribute value. On a DNN site the `.Text`
  the runtime appends is stripped from the label, so what you pick is what you type. Builder
  prefixes complete too, `dnnLoc` only when DNN is actually referenced.
- **Rename (F2)** rewrites the whole family at once: the `name=` attribute in each `.resx`, every
  configured lookup literal in C#, every builder argument and `meta:resourcekey` in markup, and —
  when a `Strings.Designer.cs` exists — the generated property and its call sites, through Roslyn's
  own rename. It refuses rather than guesses: if the call site does not say which resource file it
  reads, or a key's `name=` carries an XML entity that cannot be spanned exactly, F2 does not open.
- **Find references (Shift+F12)** from inside a `.resx` finds the call sites, including markup ones.
- **Outline** of a `.resx` groups keys under the name before their first dot, so an
  `App_LocalResources` file reads as a list of controls rather than `btnSave.Text`,
  `btnSave.ToolTip`, `btnSave.AlternateText` interleaved three ways over. **Go to symbol in
  workspace (Ctrl+T)** searches keys with the same matcher C# symbols use.
- **Diagnostics** in the `.resx` buffer flag a key declared twice (RSX0001, warning) and, in the
  neutral file, mark each key at its declaration with the translations that lack it (RSX0002,
  information — an untranslated key still renders, so this is a worklist, not a defect).
- **Renaming a `.resx`** in the explorer drags its translations, its customizations and its
  `.Designer.cs` with it. Renaming `Strings.nl-NL.resx` alone does not — that is a statement about
  one file.
- **`<%$ AppSettings: … %>` and `<%$ ConnectionStrings: … %>`** resolve against `web.config`
  instead, layered from the project directory down to the page's own folder, with F12, hover and
  completion over the names they define.

Two things are deliberately off or absent. A key that no file of its family declares is reported only
if you ask for it (`"resources": { "missingKeyDiagnostic": true }` in `roslynsense.json`) — DNN's
common call shapes do not say which file they read, and a false "this key does not exist" is how a
feature gets switched off. And there is no way to *add* a key: writing a new entry into a `.resx`
from a code action is not implemented, so a missing key is a navigation target you create yourself.

`roslynSense.languages.resx` turns the whole thing off for one window, on the same reload-window
terms as WebForms; `--no-resources` (or `"tools": { "resources": false }`) turns it off for the
daemon, AI sessions included.

## Logging templates

A structured logging message is a small language living in a C# string, and nothing in C# connects
its `{Placeholders}` to the values beside them. Microsoft.Extensions.Logging, Serilog and NLog all
implement [messagetemplates.org](https://messagetemplates.org), and this reads all three.

The thing worth knowing before anything else: **at a call site the holes bind by position, not by
name.**

```csharp
Log.Warning("{User} left {Room}", room, user);   // logs the room as User and the user as Room
```

That compiles, runs, and quietly logs the wrong thing forever. Only the `[LoggerMessage]` source
generator binds by name.

- **Colour.** Each hole is painted — braces as punctuation, the name as the value it stands for. A
  hole that reaches no value is left the colour of the string around it, because that is what it
  prints as.
- **Hover** on a hole names the value it actually prints — `2nd value passed to _logger.LogWarning
  — matched by position, not by name` — with its type, plus what a `@`/`$` capture operator, an
  alignment or a format specifier does to it.
- **Completion** inside `{` offers what the call passes, in the PascalCase a log property is
  written in whatever the parameter is called, with the one this position actually renders
  preselected.
- **Diagnostics**, all warnings, each switchable in `roslynsense.json`:

  | | |
  |---|---|
  | `LOG0001` | A malformed template — an unclosed brace, a hole naming nothing. Microsoft.Extensions.Logging throws `FormatException` on the first of those. |
  | `LOG0002` | A `[LoggerMessage]` placeholder matching no parameter, so it prints as literal text. |
  | `LOG0003` | The placeholders and the values disagree in count. Because binding is positional, this shifts every hole after the mistake onto the wrong value. |
  | `LOG0004` | A value no placeholder prints. Reported on the parameter or the argument, not on the call. |
  | `LOG0005` | An exception passed as a rendered value instead of as the first argument. It compiles and logs something, and the stack trace, the error grouping and the sink's own exception rendering are all gone. |

`LOG0002` and `LOG0004` over a `[LoggerMessage]` restate what the source generator reports as
SYSLIB1014 and SYSLIB1015 — more precisely placed, on the hole and on the parameter rather than on
the method, but the same claim. Where the generator runs, turn those two off:

```json
{ "logging": { "unknownPlaceholder": false, "unusedValue": false } }
```

Where it does not — an older target framework, a project with the generator disabled, or a codebase
that calls the logger directly — they are the only report there is. Serilog and NLog have no
generator and no equivalent analyzer at all.

log4net is deliberately absent: `ILog.WarnFormat` is `string.Format` with a logger attached, so
there are no properties to name and nothing to explain.

`roslynSense.languages.logging` turns it off for one window; `--no-logging` (or
`"tools": { "logging": false }`) turns it off for the daemon, AI sessions included.

## Format strings

`dd-MM-yyyy` and `dd-mm-yyyy` are one keystroke apart. Both are valid, both look like a date, and the
second prints the minute where the month belongs — every month, until someone notices. The specifier
is handed to the value's own `ToString` at run time, so no compiler has ever had an opinion about it.

- **Colour.** Every component of a specifier gets its own colour, using C#'s existing token names so
  the theme you already have distinguishes them. A day, a month and a year are three colours; so are
  an hour, a minute and a second. The literal text between them keeps the string's colour, because
  that is what it prints as.
- **Hover** on a component says what it produces and works an example:

  > **MM** — Month, two digits
  >
  > Prints `03`.
  >
  > `dd-MM-yyyy` → `27-03-2026`
  >
  > | | |
  > |---|---|
  > | `dd` | Day of the month, two digits |
  > | `MM` | Month, two digits |
  > | `yyyy` | Year, four digits |

  Hovering the hole instead names the value it prints — `The 2nd value passed to String.Format —
  matched by position, not by name` — which is the arithmetic nobody does, and the reason
  `string.Format("{0:dd-MM-yyyy}", name, date)` compiles, runs and prints a name.
- **Completion** inside a specifier offers the components with their rendered output beside them, so
  "is the month `MM` or `mm`" is answered where it is asked rather than by requesting the page.

Everything reads the same grammar in every place a format string is written:

```csharp
string.Format("Completed {0:dd-MM-yyyy} by {1}", order.CompletedDate, user)
$"{DateTime.Now:yyyyMMdd}"
order.CompletedDate.ToString("HH:mm:ss")
DateTime.ParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture)
```

```xml
<grid:GridBoundColumn DataField="CompletedDate" DataFormatString="{0:dd-MM-yyyy HH:mm}" />
```

The **type of the value decides how the specifier reads**, because the same characters mean
different things: `MM` is a two-digit month on a `DateTime` and two literal Ms on a `decimal`, and
`N2` is a thousands-separated number on a `decimal` and the letter N followed by a 2 on a date. In
C# the value is beside the specifier and costs nothing to find. In markup it is named by a sibling
attribute, which is what `source` in [`webForms.dataExpressions`](#data-expressions-in-markup) is
for — a `decimal` column is then offered `#,##0.00` and `N2` where a `DateTime` column is offered
`dd` and `yyyy`. Where nothing said, the specifier is read from what it contains and the components
are still coloured.

`roslynSense.languages.formatting` turns it off for one window; `--no-formatting` (or
`"tools": { "formatting": false }`) turns it off for the daemon, AI sessions included.

## Value sets

Some strings are not really strings. A status code is one of a fixed list, that list is rows in a
lookup table, and the C# holding it is a bare `string` — so nothing between the two checks anything:

```csharp
if (status?.Code is "order_rejected" or "order_wait_for_logn")   // never true, forever
```

That compiles, the tests pass, and the branch is simply never taken. A row renamed by a migration
does the same thing in reverse to code that used to work. An `enum` would fix it and usually is not
available, because the table is the product's data and rows get added without a deployment.

So name the query once and say where its values are written:

```json
{
  "valueSets": {
    "sets": [
      {
        "id": "orderStatus",
        "connection": "shop",
        "query": "SELECT [Code], [Description] FROM Shop_OrderStatus ORDER BY [SortOrder]"
      }
    ],
    "bindings": [
      {
        "set": "orderStatus",
        "containingType": "Contoso.Shop.OrderController",
        "memberName": "OrderStatus_Get",
        "parameterTypes": ["string"],
        "valueIndex": 0
      },
      {
        "set": "orderStatus",
        "containingType": "Contoso.Shop.Data.OrderStatus",
        "memberName": "Code"
      }
    ]
  }
}
```

The two bindings are the two halves, and which is which comes from the member rather than from a
flag. A **method with a parameter position** takes the value as that argument. A **property or
field holds** one, so what is checked is every literal it is compared or assigned — and that means
all of these, wherever they are written:

```csharp
status.Code == "order_shipped"                                   // and !=
status?.Code is "order_rejected" or "order_wait_for_login"       // including or-patterns
status is { Code: "order_shipped" }
status.Code.Equals(code, StringComparison.OrdinalIgnoreCase)
switch (status.Code) { case "order_shipped": … }
status.Code switch { "order_shipped" => … }
status.Code = "order_shipped"
```

A **method with no parameter position** is read as returning one, so literals compared against its
result are checked too.

You get completion from the column itself — with the second column, if the query selects one, shown
beside each value as a label — hover saying what a code means and where the list comes from, and
`VAL0001` on a string the set does not contain, with the nearest value offered:

> `'order_wait_for_logn'` is not one of the 7 values of `'orderStatus'`, from shop: SELECT [Code], [Description] FROM Shop_OrderStatus ORDER BY [SortOrder]. Did you mean `'order_wait_for_login'`?

An error rather than a warning, because that is what it is — the same class of mistake as a
misspelled member name. `"severity": "warning"` while a codebase catches up.

**It never guesses.** The values are loaded once per session and cached, and `VAL0001` is reported
only for a set that loaded *completely* — a database that is unreachable, a query that failed and a
result too large to be a value set all report nothing at all, because "that is not a valid code" is
a claim about every code there is. Completion still offers whatever arrived, and hover says why the
rest did not.

Because the values are cached, **RoslynSense: Reload Value Sets** is how a migration that added a
row reaches the editor.

Sets with no database behind them work too — `"values": ["draft", "sent"]` instead of a query —
which is worth having for a list that lives in a spreadsheet, another team's documentation, or a
config file nothing in the solution reads.

`roslynSense.languages.valuesets` turns it off for one window; `--no-valuesets` (or
`"tools": { "valueSets": false }`) turns it off for the daemon, AI sessions included. With no
`valueSets` section there is nothing to do and the pack is not loaded at all.

## Editor context for AI chats

With `roslynSense.shareEditorContext` on (the default), the extension tells connected AI chats
which file and symbol you are looking at, your selection, and the diagnostics already visible —
so asking "why does this fail?" resolves to what is on your screen. It sends paths, the cursor,
the selection, and those diagnostics; never whole file contents. Turn it off in settings.

## Colours

The server classifies C# more finely than the LSP standard vocabulary can express. The standard
has a single `variable`, so a field, a local and a parameter arrive at the editor
indistinguishable and every theme paints the three of them one colour. RoslynSense sends `field`,
`local`, `constant`, `delegate` and `extensionMethod` as their own token types instead, each
declared with a `superType` so a theme that has never heard of them still colours them as the
standard type they refine.

Your existing theme therefore keeps working and gets slightly better. To get the full separation,
pick **RoslynSense Rider Islands Dark** (Preferences → Color Theme) — a port of Rider's Islands
Dark scheme that has a colour for each of them: fields and properties cyan, methods green, types
purple, locals and parameters plain grey.

Hovers are coloured by a different mechanism — VS Code renders the fenced code in a hover with
the C# TextMate grammar, and semantic tokens never reach it. The grammar only matches real
declaration syntax, so hovers are written as declarations: modifiers and accessibility included,
attributes above, a terminating `;`, and the containing type on its own line underneath rather
than glued to the member name.

## Coexistence with the C# extension / C# Dev Kit

The Microsoft C# extension (`ms-dotnettools.csharp`) runs its own language server. Running
both gives you duplicated hovers/definitions and a second solution load. For the shared-solution
workflow, disable the Microsoft C# extension for the workspace (Extensions → C# → Disable
(Workspace)) and use RoslynSense instead — debugging and tests no longer depend on it.

## Settings

- `roslynSense.serverPath` — path to the `roslyn-sense` executable (default: `roslyn-sense`).
- `roslynSense.solutionPath` — explicit `.sln`; default resolves the nearest solution from the workspace folder.
- `roslynSense.debuggerPath` — path to netcoredbg; empty lets the server find or download it.
- `roslynSense.languages.webforms` — language features for `.aspx` and its siblings in this window
  (default on). See [Turning it off](#turning-it-off).
- `roslynSense.languages.resx` — resource-key features in `.resx` files and at the call sites that
  read them, in this window (default on). `--no-resources` is the solution-wide switch. See
  [Resources](#resources).
- `roslynSense.trace.server` — LSP trace for debugging.

These are VS Code settings: per window, synced by Settings Sync, and about this editor. Everything
the *server* does — which language packs are on, which solutions preload, database connections —
lives in `roslynsense.json` instead, because the daemon serves more than this window.

### The settings page

**RoslynSense: Settings** — from the command palette, or the `⋯` menu at the top of the Solution
Explorer — opens a form over `roslynsense.json`, with a tab per scope:

| Tab | File |
| --- | --- |
| Solution | `<solution>/roslynsense.json` — the team's settings, committed. |
| Solution (personal) | `<solution>/roslynsense.local.json` — yours for this checkout. Gitignore it. |
| Personal | `~/.roslynsense/projects/<mangled-path>/roslynsense.json` — the same, for a checkout you would rather not write into. |
| Global | `~/.roslynsense/roslynsense.json` — every checkout on this machine. |

They merge field by field, weakest first in that reverse order, so a nearer file overrides only the
settings it names. Each row shows the value **in effect** with a chip naming the layer it came from
— the one question a layered file cannot answer by being opened — and booleans are three-state
(Default / On / Off), because unsetting a value is how you stop overriding a weaker layer.

The form is generated from the schema the server emits, so new settings appear without the panel
being taught about them, and the same schema is registered for `roslynsense.json` and
`roslynsense.local.json` — hand-editing gets completion and validation too. Writes are surgical:
comments, key order and indentation survive being touched from a form.

A setting that names a method in your code — a resource lookup's class, member and signature — is
one editor over all of it rather than a text box each. It asks the solution as you type: the class
and member names complete from the compilation, the overloads the signature selects are listed
against the ones it merely shares a name with, and the parameter carrying the key is a click rather
than a comma count. That is worth the machinery because a wrong class, a misspelled member and a
signature matching no overload all look exactly like a correct entry — they bind nothing, quietly,
and the only symptom is a feature that does not work. Settings whose valid values depend on the
solution, such as a lookup's fallback conventions, are a checklist of what this solution actually
defines. Both are declared in the schema (`x-shape`, `x-choices`), so the next setting naming a
method gets the same editor without the panel learning anything new.

## Building the extension

```bash
cd vscode-extension
npm install
npm run compile
npm test          # node --test over the pure-TypeScript half
npm run package   # produces the .vsix (requires @vscode/vsce)
```

## Third-party content

`syntaxes/webforms.tmLanguage.json` and `syntaxes/csharpEmbedded.tmLanguage.json` are copied
from [vscode-webforms](https://github.com/GerardSmit/vscode-webforms) (MIT). The embedded C#
grammar there is itself a conversion of
[dotnet/csharp-tmLanguage](https://github.com/dotnet/csharp-tmLanguage). Fixes belong upstream
first, then get re-copied here.
