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
- `roslynSense.languages.webforms` — language features for `.aspx` and its siblings in this window
  (default on). See [Turning it off](#turning-it-off).
- `roslynSense.languages.resx` — resource-key features in `.resx` files and at the call sites that
  read them, in this window (default on). `--no-resources` is the solution-wide switch. See
  [Resources](#resources).
- `roslynSense.trace.server` — LSP trace for debugging.

## Building the extension

```bash
cd vscode-extension
npm install
npm run compile
npm run package   # produces the .vsix (requires @vscode/vsce)
```

## Third-party content

`syntaxes/webforms.tmLanguage.json` and `syntaxes/csharpEmbedded.tmLanguage.json` are copied
from [vscode-webforms](https://github.com/GerardSmit/vscode-webforms) (MIT). The embedded C#
grammar there is itself a conversion of
[dotnet/csharp-tmLanguage](https://github.com/dotnet/csharp-tmLanguage). Fixes belong upstream
first, then get re-copied here.
