---
name: csharp-database
description: Querying and inspecting the databases behind a C#/.NET solution with the RoslynSense db tools — listing connections the solution already declares, reading a schema, running parameterised SELECTs, and explaining a slow query from its execution plan. Use when a task needs real data or a real schema rather than the C# model of it, or when a query is slow.
---
# Databases with RoslynSense

These tools talk to the databases a solution is configured against. They exist because the C#
side of a data access layer tells you what the code *believes* the schema is; only the database
tells you what it *is*.

## Start by listing, not by registering

At startup RoslynSense scans the working directory for `web.config` and `appsettings*.json`
files and registers every connection string it finds, aliased `ProjectName_ConnectionStringName`.
So in a normal solution the connections are already there:

- **DbListConnections** — the registered aliases and their providers. **Call this first.** Every
  other `db_*` tool takes an `alias`, and guessing one wastes a round trip.
- **DbAddConnection** — only when the alias you need is genuinely absent. Takes `alias`,
  `provider` (`psql`, `mssql`, or `sqlite`), and a connection string — or an
  `xml:<path>#<name>` / `json:<path>#<name>` reference to a config file, which is preferable to
  pasting credentials. Pass `replaceExisting: true` to rebind an existing alias.
- **DbRemoveConnection** — drop an alias again.

Environment-specific files override the base file for the same alias, and files that look
production-flavoured (`Production`, `Prod`, `Live`, `Staging`, `Release`) are skipped
deliberately — an agent should not reach a production database by accident. If a connection you
expected is missing, that is usually why, and registering it by hand is a decision to make out
loud rather than quietly.

## Reading the schema

- **DbListTables** — tables and views, optionally filtered by `schema` (ignored for SQLite).
- **DbDescribeTable** — columns, data types, nullability, and defaults. `table` may be
  schema-qualified (`public.users`).

Prefer these over inferring the schema from EF entities or hand-written SQL in the codebase.
Migrations drift, `[NotMapped]` exists, and views are usually invisible from the C# side.

## Running SQL

- **DbQuery** — a `SELECT`. Returns rows as a table, capped by `maxRows` (default 200).
- **DbExecute** — anything that is not a query: `INSERT`, `UPDATE`, `DELETE`, DDL. Returns the
  affected row count.

**Always pass user-supplied or session-derived values through `parameters`**, a JSON object like
`{"@id": 42, "@name": "abc"}`, rather than interpolating them into the SQL. This is not only
about injection: parameterised SQL is what the server caches a plan for, so a concatenated query
also measures differently from the one the application runs.

DbExecute writes to a real database. Say what you are about to change before you change it, and
prefer a `SELECT` of the affected rows first — there is no undo here.

## Explaining a slow query

Do not guess at indexes. Capture a plan and read it:

1. **DbQuery** with `includeExecutionPlan: true`. The result carries a `planId` like
   `plan-153012-a1b2`.
   - SQL Server: `SET STATISTICS XML ON` — you still get the data rows.
   - PostgreSQL: `EXPLAIN (ANALYZE, BUFFERS, ...)` — you get the plan **only**, not the rows.
     Run the query twice if you need both.
   - A provider without plan support says so in the output rather than failing.
2. **DbPlan** with that `planId` and a `view`:
   - `summary` (default) — cost, elapsed, the top operators, warning counts. Start here.
   - `warnings` — native warnings, missing-index suggestions, estimate-vs-actual mismatches.
     This is where the answer usually is.
   - `operators` — the operator list, `sortBy` one of `cost`, `actual_rows`, `actual_elapsed`,
     `estimate_rows`. Sorting by `actual_elapsed` finds where the time went; comparing
     `actual_rows` against `estimate_rows` finds where the optimiser was misled.
   - `query` — pull structure out of the raw plan: XPath with the `sp:` prefix for SQL Server,
     JSONPath for PostgreSQL. Requires `expression`.
   - `suggest_indexes` — PostgreSQL only, and validated with `hypopg` when that extension is
     installed. Without it the candidates are heuristic; say so rather than presenting them as
     measured.

A plan is evidence about one execution against one data distribution. An index it justifies on a
seeded development database may be pointless in production, so report what the plan showed
alongside what you concluded from it.

## Tool selection

| Task | Preferred Tool | Avoid |
|------|----------------|-------|
| Find out which databases are reachable | **DbListConnections** | Reading connection strings out of config by hand |
| Learn a table's real columns | **DbDescribeTable** | Inferring them from the EF entity |
| Check what data actually looks like | **DbQuery** with `parameters` | Interpolating values into the SQL |
| Understand why a query is slow | **DbQuery** `includeExecutionPlan`, then **DbPlan** | Adding an index on a hunch |
| Change data or schema | **DbExecute**, after saying what will change | Running it and reporting afterwards |
