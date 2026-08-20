# Language packs

Everything RoslynSense understands that is not C# lives here, one folder per language. A pack owns
its files across **both** front-ends at once: the LSP features an editor asks for, and the MCP tools
an AI session calls. That is the whole point of the abstraction — before it, WebForms was two
independent implementations of the same language that happened to share a name, and the MCP side
had the worse one.

C# is not a pack. It is the host language: markup delegates *into* the C# handlers for the code
embedded in it, and anything no pack claims falls through to Roslyn untouched.

```
Languages/
  Abstractions/        ILanguagePack, the registry, the session, the provider and contributor
                       interfaces, and the one registration entry point
  WebForms/            .aspx and its siblings
    Core/              the engine: parse, project to C#, resolve a caret, find references
    Lsp/               LSP-shaped handlers over Core
    Tools/             MCP-shaped formatters over Core
    WebFormsLanguage.*.cs
  Razor/               .razor and .cshtml — MCP tools only, no LSP providers
  Proto/               .proto — gRPC contracts, bound to the C# protoc already generated
    Core/              the engine: parse, resolve a caret, bind a declaration to its ISymbol,
                       find references
    Lsp/               LSP-shaped handlers over Core
    Tools/             MCP-shaped formatters over Core
    ProtoLanguage.*.cs
  Mediator/            MediatR and Zapto.Mediator — no files of its own, only the edge between a
                       Send and the handler DI resolves it to
    Core/              the engine: classify a symbol, a dispatch to its handlers, a handler to
                       its dispatch sites
    MediatorLanguage.*.cs
  Resources/           .resx, and the resource keys C# and markup name
    Core/              the engine: discover, group, read, and resolve a key to a family
    ResourcesLanguage.*.cs
  DotSettings/         ReSharper and Rider settings layers — answers no request about its own
                       files, and narrows the answers to requests about other files instead
    Core/              the engine: unescape, parse, stack the layers, resolve the four keys
    DotSettingsLanguage.cs
```

`DotSettings/` is the other kind of exception, and the opposite one. It owns two extensions and
implements no provider or contributor at all: nothing asks RoslynSense anything about a
`.DotSettings`, and if something did, the answer would be XML's. What the file changes is the
answer to requests about *other* files — which folders make a namespace, which files a search may
return, which types a coverage run counts — so the work happens at those three call sites and
reaches the settings through `ReSharperSettings.ForProject`. The pack exists to be the gate: it is
what a reader looks for when they want to know whether a committed settings file is allowed to move
those answers, and what they switch off when it should not.

Not every pack is a file type. `Mediator/` owns no extension at all: a request, its handler and the
call joining them are ordinary C#, and what is missing is the *edge* between them, which Roslyn
cannot see because the mediator matches generic types at runtime. So it has no `Core/Lsp/Tools`
split either — there is no request about a file of its own to shape — and it exists entirely as
contributions to requests about C#.

## Core / Lsp / Tools, and why the engine is shared

The three-layer split is not decoration. `Core` is the only place that knows the language: it parses
the file, projects the embedded code into C# so Roslyn can bind it, maps a caret to a symbol, and
finds references. It returns domain objects — a parsed document, a hit, a reference — and knows
nothing about LSP or MCP.

`Lsp/` and `Tools/` are both thin, and they are thin over the *same* `Core`. `Lsp/` turns a hit into
a `Location`; `Tools/` turns the same hit into markdown for a chat. Nothing decides anything at
those layers, and neither may reach past `Core` into its own copy of the engine.

This matters because the two surfaces used to drift, and the drift was invisible. MCP find-usages
answered from a substring scan while the LSP answered from a bound reference search, so
`find_usages` reported hits inside comments and string literals that Shift+F12 in the editor
correctly did not. The AI assistant got the worse answer, and nothing about that was visible from
either side. One engine is what stops it recurring: a fix to reference resolution is a fix to both
surfaces, or it does not compile.

## What a pack declares

`ILanguagePack` carries identity only — id, display name, file extensions, capability
contributions, and the two "declared interest" fields. What a pack can actually *do* is expressed by
which of the `ILanguage*Provider` and `ILanguage*Contributor` interfaces it also implements.
Dispatch pattern-matches on those, so:

- a pack that implements none is legal, and every request about its files goes to C#;
- adding a feature means adding one partial file that implements one interface, and no dispatch
  code changes anywhere;
- a pack can decline a single request cheaply — the server never asks it something it did not
  claim to answer.

`WebFormsLanguage` is a partial class split one file per feature
(`WebFormsLanguage.Hierarchy.cs`, `WebFormsLanguage.SemanticTokens.cs`, …) for exactly that reason.

### Providers answer about the pack's own files

One interface per LSP request, with signatures mirroring the C# handlers in `Lsp/Handlers`, so
dispatch is a straight either/or:

```csharp
private Task<T> Route<TProvider, T>(
    string uri, Func<TProvider, Task<T>> language, Func<Task<T>> csharp)
    where TProvider : class =>
    _languages.Resolve<TProvider>(uri) is { } provider ? language(provider) : csharp();
```

`LspServer` has a `TextDocumentIdentifier` overload forwarding to this one, because most requests
arrive with a document while the hierarchy requests carry a bare `item.uri`.

### Contributors add to an answer about a C# file

The other direction, and it is not the same shape. `OnClick="Save_Click"` in an `.aspx` is a
reference to a C# method that Roslyn cannot see, so find-references on that method must merge markup
hits into a C# answer rather than choose between them. Same for rename — omitting the markup edits
turns F2 into silent corruption, an attribute naming a method that no longer exists.

Every registered pack's contributors run on every such request. The question is not "whose file is
this" but "does anyone have more to say", which means **a contributor must be cheap to decline**.
See [Cost](#cost-declared-interest-not-scanning).

### Adding to an answer and replacing it are different seams

`ILanguageDefinitionContributor` and `ILanguageDefinitionRedirector` both fire on F12 over a C#
caret and they are separate interfaces because the difference between them is the whole content of
each.

A **contributor adds locations**, and may withdraw the ones its own answer supersedes. The `.proto`
line behind a generated `.cs` is added to Roslyn's answer; `Supersedes` then takes the generated
file back out, because it is not an alternative to that line but the same declaration re-emitted
into a file the next build overwrites, and offering both makes F12 a picker whose second entry is
never the wanted one. Only a pack that contributed to the request is asked, so a decline can never
empty the result. The default is to withdraw nothing, which is right for every contributor that
merely knows another place a symbol is mentioned.

`Supersedes` lives on `ILanguageSupersedingContributor`, which both the definition and the reference
contributor inherit, because a file is generated or it is not: F12 hiding `WidgetsGrpc.cs` while
Shift+F12 lists it would be the two features disagreeing about the same pair of files.

A **redirector replaces**. `_mediator.Send(new CreateUserRequest())` binds to `ISender.Send` — the
same metadata member every send in the solution binds to — so Roslyn's answer is not a worse
destination, it is not a destination at all. Offering it beside the handler rebuilds the
pick-one-of-two list the redirect exists to remove. Returning nothing means "not mine" and is the
normal answer; so is deciding the caret *is* a dispatch but being unable to name the handler,
because Roslyn's own answer beats a guessed one.

Two consequences worth knowing:

- A redirector is handed the document and the offset, not just the symbol, because what decides the
  answer is the invocation around the caret rather than the symbol under it.
- The append pass then runs on **what the redirect named**, so the two compose: F12 on a `Send`
  reaches the handler, and if protoc generated that handler, the `.proto` line comes with it.

`ILanguageCodeLensContributor` is the third of the C#-facing seams, for counts only the pack can
compute. A mediator handler is dispatched to from everywhere and referenced from nowhere, so the
C# reference lens over its `Handle` would read "0 references" above a peek listing a dozen.

### Two requests that carry no document

`completionItem/resolve` and `codeAction/resolve` arrive with no URI, so they cannot be routed.
The contract:

> A pack's completion items, code actions and code lenses must either be **self-contained** — nothing
> left to resolve — or carry enough in their `data` payload for the resolve handler to find its way
> back. `ILanguagePack.Id` is what a completion item or code action stamps; a code lens over the
> pack's own file routes on `CodeLensData.Uri`, and one contributed to a *C#* document stamps
> `CodeLensData.PackId`, because there the URI says only that the document belongs to nobody.

WebForms items are self-contained: a tag name, an attribute name, an ID are complete as sent, and
the items that come from inline C# were produced by the *C#* completion handler against the
projection, so they resolve through it.

Commands are the third documentless case, and they are dispatched by name:
`ILanguageCommandProvider.CanExecute(command)` is asked of each pack in turn.

### Projections are not file extensions

`ILanguagePack.IsProjectionPath` is a separate question from `FileExtensions`, deliberately. A
projection — the synthetic C# a pack generates so Roslyn can bind the code embedded in its markup —
*is* a `.cs` file as far as its extension goes. Matching it by extension would route requests about
it back to the pack that generated it instead of to Roslyn, which is precisely backwards. The two
questions are "do you own this file type" and "did you invent this file".

Generated and decompiled documents (the `roslynsense-generated:` and metadata URI schemes) belong to
no pack whatever their extension looks like; the registry and the session both exclude them before
any pack is consulted.

Protobuf is the first pack that needs no projection at all, and it is worth knowing that shape
exists before you reach for one. `Grpc.Tools` writes real `.cs` into `obj/` and MSBuild hands them
to Roslyn as ordinary `Compile` items, so the C# behind a `.proto` is already in the compilation
with real symbols on it: the pack binds each proto declaration to the `ISymbol` protoc generated
for it and lets `SymbolFinder` do the navigating, and `IsProjectionPath` answers `false` because it
invented no document. Ask whether the build already compiles code for your language before you
write a projector — it is a great deal of machinery to own.

## The two gates

Registration and activation are deliberately asymmetric, and this is the load-bearing decision of
the whole design. The daemon serves several LSP connections **and** any number of MCP clients from
one `IServiceProvider`, so a single toggle would mean one editor window's preference silently
removing tools from an AI session attached to the same daemon.

| | Registration | Activation |
| --- | --- | --- |
| Set by | `roslynsense.json` (`tools.webforms`) and `--no-webforms` | `roslynSense.languages.<id>` in the client's `initializationOptions` |
| Scope | the whole daemon process | one LSP connection |
| Held on | `LanguageRegistry` (DI singleton, immutable after startup) | `LanguageSession` (one per `LspServer`) |
| Governs | **MCP tool availability**, and what a session *may* enable | LSP dispatch and advertised capabilities for that window |

So: turning WebForms off in an editor's settings stops that window answering about `.aspx` and
leaves the other window — and every AI session on the same daemon — untouched. Removing the MCP
tools is what `roslynsense.json` and `--no-webforms` are for. Editor settings never reach the
registry.

Toggling is **reload-window**. The capability was advertised at `initialize` and the protocol has no
way to withdraw it afterwards, so `LanguageSession` is built once, right after
`ConfigurationHandler.Apply` has read the initialization options and before capabilities are built.

A session with no packs is pure C# fallback. That is also what a directly constructed `LspServer`
gets — every test that does `new LspServer(services)` without a registry — so tests need no
knowledge of packs at all.

### The semantic-token legend is per session

The legend is the union of C#'s token types and whatever the enabled packs declared, so two sessions
with different enabled sets genuinely have different legends. It therefore lives on
`LanguageSession`, not on the pack, and a pack asks the session for its own offsets when it emits:

- C# keeps the low indices, so its numbering never moves;
- `TokenTypeOffset(pack)` is where that pack's declared types start — the pack emits `offset + i`;
- `TokenModifierOffset(pack)` is a **bit shift**, not an index, because modifiers are a bitmask.
  Watch the total against the client's int width;
- a pack whose token really is a `class` or a `property` should call
  `LanguageSession.SharedTokenType` and reuse C#'s entry rather than declaring the name again — that
  way the user's theme colours it the way it colours the C# one.

## Cost: declared interest, not scanning

Contributors run against C# files, so a pack must cost approximately nothing in a solution that does
not use it. The mechanism is Roslyn's own, from `RegisterCompilationStartAction`: declare the types
you need, resolve them once per compilation, and return immediately when none are present.

```csharp
public ImmutableArray<string> WellKnownTypeNames { get; } =
    ["System.Web.UI.Control", "WebFormsCore.UI.Control"];

public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } =
    [SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field, SymbolKind.Event];
```

`WellKnownTypeNames` is the outer gate: neither control base class resolving means the project has
no WebForms in it, and every contributor can decline without touching the file system.
`InterestingSymbolKinds` is the inner one: markup refers to code-behind members by name from
attributes — a handler method, a control field, the page class in `Inherits` — so a symbol of any
other kind can be skipped before the pack's index is loaded.

A pack that declares neither will be asked about everything, on every reference and rename request,
in every solution. Declare both.

## Adding a pack

Say you are adding GraphQL.

**1. Write the pack.** `Languages/GraphQL/GraphQLLanguage.cs` implementing `ILanguagePack`, plus one
partial file per provider or contributor you can actually answer. Keep the engine in
`Languages/GraphQL/Core/` and let both `Lsp/` and `Tools/` be thin over it.

```csharp
internal sealed partial class GraphQLLanguage : ILanguagePack
{
    // The formatter is how the MCP tool handlers render their output; a pack with no MCP
    // surface takes nothing.
    public GraphQLLanguage(IOutputFormatter formatter) => InitializeToolHandlers(formatter);

    public string Id => "graphql";
    public string DisplayName => "GraphQL";
    public ImmutableArray<string> FileExtensions { get; } = [".graphql", ".gql"];
    public LanguageCapabilities Capabilities { get; } = LanguageCapabilities.None;
    public ImmutableArray<string> WellKnownTypeNames { get; } = ["HotChocolate.ObjectType"];
    public ImmutableArray<SymbolKind> InterestingSymbolKinds { get; } = [SymbolKind.NamedType];
    public bool IsProjectionPath(string? filePath) => false;
}
```

**1a. A pack does not have to own a file type.** `FileExtensions` may be empty, and the mediator
pack's is. A `Send` and the handler it reaches are both C#, so there is nothing for the extension
routing to match: every `ILanguage*Provider` would be unreachable, and the pack expresses itself
entirely through the C#-facing contributors and the redirector. `Contributors<T>()` does not consult
extensions, so an empty list costs nothing and keeps the pack out of routes it could only answer
wrongly. Such a pack still needs an `Id` — it is the per-window switch and the `data` stamp.

**2. Add the gate.** `ToolsConfig.GraphQL` in `Config/RoslynSenseConfig.cs`, threaded through
`EffectiveSettings.Resolve` next to `webForms` so `--no-graphql` and `tools.graphql` both work.
Without this the pack cannot be turned off, and turning a language off is how a user opts out of a
parser they do not want running.

**3. Register it — one file.** `Abstractions/LanguagePackRegistration.cs` has two methods and both
need the line:

```csharp
if (settings.GraphQL)
    packs.Add(new GraphQLLanguage(formatter));   // Create — for hosts with no container
...
if (settings.GraphQL)
    AddPack<GraphQLLanguage>(services);          // AddLanguagePacks — for hosts with one
```

`AddPack` registers the single instance as `ILanguagePack` *and* under every MCP tool-handler
interface it implements, which is what keeps one gate in front of the editor features and the AI
tools instead of each carrying its own.

**4. Know the three hosts, even though you do not edit them.** Three processes build a container or
a registry, and a pack registered in one but missing from another is the exact failure this file
exists to prevent:

| Host | Where | How it registers |
| --- | --- | --- |
| MCP server | `Program.cs` | `builder.Services.AddLanguagePacks(settings)` |
| Shared-host daemon | `Host/ToolHostServices.cs` | `services.AddLanguagePacks(settings)` |
| CLI (`--cli`) | `CliRunner.cs` | `LanguagePackRegistration.Create` via the `Languages(…)` helper — no container exists on this path |

The in-process LSP fallback goes through `ToolHostServices.Build`, so it is covered by the daemon's
registration and is not a fourth site.

`LanguageRegistry` publishes itself as it is constructed, and DI constructs it lazily, so a host
resolves it eagerly once the container is built. Without that, a daemon serving nothing but MCP
tools — no editor ever connects — would leave `LanguageRegistry.Current` empty, and every static
reading it would answer as though no pack were registered at all.

You do touch a host when the language needs something that is *not* a pack. WebForms contributes an
`IDesignerGenerator`, which is a peer of the pack rather than part of it — it is a generic seam with
a non-WebForms implementation — so `Program.cs` and `ToolHostServices.cs` each carry one gated
`AddSingleton<IDesignerGenerator, AspxDesignerGenerator>()` line alongside `AddLanguagePacks`.

**5. Adding a *new* MCP handler interface** (as opposed to implementing an existing one) is the one
change that is still spread out, because the CLI resolves tool parameters by type without a
container. It needs: the interface in `Abstractions/`, an `AddHandler<TPack, THandler>` line in
`LanguagePackRegistration.AddPack`, a list property on `LanguageRegistry`, and a
`pt == typeof(IEnumerable<TNew>)` arm in `CliRunner.InvokeAsync`.

**6. Wire the editor side.** `vscode-extension/` drives the client half from a single table so a
pack opts in once:

```ts
const EXTRA_LANGUAGES = [
  { id: 'webforms', extensions: ['.aspx', '.ascx', '.master', '.asax', '.ashx', '.asmx'], breakpoints: true },
  // A pack that owns no files: `.cs` is already selected unconditionally, so the row exists only
  // to carry the id into `serverSettings().languages`.
  { id: 'mediator', extensions: [], breakpoints: false },
];
```

Everything that used to be a hardcoded list is built from it: the LSP `documentSelector`, the
workspace `FileSystemWatcher` globs, the language-status item, and the `languages` object in
`serverSettings()` that becomes this connection's activation set. A row with no extensions is
filtered out of the first two by `enabledFileLanguages()` — a selector entry for a language id VS
Code has never heard of matches nothing, and the watcher glob would come out as `**/*.{}`. Two
things stay outside the table:

- `contributes.languages` and `contributes.grammars` in `package.json` are unconditional. VS Code
  cannot contribute those conditionally, and syntax highlighting without language features is still
  better than nothing.
- `contributes.breakpoints` is a manifest entry too, and needs the pack's language id listed for the
  gutter to offer a red dot at all.

A change under `roslynSense.languages` prompts a window reload rather than sending
`didChangeConfiguration`, matching the reload-window rule above.

## Embedded languages are a different extension point

A pack owns *files*. A language that appears inside a C# string literal — a route template in
`[HttpGet("api/{id}")]`, GraphQL in a `const string` — is not a file type and does not fit. That is
`IEmbeddedStringLanguage`, and it is orthogonal on purpose: a pack is resolved from a file extension
and owns whole documents, an embedded language is resolved from the symbol a literal flows into and
owns a span inside someone else's document.

Detection is Roslyn's, through `RoslynEmbeddedLanguages`: declare your `StringSyntaxIdentifiers` and
you get `[StringSyntax("…")]` resolution — chased through locals, fields and interpolation format
clauses — plus `// lang=id` comments, for free. Then implement whichever of
`IEmbeddedCompletionProvider`, `IEmbeddedDefinitionProvider` and `IEmbeddedDiagnosticProvider` you
can answer.

Registration follows from what the language is:

- **A pack that also claims literals** just implements the interface too.
  `RoslynEmbeddedLanguages.Current` derives from `LanguageRegistry.Current`, so there is no second
  registration site. This is how GraphQL would cover `.graphql` files and GraphQL-in-C#-strings out
  of one folder.
- **A language with no files at all** — ASP.NET Core route templates, which have no extension to
  own — calls `RoslynEmbeddedLanguages.Register` instead. It cannot be a pack; there is nothing for
  a pack to own.
- **A language Roslyn's detector cannot be made to name** implements `IConfiguredStringLanguage`
  and answers `DetectAsync` itself. That is the resources pack: `[StringSyntax]` would have to be written
  into an assembly we do not own, `// lang=` is not what a call site carries, and the unannotated
  well-known-API route is hardcoded to Regex and Json. `DetectAtAsync` asks the configured languages
  only for tokens Roslyn's own detector already declined, so it is one fallback in one method and
  `DetectAsync`/`DetectAllAsync` inherit it. Reject on syntax before binding — a large file
  otherwise pays a `GetSymbolInfo` per string literal on every diagnostics pass. The claim is
  asynchronous and carries the `Document` because a literal's meaning can depend on a declaration
  in another project: the configuration packs claim `Config.GetSetting("Test")` by reading the body
  of the method it is passed to, which needs the solution the semantic model alone does not carry.
  Anything that walks into another document that way memoises its answer against the syntax tree it
  read, which the workspace replaces on every edit to that file.

The same self-contained rule as above applies with more force: an embedded literal has no URI, so a
completion item produced inside one has nothing to route a resolve request back by. Send items
complete.

## Resources: the one pack whose model needs explaining

Reading a `.resx` is trivial. Deciding *which* `.resx` files a key at a call site is asking about is
not, and that is what `Resources/Core` exists for. Four ideas carry it, and someone adding a
localisation convention needs all four.

### A family, not a file

A **resource family** is every `.resx` sharing a base name in one directory: the neutral file, its
translations, and its customizations. `View.ascx.resx`, `View.ascx.nl-NL.resx` and
`View.ascx.nl-NL.Portal-3.resx` are one family with base name `View.ascx`. Families never cross a
directory — DNN puts its overrides beside the base file, and so does .NET — which is why
`ResourceDocuments.FamilyOf` can answer from a single directory listing, and why the whole
decomposition is tractable at all.

Decomposition is **grouping-first, and never parses a lone file name.** `CultureInfo.GetCultureInfo`
throwing is not a usable signal: on ICU it returns a neutral custom culture for any well-formed
unknown subtag, so `My.Company.Strings.resx` would parse `Company` as a culture. Instead
`ResourceFamilyParser` sorts a directory's stems by ascending length and asks, for each, whether the
file name is some *other* stem plus a dot plus a tail. `View.ascx` exists, so
`View.ascx.nl-NL.Portal-3` decomposes against it; `My.Company.Strings` has no shorter sibling stem in
front of it and is a base, full stop — no
culture parsing was ever attempted. Only then is the remaining tail parsed, right to left: override
patterns first, then at most one culture, which must pass both a shape regex and membership in
`CultureInfo.GetCultures(AllCultures)`. Failing either demotes the stem to its own base.

Culture case on disk is unstable by design — DNN lower-cases a name to read a file and re-cases it to
write one, so both `nl-nl` and `nl-NL` occur — so every culture comparison is `OrdinalIgnoreCase`,
canonicalised through `CultureInfo.GetCultureInfo(x).Name` on store.

### The catalog enumerates the family; it never simulates a winner

`Localization.GetString(key, root)` probes up to 27 files: three language tiers outer, three
customizations inner, and the whole cascade re-runs against `SharedResources.resx` beside the file
and then under `App_GlobalResources`. Which file wins is a function of the portal id, the thread
culture and a database-configured fallback locale, and **none of the three exists at edit time.** So
nothing picks one:

- definition returns one location per file of the family that declares the key, in the family's own
  precedence order — neutral, then cultures by name, then overrides by rank;
- hover shows the first value there is and names every file that declares the key;
- completion offers `ResourceFamily.AllKeys`, the union across the family;
- the missing-key diagnostic fires only when **no** file of the family declares it.

A key that only a culture or override file declares is a note, not an error:
`TryGetFromResourceFile` reads each file directly and never requires the neutral one to carry the
key. Simulation is a real question — "what does portal 3 show in Dutch" — but its inputs are
arguments rather than editor state, so it belongs in an MCP tool and not in the LSP.

### Two orthogonal axes, not one `resourceClass`

Where a root *value* comes from is syntactic; how that value *becomes a base name* is semantic.
Fusing them yields members like `VirtualPathFromContainingType` and a set that grows
multiplicatively. As a cross product, six sources and five interpretations cover every world we care
about:

| Call shape | `RootSource` | `RootInterpretation` |
| --- | --- | --- |
| `GetString(key, "~/…/View.ascx")` | `Argument` | `VirtualPath` |
| `<%$ Resources: Cls, Key %>` | `Argument` | `GlobalClassName` |
| `IStringLocalizer<Home>` | `TypeArgument` | `TypeName` |
| `PortalModuleBase.LocalizeText(key)` | `ContainingType` | `VirtualPath` |
| `<%$ dnnLoc:Key %>` | `ContainingFile` | `VirtualPath` |
| `meta:resourcekey` | `ContainingFile` | `RelativePath` |

`ResourceLookup.ParameterTypes` is mandatory rather than decorative. `Localization.GetString` has
three distinct two-argument overloads — `(string, string)`, `(string, Control)` and
`(string, PortalSettings)` — and only the first puts a root at index 1. Matching on name and arity
binds all three and resolves two of them to garbage.

### Confidence gates features; it does not decorate them

The unresolved root is the majority case, not a degradation: DNN's dominant call shapes are
`LocalizeText(key)`, `LocalizeString(key)` and `GetString(key, this.LocalResourceFile)`. Three rules
run in order, and each yields a `RootConfidence`:

1. **Convention, as a primary path.** When the root binds to `IModuleControl.LocalResourceFile`, do
   not chase the value — do what DNN does. `ModuleControlFactory` sets it from `ControlSrc`, which
   *is* the markup file's path, so find the markup file whose `Inherits` names the containing type
   (`WebFormsIndex`) and apply the `local` convention to that. `Exact`.
2. **Single-assignment inference.** `GetConstantValue`, else one syntactic assignment with a constant
   right-hand side. Nothing beyond that. `Inferred`.
3. **Bounded proximity.** Candidates from the call-site file, nearest first, capped at eight and all
   returned. `Ambiguous`, or `Unknown` when nothing is reachable.

```csharp
internal enum RootConfidence
{
    Exact,      // literal, const, or a convention DNN itself applies — everything on
    Inferred,   // single-assignment — everything on
    Ambiguous,  // proximity — navigation/hover/completion on and capped; diagnostics off; rename refused
    Unknown,    // nothing reachable — empty, and no diagnostic
}
```

The two gates are not negotiable. A false "this key does not exist" on a key that resolves fine at
runtime is what gets a feature switched off wholesale, so diagnostics run at `Exact` and `Inferred`
only — and the missing-key rule ships behind a second switch on top of that
(`resources.missingKeyDiagnostic`). `prepareRename` returns null below `Inferred`, because renaming a
key across a guessed file set is silent corruption. References stay available at `Ambiguous`, marked
as proximity matches, since over-reporting is a nuisance rather than corruption.

There is a second refusal, and it comes from the reader rather than the resolver. `ResxReader` parses
with `Microsoft.Language.Xml`, a full-fidelity tree in which every character of the source is a node,
so a span already *is* the range in the buffer — nothing is rebuilt from line and column, and a
half-typed file still yields its entries rather than stopping at the first malformation.

What the reader still has to be careful about is that a span is the text *as written* while `Key` is
the text *as decoded*. A name written `A&amp;B` is the key `A&B` — which is what a `GetString` call
in C# passes — but it occupies five more characters than that on disk. So the two are not
interchangeable, and a caller rewriting a key in place checks `KeySpan.Length != Key.Length` before
editing: an entity-carrying name is spanned and navigable, but declines an in-place rename. No rename
beats a rename with a wrong span.

`XmlSpans` holds the two edges every XML reader in this repo shares: an attribute's value node spans
its quotes (so a value range is bounded by the quote *tokens*, which matters because the closing one
is synthesized and zero-width while the user is still typing), and `Decode` resolves the five entity
references XML predefines.

### Adding a convention or a lookup

`ResourcePresets` holds the built-in sets (`webforms`, `dnn`, `dotnet`), and omitting
`resources.preset` merges all three — safe because every lookup names a fully-qualified containing
type that simply does not resolve in a solution built on something else. Add a built-in there; add a
solution's own house helper in `roslynsense.json`, which layers onto the preset rather than replacing
it:

```jsonc
"resources": {
    "preset": "dnn",
    "lookups": [
        {
            "containingType": "Acme.Web.ModuleBase",
            "methodName": "T",
            "parameterTypes": ["string"],
            "keyIndex": 0,
            "rootSource": "containingType",
            "rootInterpretation": "virtualPath",
            "defaultKeySuffix": ".Text",
            "fallbacks": ["localShared", "global"]
        }
    ]
}
```

`parameterTypes` accepts either spelling of a built-in — `"string"` and `"System.String"` both name
the same parameter — because which one a configuration reaches for is house style, and a lookup that
binds nothing over it gives no sign of why.

`containingType` may be omitted, and then the lookup matches on the method name and signature alone.
That is for a codebase where each module declares its own wrapper — a `protected string
GetString(string key)` on the page itself, which no list of type names can keep up with — and it is
deliberately not the default: a bare name binds every method so called in the solution, including
ones that have nothing to do with resources. Give it a `parameterTypes`, which is then the only thing
telling the intended call apart from the rest.

Four different merge rules, each following from what the thing is: **lookups append** (a lookup is
identified by nothing, so there is nothing to replace); **conventions merge by `id`**, so redeclaring
`local` replaces that one and leaves `localShared` and `global` alone; **markup bindings append and
then dedupe**, since one is identified by everything it holds and the presets overlap; **overrides
replace the preset's set wholesale**, because a rank scheme only means anything as a whole. A
malformed entry warns and is dropped — a typo in one lookup must not leave the solution with no
navigation at all.

### Keys nothing writes out

Most keys in an `App_LocalResources` file have no call site anywhere. A page-wide localizer walks the
control tree once and asks for each control under its own `ID` — with DNN's default property,
`litStock.Text` — and a grid asks for one heading per column under a prefix and the column's
`UniqueName`, because a column is not a control and has no ID to be found by. Nothing in the solution
spells those keys, so find-references answered with the declaration and nothing else, and "what is
this string for" had no answer at all.

A binding is written as the key it produces, with the attribute in the middle:

```jsonc
"resources": {
    "markupBindings": ["Header[Control.UniqueName].Text", "[Control.ID].Header"]
}
```

Naming the whole key rather than a prefix is what makes it a setting rather than two hard-coded
shapes. A codebase that puts its fixed part on the other side writes `[Control.ID].Header`; one that
composes on both sides writes both. The `Control.` in front of the attribute name is optional and
ignored — it is there because that is how the shape reads to someone who has one of these, and
dropping it silently beats rejecting a pattern over punctuation. The `webforms` and `dnn` presets
ship `[Control.ID].Text`, `[Control.ID].ToolTip`, `Header[Control.UniqueName].Text` and
`Header[Control.Name].Text`.

The search runs **backwards, from the family to the one markup file it belongs to**, and that is not
an optimisation — it is the only correct direction. Every other producer gates on a text search for
the key, and a page that writes `UniqueName="Amount"` does not contain `HeaderAmount` anywhere, so a
forward scan would have to search for the bare column name instead: a common word, parsed across most
of a large site on every request. Inverting the convention that placed the family is exact, not a
guess — the family's directory *is* the sibling folder and its base name *is* the markup file's name,
because that is how the catalog grouped them. It also keeps the binding local, where an id matched
across the project would report every page that happens to reuse it. Fixed-name families such as
`SharedResources.resx` drop out of the same inversion, which is right: a key in a shared file would
otherwise bind to every same-named control on the site.

Before any of that, each pattern is asked what the attribute would have had to read for this key to
be the one it composed. A key no pattern could have produced — every key a call site does write out —
leaves having cost a few string comparisons rather than a parse.

A binding site is **a reference and never an edit**. The characters there are the control's name, not
the key: rewriting them to a new key would rename the control, orphan the field its designer declares
and break every line of code-behind that touches it. So a rename reports the site through
find-references and leaves it alone — the same trade the pack already makes for `meta:resourcekey`,
with the same consequence, that the markup goes on naming a key that has moved.
