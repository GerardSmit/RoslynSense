# SQL Server performance tuning

Distilled from field checklists for T-SQL review, execution-plan analysis, index advising, and
deadlock analysis (after github.com/vanterx/mssql-performance-skills, MIT). Organised by what you
are looking at, not by check number.

**Contents**
1. [Reading an execution plan](#reading-an-execution-plan)
2. [Sargability — predicates that kill index seeks](#sargability)
3. [Indexing playbook](#indexing-playbook)
4. [Parameter sniffing and the plan cache](#parameter-sniffing-and-the-plan-cache)
5. [Constructs the optimiser cannot estimate](#constructs-the-optimiser-cannot-estimate)
6. [Workload-level analysis with DMVs](#workload-level-analysis-with-dmvs)
7. [Deadlocks](#deadlocks)
8. [T-SQL correctness traps worth flagging in passing](#t-sql-correctness-traps)

---

## Reading an execution plan

`DbPlan warnings` surfaces the native plan warnings; these are the highest-signal findings:

| Warning | Meaning | Fix |
|---------|---------|-----|
| `PlanAffectingConvert` (Seek Plan) | Implicit type conversion on the *column* side prevented a seek | Match parameter type to column type — for .NET, usually an NVARCHAR parameter against a VARCHAR column |
| `PlanAffectingConvert` (Cardinality) | Conversion distorted the histogram lookup; estimates are density-vector guesses | Same fix; estimates recover once the conversion goes |
| `SpillToTempDb` | A sort or hash ran out of its memory grant and wrote to tempdb (SpillLevel ≥ 2 is severe) | Fix the row underestimate feeding it (stats, sniffing); the grant was sized for the estimate |
| `NoJoinPredicate` | Cartesian product — output rows are the *product* of the inputs | Usually a forgotten join condition. Two benign look-alikes: correlated APPLY (condition lives in outer references) and a predicate the optimiser proved redundant |
| `ColumnsWithNoStatistics` | Optimiser used a fixed default guess for a column | `UPDATE STATISTICS` / create statistics on the column |
| Missing-index suggestions | The optimiser's own index wish | Treat as raw material, not DDL — see the indexing playbook |

Then read `operators`, and know the recurring villains:

- **Key Lookup / RID Lookup** at volume (thousands of rows or executions): the nonclustered index
  found the rows but had to fetch extra columns from the base table, one row at a time. Fix by
  adding those columns as `INCLUDE`s on the index being seeked.
- **Scan where a seek was possible**: `rows read` vastly exceeding `rows returned` (100×+) means the
  predicate was applied *after* reading. Either the predicate is non-sargable (§2) or the index is
  missing (§3).
- **Seek with a residual predicate**: a Seek node can lie the same way — a seek predicate *plus* a
  separate Predicate, reading 10× more rows than it returns, means the index descended on part of
  the filter and discarded the rest row by row at the leaf. Promote the residual column into the
  index key (§3).
- **Eager Index Spool**: SQL Server built a temporary index in tempdb at runtime — on every
  execution. This is the strongest "an index is missing" signal in a plan, and it often *suppresses*
  the missing-index suggestion (the optimiser already found its workaround). Derive the permanent
  index from the spool's seek predicate.
- **Nested Loops with a high execution count** on the inner side (10,000+): the N+1 pattern, or a
  join strategy error from a row underestimate. If the inner side is a scan, index its join column;
  if the estimate was 1 row and reality was thousands, fix the estimate.
- **Sort** dominating the plan: add an index that returns rows pre-ordered (filter columns first,
  then the ORDER BY columns in matching direction), or stop sorting unneeded columns — `SELECT *`
  into a sort of a million wide rows is what "unexplained" multi-GB memory grants are made of.
- **Hash Match spill / many-to-many Merge Join**: worktables in tempdb; usually a cardinality error
  or missing unique constraint upstream.
- **Window Spool** under a window aggregate: `RANGE UNBOUNDED PRECEDING` — the default frame of an
  ORDER BY-only `OVER` clause — spools per row. If duplicate ORDER BY values don't affect
  correctness, writing `ROWS UNBOUNDED PRECEDING` explicitly removes the spool (2–10× on large sets).
- **Table Scan** (as opposed to Clustered Index Scan) means a heap. Heaps accumulate forwarded
  records under updates and cannot use read-ahead efficiently; give the table a clustered index
  unless it is deliberately a staging heap.

**Estimate vs actual is the master diagnostic.** A 100×–1,000× row mismatch on any operator means
every decision downstream of it (join strategy, memory grant, parallelism) was made on wrong
information. Find *why* the estimate is wrong — stale stats, sniffed atypical parameter, table
variable, MSTVF, expression on a column — before tuning anything the mismatch feeds. For sniffing,
the plan carries direct evidence: `ParameterCompiledValue` ≠ `ParameterRuntimeValue` in the
parameter list means the estimates were made for someone else's parameters (§4). A local variable
(`DECLARE @x`) never appears in that list at all — see §5.

**Cost percentages are estimates, not measurements.** Operator cost % is the compile-time model
and never updates with reality; in an actual plan, locate the bottleneck by elapsed time instead.
One trap: in row-mode plans an operator's elapsed time is *cumulative* — it includes all its
children — so operators near the root always look expensive until you subtract the children's
time. (Batch-mode operators report their own time only.)

**Statement-level red flags**: compile timeout (`StatementOptmEarlyAbortReason=TimeOut` — the
optimiser gave up before finding a good plan; simplify the query), a granted memory ≥ 10× used
(row overestimate), grant waits (`RESOURCE_SEMAPHORE` queueing — the server is out of query memory),
and 8+ joins in one statement (the optimiser switches from exhaustive to greedy join ordering;
materialising an intermediate result into a temp table can beat one giant query).

## Sargability

A predicate is sargable when the column stands bare on one side of the comparison. Anything that
wraps or transforms the column forces a scan with a per-row filter. The rewrites:

| Non-sargable | Sargable rewrite |
|--------------|------------------|
| `WHERE YEAR(OrderDate) = 2024` | `WHERE OrderDate >= '2024-01-01' AND OrderDate < '2025-01-01'` |
| `WHERE DATEDIFF(DAY, OrderDate, GETDATE()) <= 30` | `WHERE OrderDate >= DATEADD(DAY, -30, CAST(GETDATE() AS DATE))` |
| `WHERE ISNULL(Status, 'X') = @s` | `WHERE (Status = @s OR (Status IS NULL AND @s = 'X'))` |
| `WHERE LEN(Email) > 0` | `WHERE Email <> ''` |
| `WHERE CAST(Id AS VARCHAR) = @s` | Cast the parameter, not the column: `WHERE Id = CAST(@s AS INT)` |
| `WHERE col + 1 = @v` | `WHERE col = @v - 1` |
| `WHERE UPPER(Name) = UPPER(@s)` | Usually just `WHERE Name = @s` — the default collations are case-insensitive, so the wrappers add nothing but cost a seek |
| `ON YEAR(a.Date) = b.Year` | Persisted computed column on `a`, indexed |
| `WHERE Name LIKE '%term'` | No rewrite exists — leading wildcards need Full-Text Search or a reversed computed column |
| `WHERE JSON_VALUE(doc,'$.x') = @v` | Persisted computed column `AS JSON_VALUE(doc,'$.x')`, indexed |

Related, and just as damaging:

- **Implicit conversion**: `NVARCHAR` parameter vs `VARCHAR` column (the .NET default — see the
  SKILL.md), `VARCHAR` literal vs `INT` column, `DATETIME` parameter vs `DATE` column. The
  conversion lands on the column side by type-precedence rules and forbids the seek.
- **Collation mismatch** across joined columns forces conversion the same way; align collations or
  add an explicit `COLLATE`.
- **`OR` in a join predicate** (`ON a.id = b.id OR a.alt = b.id`) blocks seek strategies — rewrite
  as `UNION ALL` of the two joins.
- **Functions of constants are fine** — `WHERE col > DATEADD(DAY, -@n, GETDATE())` is sargable (the
  function is on the value side); just don't recompute it per row in a UDF.

## Indexing playbook

Derive indexes from what the plan did, in this order of reliability:

1. **Key Lookup → extend the existing index.** The seeked index's keys stay; the lookup's output
   columns become `INCLUDE`s. Kills the lookup without a new index:
   `CREATE INDEX IX_... ON T (SeekCol) INCLUDE (FetchedCol1, FetchedCol2) WITH (DROP_EXISTING = ON, ONLINE = ON)`.
2. **Eager Index Spool → make it permanent.** The spool's seek predicate *is* the index definition.
3. **Expensive scan with a predicate → seek index.** Equality columns first, then range columns.
4. **Residual predicate on a seek** (seek reads far more rows than it returns): promote the residual
   column into the *key* (it must narrow the B-tree descent — `INCLUDE` doesn't).
5. **Dominant Sort → pre-sorted index.** Filter columns leading, then ORDER BY columns in matching
   ASC/DESC. A backward scan in the plan is the same story — the index exists but in the wrong
   direction — but it costs only modestly more CPU and is rarely the bottleneck; fix it last.
6. **Nested-loops inner-side scan → index the join column** of the inner table.
7. **Hash join with a scanned probe side → index the probe-side join column.** May let the
   optimizer drop the hash build for a merge join or a seek — but only if the build side can also
   arrive sorted; if the plan stays Hash Match, the other side needs an index too.
8. **Skewed low-cardinality filter** (`IsDeleted = 0` matching 2% of rows) → filtered index
   `WHERE IsDeleted = 0`. Requires `ANSI_NULLS ON` and `QUOTED_IDENTIFIER ON` at create time *and*
   in every session that later modifies the table — emit the SET statements with the DDL. And the
   optimizer only picks it when the query carries the literal (`Status = 'Pending'`), not a
   parameter (`Status = @status`) — verify before shipping.

Server-wide, the missing-index DMVs rank wishes by measured demand (needs `VIEW SERVER STATE`;
on SQL Server 2022+ the lesser `VIEW SERVER PERFORMANCE STATE` suffices):

```sql
SELECT mid.statement AS table_name, mid.equality_columns, mid.inequality_columns,
       mid.included_columns, migs.user_seeks, migs.avg_total_user_cost, migs.avg_user_impact,
       ROUND(migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans), 2) AS weighted_impact
FROM sys.dm_db_missing_index_groups mig
JOIN sys.dm_db_missing_index_details mid ON mig.index_handle = mid.index_handle
JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle = migs.group_handle
ORDER BY weighted_impact DESC;
```

Caveats that keep this honest:

- Optimizer suggestions are per-query and greedy. **Merge before creating**: several suggestions on
  one table usually collapse into one index; a suggestion that is a prefix of another is subsumed.
- A suggestion with > 4 key columns or > 5 INCLUDEs is the optimiser stapling unrelated access
  patterns together — split it or fix the queries instead.
- The DMV data resets on restart and each missing-index DMV caps at 600 rows — a busy instance
  silently truncates; absence of a suggestion is not absence of need (spools suppress them).
- The stats aggregate across every query that wanted the index — a big `weighted_impact` may be one
  hot query or fifty cold ones. On SQL Server 2019+, `sys.dm_db_missing_index_group_stats_query`
  gives one row per query per group (resolve text via `sys.dm_exec_sql_text`), so you can tell a
  nightly report's wish from a hot OLTP path's.
- Hard engine limits make the DDL fail outright: 32 key columns (16 before SQL 2016), key size
  1,700 bytes nonclustered / 900 clustered, 1,023 INCLUDEs. Variable-length columns count at
  *declared* max — two `nvarchar(500)` keys are 2,000 bytes and fail even if every value is short.
  Split an over-wide candidate by query rather than truncating.
- Every index costs writes and space. For each candidate, name the queries it serves and the
  DML-heavy tables it taxes; drop the idea if you cannot.

## Parameter sniffing and the plan cache

The first execution's parameter values shape the cached plan; every later execution reuses it. When
the sniffed value is atypical ("customer with 3 orders" vs "customer with 3 million"), the symptom
is a query that is fast or slow *depending on who ran it first* — and a plan whose estimates match
some other parameter's reality. Query Store shows it concretely: one `query_hash` with several
plans whose average durations differ 3×+ is the sniffing signature; without Query Store, a
max-to-average CPU ratio ≥ 10 in `sys.dm_exec_procedure_stats` says the same.

Options, cheapest first:

- **Fix the skew's visibility**: up-to-date statistics, possibly filtered statistics on the skewed
  range.
- **`OPTION (RECOMPILE)`** on the statement: per-execution plan, perfect estimates, pays compile
  cost each call — negligible for infrequent queries, a real tax on hot ones. Right answer for
  catch-all/dynamic-filter queries
  (`WHERE (@name IS NULL OR Name = @name) AND (@city IS NULL OR ...)`) — the classic "optional
  filters" pattern EF and hand-rolled repositories both produce. Without it, one plan must serve
  every filter combination, and serves all of them badly.
- **`OPTION (OPTIMIZE FOR (@p = value))`** when one value class dominates. `OPTIMIZE FOR UNKNOWN`
  plans on average column density — uniform mediocrity instead of peak performance; measure before
  accepting it. Copying parameters into local variables has the same density effect: it is
  `OPTIMIZE FOR UNKNOWN` in disguise, not a distinct fix.
- **SQL Server 2022+** (compat level 160): Parameter Sensitive Plan optimization compiles a
  dispatcher plus per-parameter-range variant plans (visible in `sys.query_store_plan` as
  Dispatcher / Query Variant types). Check it is active before hand-tuning — but verify rather
  than trust: a variant can still carry a bad estimate for its own range.
- **Query Store forced plans** (`sp_query_store_force_plan`) pin a known-good plan — and go stale
  silently, or stop applying when an index they reference is dropped or the schema changes
  (`force_failure_count` in `sys.query_store_plan`; fix the cause or `sp_query_store_unforce_plan`).
  Treat any `QDS_`-forced plan in a captured plan as a standing risk. On 2022+,
  `sys.sp_query_store_set_hints` attaches an `OPTION` hint to a query without touching code — the
  only lever for ORM-generated SQL; unsupported hints fail silently, so confirm the hint took in
  `sys.query_store_query_hints`.

Plan-cache hygiene, .NET-flavoured: every distinct SQL *text* is a separate cache entry. Literal
values baked into SQL (string interpolation, non-parameterised Dapper), varying `IN`-list lengths,
and varying `SET` options per connection all bloat the cache and defeat reuse. Parameterise, and
keep schema prefixes on object names (`dbo.Orders`) so different default schemas share plans.

## Constructs the optimiser cannot estimate

These carry fixed guesses instead of statistics; each one poisons the estimates of everything
joined to it:

| Construct | Estimate used | Fix |
|-----------|---------------|-----|
| Table variable `@t` | 1 row (< SQL 2019); actual count but no column stats (2019+) | `#temp` table when > ~100 rows or when joined |
| Multi-statement TVF | 1 row (100 from 2014, interleaved-exec actuals 2017+) | Inline TVF, or materialise into `#temp` first |
| `STRING_SPLIT` | Fixed 50 rows | Fine for short lists; TVP or staging table at scale |
| Scalar UDF in SELECT/WHERE | Cost hidden entirely; runs per row, blocks parallelism | Inline the logic or convert to inline TVF (2019+ may inline automatically — check the plan's UdfElapsedTime for the ones it didn't) |
| Linked-server query | 1 row for the remote side | Pull remote data into `#temp` locally, then join |
| CTE referenced twice | Re-executed per reference (never materialised) | `#temp` table to compute once |
| Local variable in `WHERE` (`DECLARE @x`) | Average column density — the value is invisible at compile time, like `OPTIMIZE FOR UNKNOWN` | Pass it as a real parameter (ADO.NET/Dapper/EF already do); inside procs that filter on locals, `OPTION (RECOMPILE)` |

Two adjacent traps. *Writing* into a table variable (INSERT/UPDATE/DELETE targeting `@t`) forces
the whole statement to run serially regardless of DOP — one more reason `#temp` wins. And `TOP`,
`EXISTS`, and `FAST N` set a **row goal**: the optimiser deliberately scales estimates *down* to
optimise for the first rows. Usually right — but when the filter is less selective than assumed,
it produces the "TOP 1 took minutes" scan. The plan records `EstimateRowsWithoutRowGoal` on
affected operators; if the full result is always consumed, `OPTION (DISABLE_OPTIMIZER_ROWGOAL)`
(SQL 2016+) turns it off.

## Workload-level analysis with DMVs

When "the database is slow" with no named query, run these through `DbQuery` (read-only,
`VIEW SERVER STATE` required):

**Top queries by cumulative cost** — tune what the server actually spends time on:

```sql
SELECT TOP 20
    qs.total_worker_time/1000 AS total_cpu_ms, qs.total_elapsed_time/1000 AS total_elapsed_ms,
    qs.total_logical_reads, qs.execution_count,
    qs.total_elapsed_time/qs.execution_count/1000 AS avg_elapsed_ms,
    SUBSTRING(st.text, (qs.statement_start_offset/2)+1,
      ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END
        - qs.statement_start_offset)/2)+1) AS statement_text
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
ORDER BY qs.total_worker_time DESC;  -- also try total_logical_reads, total_elapsed_time
```

A cheap statement with a huge `execution_count` is the N+1 pattern seen from the server side —
often a bigger win than the single expensive query.

**Wait statistics** — what the server is waiting on, cumulative since restart:

```sql
SELECT TOP 15 wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
    CAST(100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0) AS DECIMAL(5,2)) AS pct_total
FROM sys.dm_os_wait_stats
WHERE wait_type NOT IN ('SLEEP_TASK','BROKER_TASK_STOP','SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
  'DIRTY_PAGE_POLL','HADR_FILESTREAM_IOMGR_IOCOMPLETION','BROKER_TO_FLUSH','SLEEP_SYSTEMTASK',
  'WAITFOR','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH',
  'XE_TIMER_EVENT','LOGMGR_QUEUE','FT_IFTS_SCHEDULER_IDLE_WAIT','BROKER_EVENTHANDLER',
  'LAZYWRITER_SLEEP','XE_DISPATCHER_WAIT','BROKER_RECEIVE_WAITFOR','SP_SERVER_DIAGNOSTICS_SLEEP')
ORDER BY wait_time_ms DESC;
```

`signal_wait_time_ms` is time spent queued for CPU *after* the resource became available —
total signal waits at ~15–25% of total wait time means CPU saturation, whatever tops the list.
And since counters accumulate from restart, a nightly batch can dominate: for a live incident,
run the query twice a few minutes apart and diff.

| Dominant wait | Points at |
|---------------|-----------|
| `PAGEIOLATCH_*` | Data-file I/O — too much data read (missing indexes, scans) or slow storage |
| `LCK_M_*` | Blocking — long transactions, missing indexes making writers scan, isolation level |
| `CXPACKET` / `CXCONSUMER` | Parallelism coordination — usually a symptom of big scans, not a MAXDOP problem per se |
| `RESOURCE_SEMAPHORE` | Queries queueing for memory grants — oversized grants from row overestimates |
| `WRITELOG` | Transaction log flushes — tiny commits in a hot loop, or slow log storage |
| `PAGELATCH_*` (not IO) | In-memory contention — wait resource `2:1:1`/`2:1:2`/`2:1:3` (tempdb PFS/GAM/SGAM) means temp tables created/dropped too fast; otherwise often a last-page insert hotspot |
| `ASYNC_NETWORK_IO` | The *client* isn't consuming rows — .NET code reading a huge result set row by row |
| `SOS_SCHEDULER_YIELD` | Threads burning full 4 ms quanta — usually big in-memory scans (missing index), *not* proof of CPU pressure; VMs inflate it. Judge CPU by the signal-wait ratio |
| `THREADPOOL` | Out of worker threads — critical at any level; long blocking chains, or too many connections/parallel queries |

**Currently blocking** right now:

```sql
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, r.command,
       SUBSTRING(t.text, 1, 400) AS query_text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0;
```

**File-level I/O latency** — when `PAGEIOLATCH_*` or `WRITELOG` dominates (also cumulative):

```sql
SELECT DB_NAME(fs.database_id) AS db, mf.name AS file_name, mf.type_desc,
       CAST(fs.io_stall_read_ms  * 1.0 / NULLIF(fs.num_of_reads, 0)  AS DECIMAL(18,2)) AS avg_read_ms,
       CAST(fs.io_stall_write_ms * 1.0 / NULLIF(fs.num_of_writes, 0) AS DECIMAL(18,2)) AS avg_write_ms
FROM sys.dm_io_virtual_file_stats(NULL, NULL) fs
JOIN sys.master_files mf ON fs.database_id = mf.database_id AND fs.file_id = mf.file_id
ORDER BY fs.io_stall_read_ms + fs.io_stall_write_ms DESC;
```

Data files: under 10 ms healthy, over 20 ms a problem; log files (every commit waits on them)
over 10 ms. If latency is fine but `PAGEIOLATCH_*` is high, storage is just the messenger —
the queries read too much, which is a code fix, not a hardware ticket.

**Memory grants queueing** — when `RESOURCE_SEMAPHORE` shows up:

```sql
SELECT session_id, requested_memory_kb, granted_memory_kb, max_used_memory_kb, grant_time, wait_order
FROM sys.dm_exec_query_memory_grants
ORDER BY wait_order;  -- grant_time IS NULL = still queued
```

A query granted far more than its `max_used_memory_kb` got an oversized grant from a row
overestimate — the fix is fresher statistics or a better plan, not more RAM.

If Query Store is enabled (`ALTER DATABASE ... SET QUERY_STORE = ON`), prefer it over
`dm_exec_query_stats` for anything historical — it survives restarts and records plan regressions
(`sys.query_store_runtime_stats` joined through plan/query to `sys.query_store_query_text`).

## Deadlocks

Get the deadlock XML first — retrying without reading it just re-rolls the dice. The system_health
Extended Events session records recent ones for free:

```sql
SELECT CAST(event_data AS XML) AS deadlock_xml
FROM sys.fn_xe_file_target_read_file('system_health*.xel', NULL, NULL, NULL)
WHERE object_name = 'xml_deadlock_report';
```

In the XML: the `<victim-list>`, per-process `<inputbuf>` (the SQL), and the `<resource-list>`
showing who held what and who wanted what. The recurring patterns:

- **Opposite access order** — two transactions touch tables A and B in reverse order. Fix: one
  canonical order everywhere (grep the codebase; in EF this includes SaveChanges ordering).
- **Reader/writer** — a SELECT under `READ COMMITTED` deadlocks with an UPDATE. Fix: enable
  `READ_COMMITTED_SNAPSHOT` (readers stop taking shared locks), after checking the app doesn't rely
  on blocking reads. Caveat: RCSI only helps `READ COMMITTED` — sessions under `REPEATABLE READ`,
  `SERIALIZABLE`, or with a `HOLDLOCK` hint still take shared locks and still deadlock.
- **Missing index escalation** — the resource list shows `pagelock`/`objectlock` instead of
  `keylock`: a writer *scans* for the rows to update, locking far more than it changes, and
  collides with everything. The fix is the index on the UPDATE/DELETE's predicate.
- **Key-lookup deadlock** — a reader holds the nonclustered index key and wants the base row; a
  writer holds the row and wants the index. Fix: covering INCLUDE (removes the lookup) or RCSI.
- **Upsert race / MERGE** — two sessions both pass the "not exists" check, both insert. Fix:
  `UPDLOCK, SERIALIZABLE` hints on the existence check (U locks serialize the racers). MERGE has
  its own deadlock modes even single-session; prefer explicit UPDATE-then-INSERT over hinted MERGE.
- **Foreign-key check** — INSERT into a child table takes a shared lock on the parent row to
  validate the FK; a concurrent parent DELETE wants it exclusively. Fix: index the FK column on
  the child (unindexed FKs also make the parent delete scan the whole child table).
- **SERIALIZABLE phantoms** — two transactions hold range locks and both try to insert into the
  range. The classic .NET cause: `new TransactionScope()` defaults to `IsolationLevel.Serializable`
  — almost never intended; pass `IsolationLevel.ReadCommitted` in `TransactionOptions` explicitly.
- **Lock escalation** — one statement takes ~5,000 locks on a table and escalates to a table lock
  mid-flight. Fix: batch the DML below the threshold (`DELETE TOP (2000)` in a loop, each batch
  its own transaction).

A deadlock between two processes with the *same* SPID is a parallel plan or cursor deadlocking
itself — a query-shape problem (reduce MAXDOP, drop the cursor), not a concurrency one.

Application-side: keep transactions short, set isolation deliberately, and treat a deadlock retry
policy as a mitigation, not a fix.

## T-SQL correctness traps

Not performance, but worth flagging whenever they pass through review — they produce silently wrong
results:

- `NOT IN (subquery)` where the subquery can yield NULL returns **zero rows**. Use `NOT EXISTS`.
- A `WHERE` filter on the nullable side of a `LEFT JOIN` silently converts it to an inner join —
  move the condition into the `ON` clause if outer rows should survive.
- `BETWEEN '2024-01-01' AND '2024-01-31'` on a datetime column drops everything after midnight on
  the 31st. Use half-open ranges: `>= '2024-01-01' AND < '2024-02-01'`.
- `TOP`/`OFFSET` pagination without a unique tiebreaker in `ORDER BY` skips and repeats rows across
  pages.
- `= NULL` never matches (`IS NULL` does); `@@IDENTITY` sees trigger inserts
  (`SCOPE_IDENTITY()` doesn't); `UNION` deduplicates and sorts when `UNION ALL` was meant.
- `SELECT @var = col FROM ...` that matches **zero rows leaves `@var` at its previous value** —
  unlike `SET @var = (SELECT ...)`, which nulls it. Initialise before, or use the `SET` form.
- `@@ROWCOUNT` is rewritten by nearly every statement — a `SET @v = 1` between the DML and the
  read makes it 1. Capture it on the very next line: `SET @n = @@ROWCOUNT;`.
- `ISNUMERIC` returns 1 for `'+'`, `'-'`, `'.'`, `','`, `'$'`, and `'E'` — all of which then fail
  the actual cast. Use `TRY_CAST(x AS INT) IS NOT NULL` (and `TRY_CAST` itself over `CAST` when
  input is dirty).
- Recursive CTEs stop at 100 levels by default (error 530). Deeper hierarchies need
  `OPTION (MAXRECURSION n)`; `MAXRECURSION 0` only with a depth-counter guard in the recursive
  member.
- String concatenation into dynamic SQL is an injection hole regardless of performance —
  `sp_executesql` with bound parameters, always; `QUOTENAME` only for validated identifiers. And
  build the string in `NVARCHAR` throughout: one `VARCHAR` operand in the concatenation can
  silently mangle characters above code-point 127.
