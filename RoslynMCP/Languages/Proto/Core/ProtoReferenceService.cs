using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.Proto.Core;

/// <summary>One place a proto declaration is used, or one place it is declared.</summary>
/// <param name="Document">The C# document the span is in, or <see langword="null"/> for a row
/// standing in for a <c>.proto</c> declaration — see <see cref="ProtoReferenceService.FindUsagesAsync"/>
/// for when one is substituted.</param>
/// <param name="Text">The buffer <paramref name="Span"/> is measured against, carried only by a
/// <c>.proto</c> row, whose parse already has it. A C# row leaves it to <paramref name="Document"/>
/// so that a caller wanting nothing but locations never pays to read the text of every file the
/// search landed in.</param>
/// <param name="IsDefinition">Whether this is where the symbol is declared rather than used. The
/// <c>message</c> a generated class was built from and the hand-written implementation of a service
/// both come back this way, and a front-end usually wants to group them apart from the call sites.</param>
internal readonly record struct ProtoUsage(
    Document? Document, string FilePath, SourceText? Text, TextSpan Span, bool IsDefinition)
{
    public static ProtoUsage In(Document document, TextSpan span, bool isDefinition) =>
        new(document, document.FilePath ?? string.Empty, Text: null, span, isDefinition);

    /// <summary>The <c>.proto</c> line that produced a generated declaration, reported in its
    /// place.</summary>
    public static ProtoUsage Declaring(ProtoReference reference) =>
        new(Document: null, reference.FilePath, reference.Text, reference.Span, IsDefinition: true);
}

/// <summary>One place in a <c>.proto</c> file.</summary>
/// <param name="Declaration">What is written there. Carried rather than left for the caller to look
/// up again: the span is the declaration's own name span, so recovering it costs an offset lookup
/// that can only ever agree with what this already knew — or, if the two ever stop agreeing, hand
/// back a different declaration.</param>
internal readonly record struct ProtoReference(
    string FilePath, TextSpan Span, SourceText Text, ProtoDeclaration Declaration)
{
    /// <summary>The 1-based line the span starts on.</summary>
    public int Line => LineIndex + 1;

    /// <summary>The whole source line, for a report that shows the reference in context.</summary>
    public string LineText => Text.Lines[LineIndex].ToString().Trim();

    private int LineIndex => Text.Lines.GetLinePosition(Math.Min(Span.Start, Text.Length)).Line;
}

/// <summary>
/// Navigation from a caret in a <c>.proto</c> into the C# built from it, and back.
/// </summary>
/// <remarks>
/// <para>
/// There is no projection anywhere in this pack, so every feature here is plain Roslyn once the
/// proto declaration has been turned into a symbol set. The whole of the work is choosing that
/// set: one proto declaration is several C# symbols, and searching for any one of them alone
/// misses most of the answer. A service is a static holder class, an abstract base and a client;
/// an rpc is a virtual method, its overrides and four or five client overloads; a field is a
/// property and — in proto2, and for anything explicitly <c>optional</c> — a <c>Has…</c> property
/// and a <c>Clear…</c> method beside it.
/// </para>
/// <para>
/// Every search runs against <c>project.Solution</c> and not against the project. The point of the
/// pack is to get from a contract to the code that implements or calls it, and that code is in
/// another assembly by construction: the <c>.proto</c> lives in a shared contracts project, the
/// server implementation lives in the web project and the callers live in whatever consumes it.
/// A project-scoped search would find nothing but the generated code itself.
/// </para>
/// </remarks>
internal static class ProtoReferenceService
{
    /// <summary>
    /// Every C# symbol a find-usages on this caret has to search.
    /// </summary>
    /// <remarks>
    /// Asynchronous and solution-scoped because two of the answers are: a service's set includes
    /// the classes deriving from its base, which is where the hand-written implementation is, and
    /// an rpc's set includes the overrides of its virtual method for the same reason. Both are
    /// declarations in other projects, so neither can be read off the index.
    /// </remarks>
    public static Task<ImmutableArray<ISymbol>> SymbolSetForAsync(
        ProtoHit hit, ProtoGeneratedIndex index, Project project, CancellationToken ct) =>
        SymbolSetForAsync(hit.Target, hit.Symbol, index, project, ct);

    /// <summary>
    /// The same set for a declaration reached without a caret in the <c>.proto</c>.
    /// </summary>
    /// <param name="target">The declaration the search is about.</param>
    /// <param name="fallback">What to search when the declaration is of a kind with no set of its
    /// own, which is the caret's own symbol when there was a caret.</param>
    public static async Task<ImmutableArray<ISymbol>> SymbolSetForAsync(
        ProtoDeclaration? target, ISymbol? fallback, ProtoGeneratedIndex index, Project project,
        CancellationToken ct, TimeSpan? budget = null)
    {
        var symbols = ImmutableArray.CreateBuilder<ISymbol>();
        var solution = await SearchScopeAsync(project, ct, budget);

        switch (target)
        {
            case ProtoService service:
            {
                Add(symbols, index.ServiceTypeFor(service));
                Add(symbols, index.ServiceClientFor(service));

                if (index.ServiceBaseFor(service) is { } @base)
                {
                    Add(symbols, @base);
                    foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(
                        @base, solution, cancellationToken: ct))
                    {
                        Add(symbols, derived);
                    }
                }

                break;
            }

            case ProtoRpc rpc:
            {
                foreach (var method in index.MethodsFor(rpc))
                    Add(symbols, method);

                if (index.BaseMethodFor(rpc) is { } baseMethod)
                {
                    foreach (var @override in await SymbolFinder.FindOverridesAsync(
                        baseMethod, solution, cancellationToken: ct))
                    {
                        Add(symbols, @override);
                    }
                }

                break;
            }

            case ProtoField field:
            {
                if (index.PropertyFor(field) is { } property)
                {
                    Add(symbols, property);

                    // Built from the bound property's name, not re-derived from the proto field's.
                    // protoc builds all three members from one string, so `HasImageUrl` and
                    // `ClearImageUrl` follow from `ImageUrl` by construction; predicting them
                    // again would have to reproduce protoc's collision rule, and a miss there
                    // drops the presence members without any sign that it did. Which of the two
                    // exist is then read off the type — a proto3 field with no `optional` has
                    // neither, and neither does a message-typed field.
                    foreach (string name in (ReadOnlySpan<string>)["Has" + property.Name, "Clear" + property.Name])
                    {
                        foreach (var member in property.ContainingType.GetMembers(name))
                            Add(symbols, member);
                    }
                }

                break;
            }

            case ProtoOneof oneof when oneof.Parent is ProtoMessage owner:
            {
                if (index.TypeFor(owner) is { } type)
                {
                    // The one place a name is predicted rather than read back off a symbol: a oneof
                    // generates no anchor of its own — no descriptor index, no `…FieldNumber`, no
                    // `OriginalName` — so there is nothing to derive these from but its proto name.
                    foreach (string name in (ReadOnlySpan<string>)
                        [
                            ProtoNaming.OneofCasePropertyName(oneof),
                            ProtoNaming.OneofCaseEnumName(oneof),
                            ProtoNaming.ClearMethodName(oneof),
                        ])
                    {
                        foreach (var member in type.GetMembers(name))
                            Add(symbols, member);
                    }
                }

                break;
            }

            case ProtoMessage message:
                Add(symbols, index.TypeFor(message));
                break;

            case ProtoEnum @enum:
                Add(symbols, index.TypeFor(@enum));
                break;

            case ProtoEnumValue value:
                Add(symbols, index.MemberFor(value));
                break;

            default:
                Add(symbols, fallback);
                break;
        }

        return symbols.ToImmutable();
    }

    /// <summary>
    /// Every use of the caret's declaration across the solution, with protoc's own output kept out
    /// of the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One sweep over the whole symbol set, deduplicated by file and span. Roslyn's public
    /// reference API takes a single symbol, so the sweep is a loop rather than one call — but the
    /// results are merged before anyone sees them, which matters because the sets overlap by
    /// design: a call through the client's <c>CallOptions</c> overload and a call through its
    /// <c>headers</c> overload are two symbols at one call site only when the caller wrote both,
    /// and a virtual method's search already cascades into its overrides.
    /// </para>
    /// <para>
    /// A generated document is a pass-through, not a place. One <c>string label = 2;</c> becomes
    /// ten mentions of <c>Label</c> in the file protoc writes — the property, the copy constructor,
    /// <c>Clone</c>, <c>Equals</c>, <c>GetHashCode</c>, <c>WriteTo</c> twice, <c>CalculateSize</c>
    /// and both <c>MergeFrom</c> overloads — so without this a find-usages on a field answers with
    /// a wall of code nobody wrote, nobody may edit and the next build overwrites, with the one or
    /// two real call sites buried in it and truncated away entirely once <c>maxResults</c> bites.
    /// Hence the rule: a reference inside a generated document is not a reference, and a
    /// <i>declaration</i> inside one is reported as the <c>.proto</c> line it was generated from,
    /// on that declaration's name span so an editor highlights the identifier rather than the whole
    /// block. A generated declaration the reverse map cannot place is dropped instead — falling
    /// back to the generated line is the thing this exists to stop.
    /// </para>
    /// <para>
    /// The substituted lines are deduplicated on their own, because several generated members stand
    /// for one proto declaration: a service is a holder class, an abstract base and a client, and a
    /// field is a property beside its <c>Has…</c> and <c>Clear…</c>. Left to the span-based dedup
    /// each of them would report the same <c>.proto</c> line again.
    /// </para>
    /// <para>
    /// Which documents are generated is asked per project rather than of the caret's index alone —
    /// see <see cref="GeneratingIndexAsync"/> for the case that makes the difference.
    /// </para>
    /// <para>
    /// Usages only. <see cref="FindImplementationsAsync"/> searches for the classes deriving from a
    /// generated base, and the hand-written server it finds is not generated code however generated
    /// the base above it is — filtering there would delete the pack's best answer.
    /// </para>
    /// </remarks>
    /// <param name="budget">
    /// How long this caller may wait for the contract's consumers to be loaded. Left at nothing by
    /// the incidental callers — a code lens, a hover — and set only by the gesture a user made on
    /// purpose. See <see cref="SearchScopeAsync"/>.
    /// </param>
    public static Task<ImmutableArray<ProtoUsage>> FindUsagesAsync(
        ProtoHit hit, ProtoGeneratedIndex index, Project project, CancellationToken ct,
        TimeSpan? budget = null) =>
        FindUsagesAsync(hit.Target, hit.Symbol, index, project, ct, budget);

    /// <summary>
    /// What a deliberate find-usages or go-to-implementation is willing to wait for the contract's
    /// consumers: all of it.
    /// </summary>
    /// <remarks>
    /// A capped budget was tried and is wrong. Cross-project find-usages is the one answer this
    /// pack exists to give, and a cap turns "no callers" and "the callers are in projects that had
    /// not finished loading" into the same empty list — with nothing on screen to tell them apart.
    /// A search that takes a while is a search; a search that quietly under-reports is a bug the
    /// user acts on. The wait is bounded by the request's own cancellation token instead, so the
    /// editor can still abandon it, and it only ever runs for a gesture the user made on purpose:
    /// the incidental paths — a code lens resolving as the view scrolls, a hover — pass no budget
    /// at all and never start the load.
    /// </remarks>
    public static readonly TimeSpan ExplicitSearchBudget = SearchScopeService.ExplicitSearchBudget;

    /// <summary>
    /// The same sweep for a caret that started in C#: every use of the whole contract behind the
    /// symbol, not only of the one member Roslyn bound.
    /// </summary>
    /// <remarks>
    /// This is what a caret on a hand-written <c>override</c> is asking. One rpc is several C#
    /// symbols — the base's virtual, the client's four overloads and every override — and the
    /// callers of a service call the <em>client</em>, so a search from the server's override finds
    /// nobody: the call site and the method it implements have no C# relationship at all, and only
    /// the <c>.proto</c> knows they are the same rpc. Without this, find-references from the
    /// implementation reports the schema line and stops, which reads as "nothing calls this".
    /// </remarks>
    public static async Task<ImmutableArray<ProtoUsage>> UsagesOfAsync(
        ISymbol symbol, Project project, CancellationToken ct, TimeSpan? budget = null)
    {
        foreach (var candidate in CandidateProjects(symbol, project))
        {
            var index = await ProtoGeneratedIndex.GetAsync(candidate, ct);

            if (DeclarationOf(index, symbol, includeInherited: true) is not { } reference)
                continue;

            // The declaring project and not the caret's, so the sweep runs over the solution the
            // contract's consumers were loaded into.
            return await FindUsagesAsync(reference.Declaration, symbol, index, candidate, ct, budget);
        }

        return [];
    }

    /// <summary>
    /// Every document in <paramref name="solution"/> except the ones protoc generated for
    /// <paramref name="project"/>, or <see langword="null"/> when that is all of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweep below used to search the whole solution and then throw the generated hits away a
    /// few lines later, which is the rule this class is built on — a reference inside a generated
    /// document is not a reference. Throwing them away is not free: Roslyn has to locate each
    /// occurrence, bind it with a semantic model and return it across the engine boundary before
    /// anything here can decide it does not count.
    /// </para>
    /// <para>
    /// That is most of the work on the code-lens path. When only the contracts project is loaded,
    /// its protoc output <em>is</em> the solution — one generated file of about 1,100 lines and
    /// another of 220 — and a type like <c>Widget</c> appears in it 68 times, a field like
    /// <c>Label</c> 36. One rpc is five or six symbols, so a single lens binds those hundreds of
    /// occurrences several times over, and there are seventeen lenses.
    /// </para>
    /// <para>
    /// Only the index already in hand is consulted. Asking every project in the solution whether a
    /// document is generated would build an index per project — the expensive thing this is trying
    /// to avoid — so other projects stay in scope and the filter after the sweep remains their
    /// backstop. Definitions are unaffected either way: they are read off the symbol rather than
    /// off the documents searched, so a declaration in an excluded file is still reported against
    /// its <c>.proto</c> line.
    /// </para>
    /// </remarks>
    /// <summary>The computed scopes per solution snapshot: one rpc is five or six symbols searched
    /// together, and each was paying the full document enumeration for the same answer.</summary>
    private static readonly ConditionalWeakTable<
        Solution,
        System.Collections.Concurrent.ConcurrentDictionary<
            (ProjectId, ProtoGeneratedIndex), StrongBox<IImmutableSet<Document>?>>> s_searchScopes = new();

    private static IImmutableSet<Document>? DocumentsWorthSearching(
        Solution solution, Project project, ProtoGeneratedIndex index)
    {
        var byKey = s_searchScopes.GetValue(solution, _ => new());
        return byKey.GetOrAdd(
            (project.Id, index),
            _ => new StrongBox<IImmutableSet<Document>?>(ComputeScope(solution, project, index))).Value;
    }

    private static IImmutableSet<Document>? ComputeScope(
        Solution solution, Project project, ProtoGeneratedIndex index)
    {
        var kept = ImmutableHashSet.CreateBuilder<Document>();
        bool excludedAny = false;

        foreach (var candidate in solution.Projects)
        {
            bool isTarget = candidate.Id == project.Id;

            foreach (var document in candidate.Documents)
            {
                if (isTarget && index.IsGenerated(document))
                {
                    excludedAny = true;
                    continue;
                }

                kept.Add(document);
            }
        }

        // Nothing to exclude means the unscoped overload, which lets Roslyn pick its own document
        // set rather than being handed one it would have derived anyway.
        return excludedAny ? kept.ToImmutable() : null;
    }

    private static async Task<ImmutableArray<ProtoUsage>> FindUsagesAsync(
        ProtoDeclaration? target, ISymbol? fallback, ProtoGeneratedIndex index, Project project,
        CancellationToken ct, TimeSpan? budget = null)
    {
        var symbols = await SymbolSetForAsync(target, fallback, index, project, ct, budget);
        if (symbols.IsEmpty)
            return [];

        var solution = await SearchScopeAsync(project, ct, budget);
        var seen = new HashSet<(DocumentId, TextSpan)>();
        var declared = new HashSet<(string, TextSpan)>();
        var indexes = new Dictionary<ProjectId, ProtoGeneratedIndex>();
        var results = ImmutableArray.CreateBuilder<ProtoUsage>();

        // The searches run together; the merge below stays serial.
        //
        // One rpc is five or six C# symbols — the base's virtual, the client's overloads, every
        // override — and each search is an independent, read-only query over the same immutable
        // solution snapshot, so nothing is gained by making the second wait for the first. Roslyn
        // already parallelises across documents inside one search, but a solution this size does
        // not saturate a machine with one symbol's worth of work.
        //
        // Task.WhenAll preserves the order of its input, so the results are merged in exactly the
        // order the sequential loop produced them: `seen` and `declared` still decide the same
        // winner for a span two symbols both reach, and the output stays byte-identical rather than
        // reordering itself run to run.
        var scope = DocumentsWorthSearching(solution, project, index);

        var perSymbol = await Task.WhenAll(symbols.Select(symbol => scope is null
            ? SymbolFinder.FindReferencesAsync(symbol, solution, ct)
            : SymbolFinder.FindReferencesAsync(symbol, solution, scope, ct)));

        foreach (var referencedGroup in perSymbol)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var referenced in referencedGroup)
            {
                foreach (var location in referenced.Locations)
                {
                    if (!location.Location.IsInSource
                        || await GeneratingIndexAsync(location.Document, index, indexes, ct) is not null)
                    {
                        continue;
                    }

                    if (seen.Add((location.Document.Id, location.Location.SourceSpan)))
                    {
                        results.Add(ProtoUsage.In(
                            location.Document, location.Location.SourceSpan, isDefinition: false));
                    }
                }

                foreach (var location in referenced.Definition.Locations)
                {
                    if (!location.IsInSource || location.SourceTree is null)
                        continue;

                    if (solution.GetDocument(location.SourceTree) is not { } document)
                        continue;

                    if (await GeneratingIndexAsync(document, index, indexes, ct) is not { } generating)
                    {
                        if (seen.Add((document.Id, location.SourceSpan)))
                            results.Add(ProtoUsage.In(document, location.SourceSpan, isDefinition: true));

                        continue;
                    }

                    // The exact map and not the inherited walk: the declaration is in generated
                    // code, so the symbol protoc emitted is the one to ask about. Widening here
                    // would answer "the service" for plumbing like the client's NewInstance.
                    if (DeclarationOf(generating, referenced.Definition, includeInherited: false) is not { } declaration)
                        continue;

                    if (declared.Add((declaration.FilePath, declaration.Span)))
                        results.Add(ProtoUsage.Declaring(declaration));
                }
            }
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// The index that calls this document protoc's output, or <see langword="null"/> when nobody
    /// does and the document is therefore somebody's hand-written code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caret's index first, which knows the output generated beside the <c>.proto</c> the caret
    /// is in. It cannot know the rest of the solution, and the rest of the solution is where the
    /// second copy of the same noise lives: a downstream contracts project whose own <c>.proto</c>
    /// imports this one generates C# that names this message in its fields, its parser, its
    /// <c>WriteTo</c> and its <c>MergeFrom</c>, and that file is protoc's output every bit as much
    /// as the one next door. Asking only the caret's index lets all of it through — which is the
    /// wall of generated code this filter exists to remove, one project further out.
    /// </para>
    /// <para>
    /// The index that claims the document is returned rather than a yes/no, because a generated
    /// <i>declaration</i> then has to be mapped back to the <c>.proto</c> it came from and only the
    /// project that ran protoc on that file can do it.
    /// </para>
    /// <para>
    /// Memoized per project for the sweep. A solution-wide search lands in the same handful of
    /// projects over and over, and a project with no <c>.proto</c> in it answers from a cached file
    /// list without ever asking for a compilation.
    /// </para>
    /// </remarks>
    private static async ValueTask<ProtoGeneratedIndex?> GeneratingIndexAsync(
        Document document,
        ProtoGeneratedIndex index,
        Dictionary<ProjectId, ProtoGeneratedIndex> indexes,
        CancellationToken ct)
    {
        if (index.IsGenerated(document))
            return index;

        if (!indexes.TryGetValue(document.Project.Id, out var owner))
        {
            owner = await ProtoGeneratedIndex.GetAsync(document.Project, ct);
            indexes[document.Project.Id] = owner;
        }

        return owner.IsGenerated(document) ? owner : null;
    }

    /// <summary>
    /// The hand-written code that implements the caret's declaration: the server classes deriving
    /// from a service's base, or the methods overriding an rpc's.
    /// </summary>
    /// <remarks>
    /// Only a service and an rpc have implementations. A message is a sealed generated class and an
    /// enum is a generated enum — nothing derives from either, so answering an empty set is the
    /// truthful result rather than a gap.
    /// </remarks>
    public static async Task<ImmutableArray<ISymbol>> FindImplementationsAsync(
        ProtoHit hit, ProtoGeneratedIndex index, Project project, CancellationToken ct,
        TimeSpan? budget = null) =>
        await ImplementationsForAsync(
            hit.Target, index, await SearchScopeAsync(project, ct, budget), ct);

    /// <summary>
    /// The same answer for a caret that started in C#: the hand-written code implementing whatever
    /// <c>.proto</c> declaration the symbol was generated from.
    /// </summary>
    /// <remarks>
    /// What makes F12 on a generated client call useful rather than merely correct. The
    /// <c>.proto</c> line says what the contract is; this says where it is honoured, which is the
    /// file the user was looking for and the one place neither Roslyn nor the schema can point at
    /// on its own. Resolved through the same nearest-first project walk as
    /// <see cref="ProtoReferencesToAsync"/>, so the declaration behind the symbol and the
    /// implementation of that declaration are found in one index rather than two that could
    /// disagree.
    /// </remarks>
    public static async Task<ImmutableArray<ISymbol>> ImplementationsOfAsync(
        ISymbol symbol, Project project, CancellationToken ct, TimeSpan? budget = null)
    {
        foreach (var candidate in CandidateProjects(symbol, project))
        {
            var index = await ProtoGeneratedIndex.GetAsync(candidate, ct);

            if (DeclarationOf(index, symbol, includeInherited: true) is not { } reference)
                continue;

            return await ImplementationsForAsync(
                reference.Declaration, index, await SearchScopeAsync(candidate, ct, budget), ct);
        }

        return [];
    }

    private static async Task<ImmutableArray<ISymbol>> ImplementationsForAsync(
        ProtoDeclaration? target, ProtoGeneratedIndex index, Solution solution, CancellationToken ct)
    {
        var results = ImmutableArray.CreateBuilder<ISymbol>();

        switch (target)
        {
            case ProtoService service when index.ServiceBaseFor(service) is { } @base:
                foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(
                    @base, solution, cancellationToken: ct))
                {
                    Add(results, derived);
                }

                break;

            case ProtoRpc rpc when index.BaseMethodFor(rpc) is { } method:
                foreach (var @override in await SymbolFinder.FindOverridesAsync(
                    method, solution, cancellationToken: ct))
                {
                    Add(results, @override);
                }

                break;
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// The reverse direction: the <c>.proto</c> that declares a C# symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the pack contributes to a C# session — go-to-definition on a generated class
    /// landing on the <c>message</c> instead of on a file in <c>obj</c>, and the same for a caret
    /// on an <c>override</c> in a service implementation, which is why inherited symbols are
    /// included.
    /// </para>
    /// <para>
    /// The span comes from re-parsing the <c>.proto</c> rather than from the index, so it is
    /// measured against the text the editor is showing. The index binds by fully-qualified proto
    /// name for exactly this reason: an edit that has moved every declaration in the file still
    /// resolves, because the name did not move.
    /// </para>
    /// <para>
    /// <paramref name="project"/> is where the caret is, which is the one project that must not
    /// decide the index — see <see cref="CandidateProjects"/>.
    /// </para>
    /// </remarks>
    public static async Task<ImmutableArray<ProtoReference>> ProtoReferencesToAsync(
        ISymbol symbol, Project project, CancellationToken ct)
    {
        foreach (var candidate in CandidateProjects(symbol, project))
        {
            var index = await ProtoGeneratedIndex.GetAsync(candidate, ct);

            if (DeclarationOf(index, symbol, includeInherited: true) is { } reference)
                return [reference];
        }

        return [];
    }

    /// <summary>
    /// The projects whose index could know this symbol, nearest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the project the caret sits in, which is the mistake that looks like a working feature.
    /// A <c>.proto</c> lives in a contracts project by construction and the caret asking about it
    /// sits in a consumer, so an index built where the caret is scans a project holding no protoc
    /// output, comes back empty, and lets go-to-definition fall through to Roslyn — which lands in
    /// <c>obj</c>, the one place this exists to keep it out of. The symbol says which assembly
    /// compiled it, and that is the project that ran protoc.
    /// </para>
    /// <para>
    /// The bases are candidates too, because the answer is wanted for a caret on the
    /// <c>override</c> in a hand-written service as well as on the generated virtual, and those
    /// two are in different projects by the same construction. Walked in the shape
    /// <see cref="ProtoGeneratedIndex.DeclarationFor"/> walks, so a project reached here is one an
    /// index could actually answer for.
    /// </para>
    /// <para>
    /// The caret's project stays, last: a symbol from a NuGet-packaged contracts assembly resolves
    /// to no project at all, and answering nothing for it is correct — the <c>.proto</c> is not in
    /// the solution — but a caret genuinely sitting beside its own generated code would otherwise
    /// lose an answer it used to get.
    /// </para>
    /// </remarks>
    private static List<Project> CandidateProjects(ISymbol symbol, Project project)
    {
        var solution = project.Solution;
        var seen = new HashSet<ProjectId>();
        var results = new List<Project>();

        void Consider(ISymbol? candidate)
        {
            if (candidate?.ContainingAssembly is { } assembly
                && solution.GetProject(assembly) is { } owner
                && seen.Add(owner.Id))
            {
                results.Add(owner);
            }
        }

        var definition = symbol.OriginalDefinition;
        Consider(definition);

        for (var method = (definition as IMethodSymbol)?.OverriddenMethod;
             method is not null;
             method = method.OverriddenMethod)
        {
            Consider(method);
        }

        for (var type = (definition as INamedTypeSymbol)?.BaseType;
             type is not null;
             type = type.BaseType)
        {
            Consider(type);
        }

        if (seen.Add(project.Id))
            results.Add(project);

        return results;
    }

    /// <summary>
    /// The <c>.proto</c> line a generated symbol was generated from, as a location in the file
    /// rather than as the index's own record of it.
    /// </summary>
    /// <remarks>
    /// The one place both directions meet: the go-to-definition contribution and the substitution
    /// <see cref="FindUsagesAsync"/> makes for a generated declaration are the same lookup, so the
    /// line F12 opens and the line find-usages lists cannot drift apart. The span is taken from a
    /// fresh parse for the reason given on <see cref="ProtoDeclarationRef"/>: the index binds by
    /// fully-qualified proto name, so an edit that moved every declaration in the file still
    /// resolves, and the span that comes back is measured against the text being shown.
    /// </remarks>
    private static ProtoReference? DeclarationOf(
        ProtoGeneratedIndex index, ISymbol symbol, bool includeInherited)
    {
        if (index.DeclarationFor(symbol, includeInherited) is not { } reference)
            return null;

        if (ProtoDocumentService.GetParse(reference.FilePath) is not { } parse)
            return null;

        if (parse.FindByFullName(reference.FullName) is not { } declaration)
            return null;

        return new ProtoReference(reference.FilePath, declaration.Name.Span, parse.Text, declaration);
    }

    /// <summary>
    /// Whether the project could contain anything generated from a <c>.proto</c> — a metadata
    /// lookup, not I/O.
    /// </summary>
    /// <remarks>
    /// Every generated message implements <c>IMessage</c> and every generated file references the
    /// runtime, so a project that cannot resolve the interface cannot hold protobuf code. This is
    /// the gate that keeps a solution-wide search from paying for the pack in the projects that
    /// have nothing to do with it.
    /// </remarks>
    public static async Task<bool> HostsProtobufAsync(Project project, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        return compilation?.GetTypeByMetadataName("Google.Protobuf.IMessage") is not null;
    }

    /// <summary>
    /// The solution a search from a <c>.proto</c> has to run against: the one holding the project
    /// that compiles the file, widened with the projects that consume it. The mechanism lives in
    /// <see cref="SearchScopeService"/>, shared with C# navigation; the rationale — lazy loading
    /// follows references, which from a contracts project points away from every answer — is
    /// documented there.
    /// </summary>
    /// <summary>The search scope, for a probe that measures the phases of a lens resolve.</summary>
    internal static Task<Solution> SearchScopeForTestsAsync(Project project, CancellationToken ct) =>
        SearchScopeAsync(project, ct);

    private static Task<Solution> SearchScopeAsync(
        Project project, CancellationToken ct, TimeSpan? budget = null) =>
        SearchScopeService.WidenAsync(project, budget, ct);

    private static void Add(ImmutableArray<ISymbol>.Builder symbols, ISymbol? symbol)
    {
        if (symbol is not null && !symbols.Contains(symbol, SymbolEqualityComparer.Default))
            symbols.Add(symbol);
    }
}
