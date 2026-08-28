# PostgreSQL performance tuning

Distilled for application developers (after github.com/Jeffallan/claude-skills postgres-pro, MIT),
organised by what you are looking at. Replication, backup, and server sizing are deliberately out
of scope — this is about making queries fast and transactions safe from the application side.

**Contents**
1. [Reading an EXPLAIN plan](#reading-an-explain-plan)
2. [Sargability — predicates that kill index use](#sargability)
3. [Indexing playbook](#indexing-playbook)
4. [Statistics and the planner](#statistics-and-the-planner)
5. [JSONB](#jsonb)
6. [Query patterns](#query-patterns)
7. [Workload-level analysis](#workload-level-analysis)
8. [VACUUM, bloat, and MVCC](#vacuum-bloat-and-mvcc)
9. [Locking and deadlocks](#locking-and-deadlocks)

---

## Reading an EXPLAIN plan

`DbQuery` with `includeExecutionPlan: true` runs `EXPLAIN (ANALYZE, BUFFERS, ...)` — note that on
PostgreSQL this returns the plan **only**, not the rows, and the query really executes (wrap DML in
a transaction you roll back). `DbPlan summary`/`warnings`/`operators` then read it; `query` takes
JSONPath for anything structural.

What the numbers mean:

- `cost=0.42..1234.56` — planner's *startup..total* estimate in abstract units; never compare it to
  milliseconds, only to other plans of the same query.
- `actual time=0.1..45.6 rows=9876 loops=120` — time-to-first-row..total, **averaged per loop**:
  multiply by `loops` for true totals. `rows` is a per-loop average too, *rounded to a whole
  number*, so `rows=0` with high `loops` can still be thousands of rows in total. A node showing
  `rows=1 loops=50000` is fifty thousand executions — the N+1 shape inside one query. (Rows
  Removed by Filter is per-loop averaged the same way.)
- `Buffers: shared hit=... read=...` — `hit` came from PostgreSQL's cache, `read` from outside it
  (OS cache or disk). A fast query that is occasionally slow with identical plans is usually a
  cold cache (`read` where `hit` normally is), not a plan problem. With `track_io_timing` on, an
  `I/O Timings` line says whether those reads actually cost wall time.
- `Execution Time` excludes `Planning Time`, and a triggered JIT compile adds a `JIT:` block whose
  timings sit inside execution — when a query is slower than its plan nodes explain, check those
  two lines. A cost misestimate can trip `jit_above_cost` and make JIT compilation the whole cost
  of a query that returns three rows.
- Estimated `rows` vs actual `rows` is the master diagnostic, exactly as on SQL Server: a large
  mismatch (10×+ compounding through joins) means the planner chose join strategy and memory on
  wrong information. Fix the estimate (§4) before tuning what it feeds.

The recurring villains:

- **Seq Scan on a large table with a filter** — the predicate is non-sargable (§2) or the index is
  missing (§3). On a small table, or when returning most rows, a Seq Scan is *correct* — don't
  fight it.
- **Nested Loop with high loops** on the inner side: fine when the inner is an index scan with few
  rows per loop; a disaster when the row estimate was 1 and reality is thousands.
- **Sort Method: external merge  Disk: ...kB** — the sort spilled past `work_mem` to disk. Fix the
  query (sort fewer rows/columns, or an index that provides the order) before reaching for a
  `work_mem` increase, which multiplies across every concurrent sort and hash.
- **Hash Batches > 1** on a Hash node — the hash join spilled the same way.
- **Rows Removed by Filter** in the thousands next to a small `rows` output: the access path reads
  far more than it returns — the index doesn't cover the selective part of the predicate.
- **Heap Fetches** on an Index Only Scan: the visibility map is stale, so the "index only" scan
  visits the heap anyway. VACUUM the table (§8); on a high-churn table Index Only Scans may never
  pay off.
- **Filter applied at the wrong join level** — a predicate you wrote in `ON` of a LEFT JOIN vs in
  `WHERE` produces different semantics *and* different plans; check where the planner placed it.
- **Every partition scanned** on a partitioned table: plan-time pruning simply omits partitions;
  runtime pruning shows `Subplans Removed: N` or per-partition `(never executed)`. A scan node for
  each partition means the predicate couldn't prune — usually the partition key is wrapped in an
  expression or the value is only known inside a join.

## Sargability

Same principle as any engine: the column must stand bare for an index to serve the predicate. The
PostgreSQL twist is that the *rewrite* is often an expression index rather than a query change:

| Predicate | Index that serves it |
|-----------|----------------------|
| `WHERE LOWER(email) = @p` | `CREATE INDEX ... ON users (LOWER(email))` — expression indexes match the exact expression; or use `citext` for the column |
| `WHERE created_at::date = @d` | Rewrite as half-open range: `created_at >= @d AND created_at < @d + interval '1 day'` |
| `WHERE name LIKE 'abc%'` | Plain B-tree only under `C` collation — otherwise `ON t (name text_pattern_ops)` (serves anchored LIKE and `=`, but not `<`/`>`/ORDER BY under your real collation; keep the plain index too if you sort) |
| `WHERE name LIKE '%abc%'` | `pg_trgm`: `CREATE INDEX ... USING GIN (name gin_trgm_ops)` — trigram indexes serve infix LIKE/ILIKE |
| `WHERE payload->>'type' = @p` | Expression index `ON t ((payload->>'type'))`, or restate as containment `payload @> '{"type": ...}'` with a GIN index (§5) |
| `WHERE id = @p` with mismatched types | Match the parameter type exactly. Integer widths are safe (`int`/`bigint` share a B-tree operator family), but a type from a *different* family — `numeric` parameter against an `int` column, `timestamp` vs `timestamptz` — resolves by casting the column, which defeats the index |

Nullability and boolean traps: `WHERE flag != true` skips NULLs silently. `flag IS DISTINCT FROM
true` fixes the *semantics* but not the plan — `IS [NOT] DISTINCT FROM` is not an indexable clause
in any PostgreSQL release; the planner cannot match it to an index. When the null-safe form is
selective, mirror it in a partial index (`... WHERE flag IS DISTINCT FROM true`) and repeat that
predicate verbatim in the query; otherwise rewrite as `(flag = false OR flag IS NULL)`.
`NOT IN (subquery)` with NULLs returns zero rows here too — `NOT EXISTS` is both correct and
better-planned.

## Indexing playbook

1. **Multi-column B-tree: order by usage, equality first.** `(user_id, created_at DESC)` serves
   `WHERE user_id = @p ORDER BY created_at DESC` and `WHERE user_id = @p AND created_at > @t`; it
   does *not* usefully serve `WHERE created_at > @t` alone — without the leading column the index
   degenerates to a full scan of itself, which the planner rarely picks over the table.
2. **Partial index** for skewed flags: `... ON orders (customer_id) WHERE status = 'pending'` —
   smaller and hotter in cache. The catch: the planner uses it only when it can *prove at plan
   time* that the query's predicate implies the index predicate, so the discriminating value must
   be known then. `status = @p` matches only while the value is substituted into planning — custom
   plans, i.e. the first five executions of a prepared statement, or always under
   `plan_cache_mode = force_custom_plan`; the generic plan a prepared statement settles into can
   never use the index. The robust pattern: bake the literal into the SQL
   (`WHERE status = 'pending' AND customer_id = @p`) and parameterize only the indexed columns.
3. **Covering index** (PG 11+) for Index Only Scans: `ON orders (user_id) INCLUDE (total,
   created_at)` — INCLUDE columns are payload only (returnable, not searchable or sortable), and
   the clause also works on UNIQUE indexes where adding the column to the key would break the
   constraint. Verify with EXPLAIN that Heap Fetches stays near zero, else vacuum more
   aggressively (§8).
4. **Expression index** whenever the query can't be rewritten to bare columns (§2). The query
   must use the same expression the index was built on (compared after parsing, so whitespace
   and case of keywords don't matter — but `LOWER(email)` won't match an index on
   `LOWER(TRIM(email))`). Side benefit: ANALYZE gathers statistics on the expression itself,
   often fixing estimates too.
5. **Pick the index type by workload:**

   | Type | For |
   |------|-----|
   | B-tree | Equality, ranges, sorted output — the default, and the only type serving a plain `ORDER BY col` |
   | GIN | Contains-style queries: JSONB `@>`/`?`, arrays `@>`/`&&`, full-text `@@`, trigram LIKE |
   | GiST | Ranges/overlaps (`&&`), geometry (PostGIS), nearest-neighbour `ORDER BY x <-> point` |
   | BRIN | Huge append-only tables where physical order tracks the column (timestamps in a log table) — tiny index, coarse pruning |

6. **Always `CREATE INDEX CONCURRENTLY` in production** — the plain form takes a lock that blocks
   writes for the whole build. CONCURRENTLY cannot run inside a transaction block (migration tools
   default to wrapping — in EF Core use `migrationBuilder.Sql(..., suppressTransaction: true)`),
   takes longer (two table scans plus waiting out concurrent transactions), and a failed or
   cancelled build leaves an `INVALID` index behind — drop it and retry, it still costs write
   overhead while invalid.
7. **Validate before shipping**: re-run the plan capture and confirm the node changed. `DbPlan
   suggest_indexes` proposes candidates and — when the dev database has `hypopg` installed —
   validates them hypothetically without building anything; without hypopg they are heuristic, say
   so.
8. **Prune what nothing uses**: `pg_stat_user_indexes.idx_scan = 0` (since stats reset) marks
   candidates; every index taxes INSERT/UPDATE and disqualifies HOT updates on its columns (§8).

## Statistics and the planner

`ANALYZE` (usually via autovacuum) samples each table into `pg_statistic`. When estimates are off:

- **Stale stats after bulk changes** — the planner extrapolates total row count from the file's
  current size, but a just-loaded (or heavily rewritten) table has no *column* statistics, so
  every predicate's selectivity is a default guess. Run `ANALYZE tablename` explicitly after bulk
  INSERT/COPY; autoanalyze fires only after ~10% of rows change, so a load-once table can wait a
  long time.
- **Skewed or high-cardinality columns** under-sampled: `ALTER TABLE t ALTER COLUMN c SET
  STATISTICS 1000` (default `default_statistics_target` = 100, ceiling 10000), then `ANALYZE`.
  Larger targets slow ANALYZE and planning — raise per column, not globally.
- **Correlated columns** — the planner multiplies selectivities as if independent, so
  `WHERE city = 'X' AND zip = 'Y'` under-estimates badly. `CREATE STATISTICS s (dependencies) ON
  city, zip FROM t; ANALYZE t;` (PG 10+) teaches it the correlation.
- **Cross-checking**: `pg_stat_user_tables.last_analyze` / `last_autoanalyze` show freshness;
  `pg_stats` shows what the planner believes about a column.

Core PostgreSQL has no query hints by design (the `pg_hint_plan` extension exists where policy
allows it). The steering levers are: statistics (above), rewriting the query, indexes, and
per-statement planner GUCs as a last resort (`SET LOCAL enable_nestloop = off` inside a
transaction is a diagnostic tool, not a fix to ship). When the same query plans differently
across environments, `EXPLAIN (SETTINGS)` (PG 12+) prints any planner-relevant GUCs changed from
default — check that before blaming statistics.

## JSONB

- **Operator choice decides indexability.** Containment `@>`, existence `?`/`?|`/`?&`, and
  (PG 12+) `@?`/`@@` jsonpath are served by a GIN index on the column; `->>` extraction in a WHERE
  clause is not — restate as containment or add an expression index on the extracted path.
- **Containment also answers array membership**: `payload @> '{"tags": ["urgent"]}'` finds rows
  whose `tags` array contains that element, using the same GIN index — the indexable way to query
  arrays inside JSONB.
- **Two GIN opclasses**: default `jsonb_ops` supports all the operators above; `jsonb_path_ops`
  (`USING GIN (payload jsonb_path_ops)`) supports only `@>`/`@?`/`@@` but is smaller and faster.
  Prefer it when containment is all you query.
- **Extract-and-cast needs the cast in the index**: `WHERE (payload->>'qty')::int > 5` matches only
  an index on `(((payload->>'qty'))::int)` — and it's a B-tree expression index, which is also what
  equality/range/sort on one extracted scalar wants; GIN earns its size for multi-key containment.
- Keep relational data relational: columns you filter, join, or sort on routinely belong as real
  columns (or, PG 12+, generated columns `GENERATED ALWAYS AS ((payload->>'x')) STORED` — `STORED`
  is the only variant through PG 17), not JSONB paths. JSONB shines for genuinely variable payloads.
- Whole-value updates only: `UPDATE ... SET payload = jsonb_set(payload, ...)` rewrites the entire
  value (and re-TOASTs it if large) — high-frequency updates of one field inside a big document
  bloat the table (§8), and with a GIN index on the column every such update maintains the index
  and loses HOT.
- **Write semantics surprise**: `||` merges shallowly — a top-level key in the right operand
  replaces the left's whole subtree, no deep merge. `jsonb_set` creates a missing *leaf* by default
  but silently returns the input unchanged when an intermediate path step is absent — ensure parent
  objects exist before setting a nested leaf (mind that `||` with `'{"user": {}}'` would *replace*
  an existing `user`, not merge into it).

## Query patterns

- **Pagination**: `OFFSET n` reads and discards n rows every page. Keyset pagination —
  `WHERE (created_at, id) < (@last_created, @last_id) ORDER BY created_at DESC, id DESC LIMIT 50`
  — is O(page) and stable under concurrent inserts. Always a unique tiebreaker. The row-wise
  comparison is valid only while *every* sort key runs the same direction (it matches the
  lexicographic order the index provides); for mixed ASC/DESC ordering expand it by hand:
  `WHERE created_at < @c OR (created_at = @c AND id > @i)` with `ORDER BY created_at DESC, id ASC`.
- **`COUNT(*)` on a big table is a real scan.** For "roughly how many":
  `SELECT reltuples::bigint FROM pg_class WHERE relname = 't'` — an estimate refreshed only by
  VACUUM/ANALYZE (and CREATE INDEX), so it drifts on churny tables, and on PG 14+ it reads `-1`
  for a table never yet vacuumed or analysed. For an exact hot-path count, maintain it (trigger,
  materialized view) rather than recount.
- **CTEs**: since PG 12 a CTE is inlined into the main query when it is non-recursive, a plain
  side-effect-free SELECT with no volatile functions, and referenced exactly once. Anything else
  materialises. Steer explicitly with `WITH x AS [NOT] MATERIALIZED (...)` — `MATERIALIZED` is an
  optimisation fence (deliberate or accidental: pre-12 code often relied on the old
  always-materialise behaviour), and `NOT MATERIALIZED` forces inlining even when multi-referenced.
- **Big IN lists**: `= ANY(@array)` with a real array parameter beats interpolated IN lists — one
  statement text, so one prepared statement and one `pg_stat_statements` entry regardless of list
  length (Npgsql maps C# arrays natively). Past a few thousand entries, join a `unnest(@array)` or
  temp table.
- **DISTINCT papering over a fan-out join** — same smell as anywhere: fix the join or use
  `EXISTS`.
- **Set-returning and volatile functions in the SELECT list** run per row; a PL/pgSQL function
  in a WHERE clause is a black box to the planner (set-returning functions default to a flat
  `ROWS 1000` estimate; declared `COST` defaults to 100 for non-C-language functions) — prefer
  simple SQL-language functions, which the planner can inline, or declare `ROWS`/`COST` honestly.

## Workload-level analysis

The one extension worth insisting on: `pg_stat_statements` (`CREATE EXTENSION pg_stat_statements`
+ `shared_preload_libraries`). Then, through `DbQuery`:

**Top queries** — normalised, cumulative since stats reset:

```sql
SELECT calls, round(total_exec_time)::bigint AS total_ms, round(mean_exec_time, 1) AS mean_ms,
       rows, shared_blks_read, query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 20;  -- also try ORDER BY calls DESC (the N+1 view) and shared_blks_read DESC (the I/O view)
```

(Column names are PG 13+; on PG ≤ 12 the columns are `total_time`/`mean_time`.) A sub-millisecond
query with hundreds of thousands of `calls` is the N+1 pattern seen server-side. Counters are
cumulative since the last `pg_stat_statements_reset()` — reset before a before/after comparison.

**What is running / blocked right now:**

```sql
SELECT pid, state, wait_event_type, wait_event, now() - xact_start AS xact_age,
       now() - query_start AS query_age, left(query, 200) AS query
FROM pg_stat_activity
WHERE state <> 'idle'
ORDER BY xact_start;
```

`state = 'idle in transaction'` with an old `xact_start` is the classic application bug: a
transaction opened and abandoned (forgotten commit, connection held across an await). It blocks
vacuum from reclaiming anything newer than it (§8) and holds locks.

**Who blocks whom:**

```sql
SELECT blocked.pid AS blocked_pid, left(blocked.query, 80) AS blocked_query,
       blocking.pid AS blocking_pid, left(blocking.query, 80) AS blocking_query
FROM pg_stat_activity blocked
JOIN LATERAL unnest(pg_blocking_pids(blocked.pid)) AS b(pid) ON true
JOIN pg_stat_activity blocking ON blocking.pid = b.pid;
```

**Buffer cache hit ratio** — want > 99% for OLTP, but read it with care: a `blks_read` is a miss
in *shared_buffers* only and frequently still comes from the OS page cache, and the ratio is
meaningless right after a restart or stats reset:

```sql
SELECT datname, blks_hit, blks_read,
       round(blks_hit * 100.0 / NULLIF(blks_hit + blks_read, 0), 2) AS hit_pct
FROM pg_stat_database
WHERE datname = current_database();
```

**Tables with heavy Seq Scans** (`pg_stat_user_tables.seq_scan`/`seq_tup_read` high on big tables
= missing index) and **unused indexes** (`pg_stat_user_indexes.idx_scan = 0` since stats reset)
round out the sweep.

## VACUUM, bloat, and MVCC

Every UPDATE writes a new row version; DELETE only marks. Dead versions stay until VACUUM, so
UPDATE-heavy tables *grow* and scans slow down even when live row counts don't change — that is
bloat, and it is the background explanation for "the same query got slower over months".

```sql
SELECT relname, n_live_tup, n_dead_tup,
       round(n_dead_tup::numeric / NULLIF(n_live_tup + n_dead_tup, 0) * 100, 1) AS dead_pct,
       last_autovacuum, last_autoanalyze
FROM pg_stat_user_tables
ORDER BY n_dead_tup DESC
LIMIT 20;
```

Developer-actionable levers:

- **Don't block vacuum**: long transactions (including `idle in transaction`, and long-running
  reports) pin the oldest visible snapshot; vacuum cannot reclaim any row version deleted after
  that snapshot — across *every* table, not just the ones the transaction touched. Fixing the
  application's transaction hygiene often fixes "autovacuum can't keep up". The server-side
  guardrail is `idle_in_transaction_session_timeout` (settable per role or per session): it kills
  abandoned transactions instead of letting them pin the horizon for hours.
- **Per-table autovacuum tuning** for high-churn tables: the default trigger is
  `autovacuum_vacuum_threshold (50) + autovacuum_vacuum_scale_factor (0.2) × reltuples` — ~20% of
  the table, which on a 100M-row table means 20M dead rows before vacuum starts.
  `ALTER TABLE hot_table SET (autovacuum_vacuum_scale_factor = 0.01)`.
- **HOT updates**: an UPDATE that modifies no indexed column *and* finds free space on the same
  page can reuse the page without touching any index — vastly cheaper, and no index bloat. Every
  index you add on a column removes updates of that column from HOT eligibility, and a fully
  packed page defeats it too — a lower `fillfactor` (e.g. 90) on update-heavy tables buys the
  headroom. Measure with `pg_stat_user_tables.n_tup_hot_upd` vs `n_tup_upd`.
- `VACUUM` reclaims space for reuse but does not shrink files; a badly bloated table needs
  `VACUUM FULL` (ACCESS EXCLUSIVE lock for the whole rewrite — an outage) or `pg_repack` (online).
  Prevention beats both.
- **Transaction ID wraparound** is the reason vacuum is not optional: XIDs are 32-bit, and if
  aging tables are never frozen the database eventually refuses writes. Autovacuum handles it
  unless something (a pinned horizon, disabled autovacuum) stops it — one more reason the
  long-transaction fix matters. `SELECT datname, age(datfrozenxid) FROM pg_database` is the check.

## Locking and deadlocks

- Plain readers never block writers and vice versa (MVCC). Blocking comes from writer-vs-writer on
  the same rows, explicit `LOCK`/`SELECT FOR UPDATE`, and **DDL**: `ALTER TABLE` wants an exclusive
  lock, and while it *waits* behind one long transaction, every later query on that table queues
  behind *it* — the "one migration froze the whole app" incident. Run migrations with
  `SET lock_timeout = '5s'` and retry, and keep transactions short so the wait never forms.
- **Deadlocks** are detected after `deadlock_timeout` (1s) and one victim gets error `40P01`; the
  server log records both queries. Same fixes as anywhere: one canonical access order (sort the
  IDs before a multi-row `SELECT ... FOR UPDATE` or batched UPDATE), short transactions, and FK
  parent/child update ordering.
- **Work queues**: `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 10` hands each worker distinct rows
  with no blocking and no serialization retries — the canonical Postgres job-queue pattern
  (`FOR UPDATE NOWAIT` when failing fast is better).
- **Upserts are built in**: `INSERT ... ON CONFLICT (key) DO UPDATE SET ...` is atomic and
  race-free — no `MERGE`-style existence-check races to hand-lock around.
- **Cap runaway statements from the application**: `statement_timeout` (per role, or
  `SET statement_timeout` per session) cancels the query server-side. Npgsql's `CommandTimeout`
  only makes the *client* give up — pair them so the server stops working too.
- .NET side: Npgsql pools connections per connection string — an exhausted pool (`Maximum Pool
  Size`, default 100) looks like a slow database; `TransactionScope` defaults to `Serializable`
  here too, and under Serializable concurrent transactions fail with `40001` serialization errors
  that the application must retry — set `IsolationLevel.ReadCommitted` explicitly unless you want
  that contract; and automatic prepared statements are **off by default** — set
  `Max Auto Prepare = 10` or so in the connection string to make hot parameterised queries
  measurably cheaper.
