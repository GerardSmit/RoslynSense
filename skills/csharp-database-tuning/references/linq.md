# LINQ and ORM query performance

The mistakes in this file are made in C#, not SQL: LINQ queries through EF Core, EF6, or
LINQ to SQL that produce bad SQL, too much SQL, or client-side work that never reaches the
database at all. Distilled from the Microsoft Learn EF Core performance docs, the EF6
performance whitepaper, dotnet/efcore and dotnet/SqlClient issue history, and field writeups.
The engine-side halves of these problems — what the resulting plan looks like and how to index
for it — live in [sql-server.md](sql-server.md) and [postgres.md](postgres.md).

**Contents**
1. [Read the SQL first](#read-the-sql-first)
2. [Materialization and tracking](#materialization-and-tracking)
3. [Loading related data](#loading-related-data)
4. [Predicates that translate into scans](#predicates-that-translate-into-scans)
5. [Contains(), dynamic queries, and the plan cache](#contains-dynamic-queries-and-the-plan-cache)
6. [Query shapes that multiply work](#query-shapes-that-multiply-work)
7. [Pagination](#pagination)
8. [Writes: SaveChanges and its loops](#writes-savechanges-and-its-loops)
9. [Context lifetime and cold start](#context-lifetime-and-cold-start)

---

## Read the SQL first

Every diagnosis here starts by seeing what the ORM actually sent — never reason from the LINQ:

| ORM | Getting the SQL |
|-----|-----------------|
| EF Core | `query.ToQueryString()` (5.0+); `optionsBuilder.LogTo(...)` (5.0+); an `IDbCommandInterceptor` |
| EF6 | `query.ToString()` — placeholders like `@p__linq__0`, no values, and only on the `IQueryable` (not `First()` etc.); `db.Database.Log = s => ...` logs executed commands *with* parameter values |
| LINQ to SQL | `dc.GetCommand(query).CommandText`; `dc.Log = writer` (don't leave it attached in production) |

Before writing any of that instrumentation, check whether the app is already logging its SQL —
EF Core's `LogTo`/console logger, an EF6 `Database.Log` hook, or a LINQ to SQL `dc.Log` is
often wired up in development configuration already. For an app started with **RunProject** (or
one the editor launched), **GetProjectOutput** reads the captured output: exercise the slow
path, then read the SQL out of the log instead of adding code. The catch is *where* the hook
writes: `Console`/`ILogger`-console output lands in stdout and is readable that way, but a hook
pointed at `Debug.WriteLine` reaches only an attached debugger — on .NET Framework, attaching
the editor's debugger routes it into the same readable log; absence of SQL in the output is not
proof the query didn't run.

Then run that SQL through `DbQuery` with `includeExecutionPlan: true` and read the plan with
`DbPlan` — the ORM-generated form, with its parameters, not a cleaned-up paraphrase. To go the
other direction — from a slow plan found in Query Store or `pg_stat_statements` back to the C#
call site — tag the query: `TagWith("...")` (EF Core 2.2+) prepends a SQL comment,
`TagWithCallSite()` (6.0+) stamps file and line. The tag is part of the SQL text, so it caches
a separate plan per distinct tag — never interpolate per-request values into one.

Two symptoms tell you the problem never reached the database and no plan will explain it:

- **The logged SQL has no WHERE/TOP although the C# does.** Somewhere the query became
  `IEnumerable<T>`, and every operator after that point binds to `Enumerable`, not `Queryable`:
  LINQ-to-Objects over the whole table, compiling without a warning. The blatant spelling is
  `.ToList().Where(...)` — materialize the whole table, then filter in memory. The quiet ones
  do the same thing: a variable or repository return typed `IEnumerable<T>` (assignment alone
  switches how later operators bind — no call needed), and an `.AsEnumerable()` inserted to
  appease a translation error, which "fixes" the exception by moving everything downstream of it
  client-side. Keep composition on `IQueryable<T>` until the final materializer
  (`ToList`/`First`/`Count`/…). Besides the missing WHERE/TOP, the tell is memory and latency
  that scale with table size rather than result size. (EF Core 3.0+ throws on untranslatable
  operators instead of silently pulling the table — 2.x only logged a client-eval warning — but
  `.AsEnumerable()` recreates the pre-3.0 silent behavior.)
- **A burst of identical single-key SELECTs.** N+1 — see the next two sections.

## Materialization and tracking

A tracked entity costs an `EntityEntry`, a snapshot of every property, and identity-map
registration — client-side CPU and memory the SQL never shows. On read-only paths this is pure
waste, and it compounds: EF6's `DetectChanges` walks *all* tracked entities, so a fat tracker
slows every subsequent operation.

- **Read-only queries**: `AsNoTracking()` (EF Core and EF6). EF Core 5.0 adds
  `AsNoTrackingWithIdentityResolution()` when repeated entities should share one instance.
  Tracking is *required* only when the same instances will be modified and saved. LINQ to SQL
  has `ObjectTrackingEnabled`, but do not reach for it: flipping it also disables deferred
  loading and makes `SubmitChanges` throw, so on any DataContext that other code touches it
  breaks behavior far from the query you were tuning. Project instead — a `select new` is
  untracked in every ORM without changing the context's semantics.
- **Better than no-tracking: don't materialize entities.** A `Select` into a DTO or anonymous
  type is never tracked, fetches only the named columns, and composes into one SQL query — but
  only when no entity type appears in the result: an entity instance embedded *inside* the
  projection (`new { Order = o, ... }`) is still tracked, with all its columns. Full
  entities drag every mapped column — including the `NVARCHAR(MAX)` body nobody reads — through
  I/O, network, and materialization. Reserve entity loading for update paths.
- **Never serialize entities.** A JSON serializer walks every navigation property; with lazy
  loading enabled it issues a query per property per row (or pulls half the database, or cycles).
  This is the worst N+1 variant because it lives in framework code, far from any query. Map to
  DTOs before the serializer sees the object.

## Loading related data

Lazy loading turns a loop over parents into one query per touched navigation. Its defaults
differ per ORM and are worth stating exactly: EF Core never lazy-loads unless opted in —
`UseLazyLoadingProxies()` (Microsoft.EntityFrameworkCore.Proxies, 2.1+) with `virtual`
navigations, or an injected `ILazyLoader`; EF6 lazy-loads *by default* on any `virtual`
navigation; LINQ to SQL defers `EntitySet`/`EntityRef` loading by default. Server-side each
query is cheap, so no single plan capture looks slow — the tell is the flood of identical
parameterized SELECTs in the log. The fixes, in order of preference:

1. **Projection** — one query, only the needed columns, no tracking. Usually strictly better.
2. **Eager loading** — EF `Include`/`ThenInclude` (EF6 nests via `Include(o => o.Lines.Select(l => l.Product))`);
   EF Core 5.0 adds filtered includes — `Include(o => o.Lines.Where(...))`, allowing `Where`,
   ordering, `Skip`, and `Take` inside the include. LINQ to SQL uses `DataLoadOptions.LoadWith`,
   assigned to the DataContext *before* the first query — it throws once results exist.
3. **Explicit loading** for the occasional case: `db.Entry(order).Collection(o => o.Lines).Load()`.

Eager loading has its own failure mode: **cartesian explosion**. Even a single collection
Include is a JOIN that repeats the parent's columns on every child row; two *sibling*
collection Includes join into one statement whose row count is the *product* of the
collections. EF Core 5.0 logs `MultipleCollectionIncludeWarning` when one query loads more
than one collection (by Include or projection), and `AsSplitQuery()` (5.0+; 6.0 extends it to
collections projected in `Select`) issues one query per collection instead — at the price of
one round trip each and no consistency between them unless wrapped in a serializable or
snapshot transaction. EF6 has no split query: it always builds one statement, and with several
collection Includes that statement grows into nested subqueries glued with `UNION ALL`,
routinely running to thousands of lines — slow to generate, slow to compile, and flooding the
wire with duplicated parent data. Split it manually — load parents, then
`db.Posts.Where(p => blogIds.Contains(p.BlogId)).Load()` into the same context and let
relationship fixup stitch the graph; fixup needs tracked entities, so this pattern excludes
`AsNoTracking`. LINQ to SQL is widely reported (field experience more than MSDN) to join only
one collection level into the main statement, with deeper `LoadWith` levels degrading back to
per-parent queries — past one level, project instead.

## Predicates that translate into scans

The sargability rules of the engine references apply unchanged; what is ORM-specific is which
innocent C# produces the offending SQL. The translations worth knowing by heart:

| C# | SQL (SQL Server) | Index seek? |
|----|------------------|-------------|
| `x.Name.StartsWith(s)` | `LIKE @p + '%'` (EF Core 8+ folds the `%` and wildcard-escaping into the parameter value itself: `LIKE @p ESCAPE '\'`) | **Yes** |
| `x.Name.Contains(s)` / `EndsWith(s)` | `LIKE '%' + @p + '%'` | No — leading wildcard; needs full-text (SQL Server) or `pg_trgm` (PostgreSQL) |
| `x.Name.ToLower() == s` | `LOWER([Name]) = @p` | No — function on the column. On SQL Server the default collation is already case-insensitive: drop the wrappers. On PostgreSQL, an expression index on `LOWER(name)` makes exactly this shape seek |
| `x.Date.Year == 2026` | `DATEPART(year, [Date]) = ...` | No — rewrite as a half-open range |
| `x.Date.Date == d` | `CONVERT(date, [Date]) = @d` | SQL Server special-cases this one cast into a range seek — but nothing else, and no other engine does; use the range anyway |
| `x.Id.ToString() == s` | `CONVERT(varchar(11), [Id]) = @p` | No — parse the parameter in C# instead |
| `string.Equals(a, b, StringComparison...)` | Does not translate (EF Core throws) | — |

The **half-open range** is the universal date fix in every ORM:
`x.OrderDate >= start && x.OrderDate < end` — bare column, seeks everywhere, correct at every
datetime precision. It replaces `.Date`, `.Year`, EF6's `DbFunctions.TruncateTime`, and
`EF.Functions.DateDiffDay` (whose column-inside-DATEDIFF form never seeks). The same
function-on-column logic makes `EF.Functions.Collate(x.Name, ...)` non-sargable: an explicit
`COLLATE` on the column discards the index's own collation order.

**The NVARCHAR parameter trap** deserves its own paragraph because every .NET ORM falls into it
the same way: C# strings become `NVARCHAR` parameters, and against a `VARCHAR` column SQL
Server's type precedence converts *the column* — `CONVERT_IMPLICIT` in the plan, seek dead,
`PlanAffectingConvert` warning. The fix is always telling the ORM the column's real type:

| ORM | Fix |
|-----|-----|
| EF Core | `.IsUnicode(false)` / `[Unicode(false)]` (6.0+) or `.HasColumnType("varchar(50)")` |
| EF6 | `.HasColumnType("varchar").HasMaxLength(50)` or `[Column(TypeName = "varchar")]`; in-query, `DbFunctions.AsNonUnicode(s)` |
| LINQ to SQL | `[Column(DbType = "VarChar(50)")]` in the mapping |

Always set `HasMaxLength` too — unbounded strings map to `nvarchar(max)`-typed parameters,
which carry their own plan-quality penalties.

Two subtler translation costs:

- **Null-semantics compensation.** C# `==` is two-valued; SQL `=` is not. EF Core preserves C#
  semantics by generating `[A] = [B] OR ([A] IS NULL AND [B] IS NULL)` shapes when two nullable
  *columns* are compared (worse for `!=`) — OR branches that can defeat seeks. For nullable
  *parameters* it instead sniffs the runtime value and emits plain `[Col] = @p` or
  `[Col] IS NULL` — different SQL per null state, so two plan-cache entries per such parameter,
  deliberately. EF6 has no sniffing: it wraps even parameter comparisons in the OR/IS NULL
  compensation. Declare columns non-nullable when they are; that alone removes the compensation.
  `UseRelationalNulls(true)` (EF Core) and `Configuration.UseDatabaseNullSemantics = true`
  (EF6, default false) trade the bloat for raw SQL semantics — your LINQ then stops meaning
  what the C# says around NULLs, so treat it as a measured, documented decision.
- **Value converters** (EF Core `HasConversion`). A converted property reliably translates for
  `==`/`!=` against a literal or parameter and little else; methods and range operators on it
  fail to translate or go non-sargable, and an order-changing conversion (number stored as
  string) breaks range predicates outright. Keep columns you filter or sort by primitively
  mapped. Enum-to-string is the common instance — equality is fine (mind the varchar trap
  above), but `>=` on it compares alphabetically.

## Contains(), dynamic queries, and the plan cache

The ORM decides what becomes a parameter and what is inlined as a literal, and that decision is
what the server's plan cache keys on. Closure variables become parameters in all three ORMs —
good. The exceptions are where plan-cache pollution comes from:

- **`ids.Contains(x.Id)`** is the classic. EF Core ≤ 7 and EF6 inline the values as literals
  (`IN (1, 2, 3)`), and LINQ to SQL sends one parameter per element (`IN (@p0, @p1, ...)` — it
  hits SqlClient's 2100-parameter cap on big lists), so every distinct list length (or content)
  is a distinct SQL text — a compiled plan per shape, cache bloat, compile CPU. EF Core 8 on SQL
  Server switched to a single JSON parameter probed with `OPENJSON` — one plan for every list,
  but the estimator guesses ~50 rows for it, which can flip a good seek plan on skewed data, and
  it needs compatibility level ≥ 130 (`UseCompatibilityLevel(120)` reverts). Per-query
  `EF.Constant(ids)` (8+) forces inlining back; `EF.Parameter(ids)` and the context-wide
  `TranslateParameterizedCollectionsToConstants()` option arrive in EF Core 9. EF Core 10
  defaults to the middle ground — one scalar parameter per element (`IN (@ids1, @ids2, ...)`),
  padded up to bucket sizes so nearby list lengths share SQL text — with
  `UseParameterizedCollectionMode(ParameterTranslationMode...)` globally and
  `EF.MultipleParameters(ids)` per query. Npgsql has always sent `= ANY(@ids)` with a native
  array parameter — one plan, no saga. On EF6/LINQ to SQL the escape hatch for large lists is a
  TVP or temp-table join. EF6 additionally never caches the *LINQ compilation* of a Contains
  query (the whitepaper calls the collection "volatile") — it re-translates on every call.
- **EF6 inlines `Skip`/`Take` integers as constants** — every page number is a new plan. The
  lambda overloads fix it: `.Skip(() => offset).Take(() => size)`.
- **Dynamically built expression trees** (predicate builders, `Expression.Constant(userValue)`)
  embed a different literal per call — distinct SQL text every time, recompilation client-side
  *and* server-side. Detect with the EF Core EventCounter `compiled-query-cache-hit-rate`
  (5.0+, healthy ≈ 100%). Build member accesses over a closure object the way the compiler
  does, or wrap values in `EF.Parameter` (EF Core 9+), so the funcletizer parameterizes them.
- **Compiled queries** are the inverse concern. EF Core caches compilation keyed on tree
  structure, so `EF.CompileQuery` only skips the cache lookup — measurable in extreme hot paths
  only. LINQ to SQL has *no* automatic translation cache, so there `CompiledQuery.Compile` into
  a static field is the single biggest win available — commonly severalfold on hot queries;
  compiling per call is worse than not compiling.

## Query shapes that multiply work

- **`FirstOrDefault` inside a `Select`** ("latest note per order") becomes a correlated
  `TOP(1)` subquery — or `OUTER APPLY` when several columns are taken — executed per outer row.
  Fine with a covering index on (correlation key, order key); brutal without. Project the whole
  needed object in *one* `FirstOrDefault(...)` so the subquery appears once — projecting each
  property through its own `FirstOrDefault` has repeated the subquery per property
  (dotnet/efcore #20826). For top-1-per-group, EF Core 6+ translates `GroupBy` + `First` into a
  `ROW_NUMBER() OVER (PARTITION BY ...)` shape the engine can hash instead of loop.
- **`GroupBy` translates only into aggregate shapes.** `GroupBy(...).Select(g => new { g.Key,
  Count = g.Count() })` is a real `GROUP BY`; materializing the groups themselves reads every
  row of every group however it is spelled (and pre-3.0 EF Core silently grouped client-side —
  a reason to distrust inherited "it worked on the old version" queries). If an aggregate
  answers the question, aggregate in the projection.
- **`Any()` beats `Count() > 0`** in every ORM: `EXISTS` stops at the first row, `COUNT(*)`
  visits them all.
- **`Distinct()` papering over join fan-out**: a join written where a semi-join was meant
  multiplies rows, then pays a hash/sort to de-duplicate them. Express membership as
  `Any()`/`Contains` so the SQL is `EXISTS` and there is nothing to de-duplicate.
- **`Union` vs `Concat`**: `Union` is `UNION` — an implicit DISTINCT over the combined set.
  When duplicates are impossible or acceptable, `Concat` (`UNION ALL`) skips it. Push `Where`
  into each branch, not after the set operation.
- **`OrderBy` on a computed expression** (`x.First + " " + x.Last`, a CASE from a conditional)
  cannot be served by any index: full sort, memory grant, spill risk — per page, under paging.
  Order by raw indexed columns with `ThenBy`, or index a persisted computed column. The
  degenerate case is `OrderBy(x => Guid.NewGuid())` → `ORDER BY NEWID()`: a GUID generated and
  sorted per row of the whole table.
- **Global query filters** (EF Core `HasQueryFilter`) add their predicate to every query, join,
  and Include touching the entity — invisible at the call site. Indexes on the filtered tables
  need the filter column, or a filtered index matching it (works for the constant soft-delete
  form; a parameterized tenant filter cannot use one). Check any unexplained predicate in the
  logged SQL against the model's filters before hunting elsewhere; `IgnoreQueryFilters()` opts
  out per query.

## Pagination

`Skip`/`Take` without `OrderBy` is nondeterministic — pages repeat and drop rows. EF Core logs
`RowLimitingOperationWithoutOrderByWarning`, EF6 throws `NotSupportedException` ("The method
'Skip' is only supported for sorted input") — and the usual "fix", `.AsEnumerable()` first,
pulls the table — while LINQ to SQL translates silently with an unspecified order. Always order
by a unique or tie-broken key.

Offset paging costs grow with depth: `OFFSET 100000` (or LINQ to SQL's `ROW_NUMBER` translation
of the same) processes and discards every skipped row, page after page. For deep or infinite
scrolling, use keyset pagination — `Where(x => x.Id > lastSeen).OrderBy(x => x.Id).Take(n)` —
which seeks straight to the page at constant cost. The direction caveats in
[postgres.md](postgres.md) §Query patterns apply to every engine.

## Writes: SaveChanges and its loops

- **Load-modify-save over a set** reads every row over the wire, snapshots it, diffs it, and
  emits per-row UPDATEs. EF Core 7.0's `ExecuteUpdate`/`ExecuteDelete` do it in one set-based
  statement with zero materialization — noting they bypass the change tracker, concurrency
  tokens, and `SaveChanges` interceptors, and run immediately. Earlier EF Core and EF6 fall
  back to `ExecuteSqlInterpolated` / `Database.ExecuteSqlCommand`. Deleting by key does not
  require loading the entity first: attach a stub and remove it.
- **`SaveChanges` inside the loop** turns one round trip into N. Mutate everything, save once —
  it is also one transaction instead of N.
- **EF6's quadratic Add trap**: `Add` (also `Attach`, `Find`, `Entry`) runs `DetectChanges`
  over *all* tracked entities — O(n) each, O(n²) for a loop of Adds. `AddRange` detects once
  for the whole batch, or wrap the loop in `AutoDetectChangesEnabled = false` (re-enable in a
  `finally`). EF6 also sends one round trip per row on save — no statement batching — so real
  bulk loads belong to `SqlBulkCopy`, in any EF version. EF Core batches statements and does
  not run full DetectChanges on `Add`, but the tracker still grows: insert in chunks with
  `ChangeTracker.Clear()` (5.0+) between them.
- **Transactions should span the database work only.** A transaction (or a tracked context)
  held across HTTP calls or message handling holds locks and version-store entries for the
  duration — the lock-queue and blocking sections of the engine references are frequently
  downstream of exactly this.

## Context lifetime and cold start

A `DbContext`/`DataContext` is a unit of work, not an application service. A long-lived one
accumulates tracked entities forever: memory climbs, every `DetectChanges` slows, and tracking
queries return the *cached* instance's stale values rather than the database's. Scope it per
request or operation; `AddDbContextPool` (EF Core 2.0+) removes the construction cost where
throughput justifies it. The contexts are cheap to make — the expensive machinery is cached
per model, not per instance.

Except, in EF6, the *first* instance: on first use EF6 builds the model and runs mapping view
generation (minutes on large models with deep inheritance or independent associations), and
JIT-compiles the not-NGen'd EntityFramework.dll. If cold start hurts: pre-generated views,
EF 6.2's `SetModelStore(new DefaultDbModelStore(...))` to cache the built model on disk, `ngen
install EntityFramework.dll`, and in production replace the default database initializer with
`Database.SetInitializer<TContext>(null)` and out-of-band migrations — the initializer's
existence and model-hash checks otherwise tax every AppDomain recycle.

Streaming versus buffering is mostly a memory question — `ToList` buffers, iterating the
query streams but holds the connection and reader open for the loop — with two traps worth
knowing. Issuing a second query on the same context mid-iteration (a lazy load inside the
loop) needs MARS on SQL Server; restructure rather than enable it. And EF6 quietly switches
from streaming to full buffering when a retrying execution strategy is configured, because
retry requires replayability — an app-wide `SqlAzureExecutionStrategy` plus a huge result set
buffers the table in memory.

One driver-level trap masquerades as an EF problem: **Microsoft.Data.SqlClient reads large
values (`NVARCHAR(MAX)`/`VARBINARY(MAX)` in the megabytes) pathologically slowly in async
mode** — seconds for what sync reads in milliseconds, superlinear in size
(dotnet/SqlClient #593). SqlClient 7.0 ships a rewritten async path (packet multiplexing) that
fixes it, but opt-in: both `Switch.Microsoft.Data.SqlClient.UseCompatibilityAsyncBehaviour` and
`...UseCompatibilityProcessSni` must be set to `false`; the default behavior is still the slow
one. If `ToListAsync` is far slower than the same SQL elsewhere and the rows carry big blob or
JSON columns, this is why: project the blob out of the async path, fetch it synchronously, or
opt into the fixed path.
