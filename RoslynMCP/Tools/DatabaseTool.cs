using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.XPath;
using Json.Path;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class DatabaseTool
{
    [McpServerTool, Description(
        "Run a SELECT query on a configured database connection. Returns rows as a table. " +
        "Always use the parameters argument for user-supplied values to prevent SQL injection.")]
    public static async Task<string> DbQuery(
        [Description("Alias of the database connection (see db_list_connections).")]
        string alias,
        [Description("SQL SELECT statement.")]
        string sql,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        ExecutionPlanStore planStore,
        [Description("Parameters as a JSON object, e.g. {\"@id\": 42, \"@name\": \"abc\"}. Optional.")]
        string? parameters = null,
        [Description("Maximum rows to return. Default 200.")]
        int maxRows = 200,
        [Description("If true, captures the actual execution plan. " +
            "SQL Server: SET STATISTICS XML ON (data rows still returned). " +
            "PostgreSQL: EXPLAIN (ANALYZE, BUFFERS, COSTS, VERBOSE, FORMAT JSON); data rows are NOT returned (only the plan). " +
            "Returns a plan ID usable with db_plan. " +
            "Ignored for providers without plan support (a note is included in the output).")]
        bool includeExecutionPlan = false,
        CancellationToken cancellationToken = default)
    {
        var provider = db.Get(alias);
        if (provider is null) return NoConnection(db, alias);

        if (!TryParseParameters(parameters, out var paramsDict, out var parseError))
            return parseError;

        bool capturePlan = includeExecutionPlan && provider.PlanFormat is not null;

        try
        {
            var result = await provider.ExecuteQueryAsync(sql, paramsDict, maxRows, capturePlan, cancellationToken)
                .ConfigureAwait(false);
            var sb = new StringBuilder();
            fmt.AppendField(sb, "Connection", $"{provider.Alias} ({provider.ProviderName})");
            fmt.AppendField(sb, "Elapsed", $"{result.Elapsed.TotalMilliseconds:F0} ms");

            if (includeExecutionPlan && !capturePlan)
                fmt.AppendField(sb, "Note", $"Execution plan capture not supported for {provider.ProviderName}.");

            if (result.ExecutionPlan is { } payload && provider.PlanFormat is { } fmt2)
            {
                var planId = planStore.Store(provider.Alias, provider.ProviderName, sql, paramsDict, fmt2, payload);
                fmt.AppendField(sb, "Execution Plan ID", planId);
                var session = planStore.Get(planId);
                if (session is not null)
                {
                    var summary = SummarizePlan(session);
                    fmt.AppendField(sb, "Plan total estimated cost", summary.TotalEstimatedCost.ToString("F4"));
                    if (summary.TotalActualElapsedMs is { } el)
                        fmt.AppendField(sb, "Plan actual elapsed", $"{el:F0} ms");
                    fmt.AppendField(sb, "Plan operators", summary.OperatorCount);
                    fmt.AppendField(sb, "Plan warnings", summary.WarningCount);
                    if (session.Format == PlanFormat.Xml)
                        fmt.AppendField(sb, "Plan missing indexes", summary.MissingIndexCount);
                }
                fmt.AppendHints(sb,
                    $"Call db_plan with planId='{planId}' (view: summary, operators, warnings, query, or suggest_indexes) to inspect the plan.");
            }

            if (result.Rows.Count == 0)
            {
                fmt.AppendEmpty(sb, "Query returned no rows.");
                return sb.ToString();
            }
            fmt.AppendTable(sb, "Results", result.Columns, result.Rows,
                totalCount: result.Truncated ? result.Rows.Count + 1 : result.Rows.Count);
            if (result.Truncated)
                fmt.AppendTruncation(sb, result.Rows.Count, result.Rows.Count + 1, "maxRows");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return SqlError(ex, sql);
        }
    }

    [McpServerTool, Description(
        "Run a non-query SQL statement (INSERT/UPDATE/DELETE/DDL) on a configured database. Returns affected row count.")]
    public static async Task<string> DbExecute(
        [Description("Alias of the database connection.")]
        string alias,
        [Description("SQL statement to execute.")]
        string sql,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        [Description("Parameters as a JSON object. Optional.")]
        string? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var provider = db.Get(alias);
        if (provider is null) return NoConnection(db, alias);

        if (!TryParseParameters(parameters, out var paramsDict, out var parseError))
            return parseError;

        try
        {
            var affected = await provider.ExecuteNonQueryAsync(sql, paramsDict, cancellationToken)
                .ConfigureAwait(false);
            var sb = new StringBuilder();
            fmt.AppendField(sb, "Connection", $"{provider.Alias} ({provider.ProviderName})");
            fmt.AppendField(sb, "Rows affected", affected);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return SqlError(ex, sql);
        }
    }

    [McpServerTool, Description(
        "Register a new database connection at runtime. Provider must be one of: psql, mssql, sqlite. " +
        "The connection string may also use the xml:/json: config-file syntax (see ConnectionStringResolver). " +
        "If the alias already exists the call fails unless replaceExisting is true.")]
    public static async Task<string> DbAddConnection(
        [Description("Alias to register the connection under. Used by subsequent db_* calls.")]
        string alias,
        [Description("Provider token: psql (postgres/postgresql), mssql (sqlserver/sql), or sqlite.")]
        string provider,
        [Description(
            "Connection string, or a 'xml:<path>#<name>' / 'json:<path>#<name>' reference.")]
        string connectionString,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        [Description("If true, replaces an existing alias. Default false.")]
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) return "Error: alias cannot be empty.";
        if (string.IsNullOrWhiteSpace(provider)) return "Error: provider cannot be empty.";
        if (string.IsNullOrWhiteSpace(connectionString)) return "Error: connectionString cannot be empty.";

        if (!replaceExisting && db.Get(alias) is not null)
            return $"Error: alias '{alias}' already exists. Pass replaceExisting=true to replace it.";

        string resolved;
        try { resolved = ConnectionStringResolver.Resolve(connectionString); }
        catch (Exception ex) { return $"Error resolving connection string: {ex.Message}"; }

        IDbProvider newProvider;
        try { newProvider = DbProviderFactory.Create(provider, alias, resolved); }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }

        try
        {
            await newProvider.GetTablesAsync(schema: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Error: connection test failed: {ex.Message}";
        }

        if (replaceExisting) db.AddOrReplace(newProvider);
        else if (!db.TryAdd(newProvider))
            return $"Error: alias '{alias}' already exists. Pass replaceExisting=true to replace it.";

        var sb = new StringBuilder();
        fmt.AppendField(sb, "Added", $"{newProvider.Alias} ({newProvider.ProviderName})");
        fmt.AppendField(sb, "Total connections", db.All.Count);
        return sb.ToString();
    }

    [McpServerTool, Description("Remove a database connection registered at runtime or via --db.")]
    public static string DbRemoveConnection(
        [Description("Alias of the connection to remove.")]
        string alias,
        DbConnectionRegistry db,
        IOutputFormatter fmt)
    {
        if (string.IsNullOrWhiteSpace(alias)) return "Error: alias cannot be empty.";
        if (!db.Remove(alias))
            return $"Error: no connection with alias '{alias}'.";

        var sb = new StringBuilder();
        fmt.AppendField(sb, "Removed", alias);
        fmt.AppendField(sb, "Total connections", db.All.Count);
        return sb.ToString();
    }

    [McpServerTool, Description("List all configured database connections.")]
    public static string DbListConnections(DbConnectionRegistry db, IOutputFormatter fmt)
    {
        var sb = new StringBuilder();
        if (db.All.Count == 0)
        {
            fmt.AppendEmpty(sb, "No database connections configured. Register one with db_add_connection, or pass --db <alias>=<provider>:<connstr> to the server.");
            return sb.ToString();
        }
        var rows = db.All
            .Select(p => new[] { p.Alias, p.ProviderName })
            .ToList();
        fmt.AppendTable(sb, "Connections", ["Alias", "Provider"], rows);
        return sb.ToString();
    }

    [McpServerTool, Description("List tables and views in a database, optionally filtered by schema.")]
    public static async Task<string> DbListTables(
        [Description("Alias of the database connection.")]
        string alias,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        [Description("Schema name filter. Optional; ignored for SQLite.")]
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        var provider = db.Get(alias);
        if (provider is null) return NoConnection(db, alias);

        try
        {
            var result = await provider.GetTablesAsync(schema, cancellationToken).ConfigureAwait(false);
            var sb = new StringBuilder();
            fmt.AppendField(sb, "Connection", $"{provider.Alias} ({provider.ProviderName})");
            if (result.Rows.Count == 0)
            {
                fmt.AppendEmpty(sb, "No tables found.");
                return sb.ToString();
            }
            fmt.AppendTable(sb, "Tables", result.Columns, result.Rows);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return SqlError(ex, "GetTables");
        }
    }

    [McpServerTool, Description("Show columns, data types, nullability, and defaults for a table.")]
    public static async Task<string> DbDescribeTable(
        [Description("Alias of the database connection.")]
        string alias,
        [Description("Table name (optionally prefixed with schema, e.g. 'public.users').")]
        string tableName,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        CancellationToken cancellationToken = default)
    {
        var provider = db.Get(alias);
        if (provider is null) return NoConnection(db, alias);

        try
        {
            var result = await provider.DescribeTableAsync(tableName, cancellationToken).ConfigureAwait(false);
            var sb = new StringBuilder();
            fmt.AppendField(sb, "Connection", $"{provider.Alias} ({provider.ProviderName})");
            fmt.AppendField(sb, "Table", tableName);
            if (result.Rows.Count == 0)
            {
                fmt.AppendEmpty(sb, $"Table '{tableName}' not found or has no columns.");
                return sb.ToString();
            }
            fmt.AppendTable(sb, "Columns", result.Columns, result.Rows);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return SqlError(ex, $"DescribeTable {tableName}");
        }
    }

    [McpServerTool, Description(
        "Inspect a captured execution plan. Use planId returned by db_query when includeExecutionPlan=true. " +
        "view: summary (cost, elapsed, top operators, warning counts), " +
        "operators (operator list; sortBy: cost|actual_rows|actual_elapsed|estimate_rows), " +
        "warnings (native warnings, missing indexes, estimate mismatches), " +
        "query (structural query: XPath with 'sp:' prefix for SQL Server, JSONPath for PostgreSQL), " +
        "suggest_indexes (PostgreSQL only; validated via hypopg when installed).")]
    public static async Task<string> DbPlan(
        [Description("Plan ID returned by db_query (e.g. 'plan-153012-a1b2').")]
        string planId,
        ExecutionPlanStore store,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        [Description("View: summary (default), operators, warnings, query, suggest_indexes.")]
        string view = "summary",
        [Description("XPath (SQL Server plan) or JSONPath (Postgres plan) expression. Required for view=query.")]
        string? query = null,
        [Description("view=operators only — sort order: cost (default), actual_rows, actual_elapsed, estimate_rows.")]
        string sortBy = "cost",
        [Description("Maximum operators/nodes to return. Default 20 (operators) or 50 (query).")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var session = store.Get(planId);
        if (session is null) return PlanNotFound(planId);

        return view.ToLowerInvariant() switch
        {
            "summary" => PlanSummaryView(session, fmt),
            "operators" => PlanOperatorsView(session, fmt, sortBy, limit ?? 20),
            "warnings" => PlanWarningsView(session, fmt),
            "query" => PlanQueryView(session, fmt, query, limit ?? 50),
            "suggest_indexes" => await PlanSuggestIndexesView(session, db, fmt, cancellationToken).ConfigureAwait(false),
            _ => $"Error: Unknown view '{view}'. Use: summary, operators, warnings, query, suggest_indexes.",
        };
    }

    private static string PlanSummaryView(ExecutionPlanStore.PlanSession session, IOutputFormatter fmt)
    {
        var summary = SummarizePlan(session);
        var sb = new StringBuilder();
        fmt.AppendField(sb, "Plan ID", session.Id);
        fmt.AppendField(sb, "Alias", session.Alias);
        fmt.AppendField(sb, "Provider", session.ProviderName);
        fmt.AppendField(sb, "Format", session.Format.ToString().ToLowerInvariant());
        fmt.AppendField(sb, "Captured", session.CapturedAt.ToLocalTime().ToString("HH:mm:ss"));
        fmt.AppendField(sb, "SQL", TruncateForDisplay(session.Sql, 200));
        fmt.AppendField(sb, "Total estimated cost", summary.TotalEstimatedCost.ToString("F4"));
        if (summary.TotalActualElapsedMs is { } el)
            fmt.AppendField(sb, "Total actual elapsed", $"{el:F0} ms");
        fmt.AppendField(sb, "Operator count", summary.OperatorCount);
        fmt.AppendField(sb, "Warnings", summary.WarningCount);
        if (session.Format == PlanFormat.Xml)
            fmt.AppendField(sb, "Missing indexes", summary.MissingIndexCount);

        if (summary.TopByEstimatedCost.Count > 0)
        {
            var rows = summary.TopByEstimatedCost
                .Select(o => new[]
                {
                    o.NodeId,
                    o.PhysicalOp,
                    o.EstimatedTotalSubtreeCost.ToString("F4"),
                    o.EstimateRows.ToString("F0"),
                    o.ActualRows?.ToString("F0") ?? "",
                    o.ObjectRef ?? "",
                })
                .ToList();
            fmt.AppendTable(sb, "Top operators by estimated cost",
                ["NodeId", "Op", "SubtreeCost", "EstRows", "ActualRows", "Object"], rows);
        }

        return sb.ToString();
    }

    private static string PlanOperatorsView(
        ExecutionPlanStore.PlanSession session, IOutputFormatter fmt, string sortBy, int limit)
    {
        var ops = OperatorsForSession(session, sortBy, limit);
        var sb = new StringBuilder();
        fmt.AppendField(sb, "Plan ID", session.Id);
        fmt.AppendField(sb, "Sorted by", sortBy);
        if (ops.Count == 0)
        {
            fmt.AppendEmpty(sb, "No operators found.");
            return sb.ToString();
        }
        var rows = ops.Select(o => new[]
        {
            o.NodeId,
            o.PhysicalOp,
            o.LogicalOp,
            o.EstimatedTotalSubtreeCost.ToString("F4"),
            o.EstimateRows.ToString("F0"),
            o.ActualRows?.ToString("F0") ?? "",
            o.ActualElapsedMs?.ToString("F0") ?? "",
            o.ActualExecutions?.ToString() ?? "",
            o.ObjectRef ?? "",
        }).ToList();
        fmt.AppendTable(sb, "Operators",
            ["NodeId", "PhysicalOp", "LogicalOp", "SubtreeCost", "EstRows", "ActualRows", "ActualMs", "Exec", "Object"],
            rows);
        return sb.ToString();
    }

    private static string PlanWarningsView(ExecutionPlanStore.PlanSession session, IOutputFormatter fmt)
    {
        var (warnings, missing) = WarningsAndMissingForSession(session);

        var sb = new StringBuilder();
        fmt.AppendField(sb, "Plan ID", session.Id);

        if (warnings.Count == 0)
            fmt.AppendEmpty(sb, "No warnings.");
        else
        {
            var rows = warnings.Select(w => new[] { w.Type, w.NodeId ?? "", w.Detail }).ToList();
            fmt.AppendTable(sb, "Warnings", ["Type", "NodeId", "Detail"], rows);
        }

        if (session.Format == PlanFormat.Xml)
        {
            if (missing.Count == 0)
                fmt.AppendEmpty(sb, "No missing-index suggestions.");
            else
            {
                var rows = missing.Select(m => new[]
                {
                    m.Impact.ToString("F2"),
                    string.IsNullOrEmpty(m.Schema) ? m.Table : $"{m.Schema}.{m.Table}",
                    string.Join(",", m.EqualityColumns),
                    string.Join(",", m.InequalityColumns),
                    string.Join(",", m.IncludeColumns),
                }).ToList();
                fmt.AppendTable(sb, "Missing indexes",
                    ["Impact", "Table", "Equality", "Inequality", "Include"], rows);
            }
        }

        return sb.ToString();
    }

    private static string PlanQueryView(
        ExecutionPlanStore.PlanSession session, IOutputFormatter fmt, string? query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query cannot be empty. Pass an XPath (SQL Server, prefix 'sp:' bound to " +
                "http://schemas.microsoft.com/sqlserver/2004/07/showplan, e.g. //sp:RelOp[@PhysicalOp='Hash Match']) " +
                "or JSONPath (Postgres, e.g. $..Plans[*][?@.'Node Type'=='Seq Scan']) expression.";

        var sb = new StringBuilder();
        fmt.AppendField(sb, "Plan ID", session.Id);
        fmt.AppendField(sb, "Format", session.Format.ToString().ToLowerInvariant());
        fmt.AppendField(sb, "Query", query);

        if (session.Format == PlanFormat.Xml)
        {
            List<XmlNode> nodes;
            try
            {
                nodes = SqlServerPlanParser.RunXPath(session.ParsedXml!, session.Namespaces!, query, maxResults);
            }
            catch (XPathException ex)
            {
                return $"Error: invalid XPath: {ex.Message}";
            }
            fmt.AppendField(sb, "Matches", nodes.Count);
            if (nodes.Count == 0)
            {
                fmt.AppendEmpty(sb, "No nodes matched.");
                return sb.ToString();
            }
            for (int i = 0; i < nodes.Count; i++)
                fmt.AppendField(sb, $"[{i}]", TruncateForDisplay(nodes[i].OuterXml, 1024));
            return sb.ToString();
        }
        else
        {
            List<JsonNode> nodes;
            try
            {
                nodes = PostgresPlanParser.RunJsonPath(session.ParsedJson!, query, maxResults);
            }
            catch (PathParseException ex)
            {
                return $"Error: invalid JSONPath: {ex.Message}";
            }
            fmt.AppendField(sb, "Matches", nodes.Count);
            if (nodes.Count == 0)
            {
                fmt.AppendEmpty(sb, "No nodes matched.");
                return sb.ToString();
            }
            for (int i = 0; i < nodes.Count; i++)
                fmt.AppendField(sb, $"[{i}]", TruncateForDisplay(nodes[i].ToJsonString(), 1024));
            return sb.ToString();
        }
    }

    private static async Task<string> PlanSuggestIndexesView(
        ExecutionPlanStore.PlanSession session,
        DbConnectionRegistry db,
        IOutputFormatter fmt,
        CancellationToken cancellationToken)
    {
        if (session.Format != PlanFormat.Json)
            return "view=suggest_indexes currently supports PostgreSQL plans only. SQL Server plans expose native missing-index suggestions via db_plan view=warnings.";

        var sb = new StringBuilder();
        fmt.AppendField(sb, "Plan ID", session.Id);
        fmt.AppendField(sb, "Provider", session.ProviderName);

        var candidates = PostgresPlanParser.IndexCandidates(session.ParsedJson!);
        if (candidates.Count == 0)
        {
            fmt.AppendEmpty(sb, "No index candidates found (no Seq Scan or Index Scan with high-rejection Filter).");
            return sb.ToString();
        }

        var heuristicRows = candidates.Select(c => new[]
        {
            c.Table + (c.Alias is null ? "" : $" ({c.Alias})"),
            string.Join(",", c.CandidateColumns),
            c.RowsRemoved.ToString(CultureInfo.InvariantCulture),
            c.ActualRows.ToString(CultureInfo.InvariantCulture),
            c.ImpactRatio.ToString("P0", CultureInfo.InvariantCulture),
            c.Reason,
            TruncateForDisplay(c.FilterExpression, 80),
        }).ToList();
        fmt.AppendTable(sb, "Heuristic candidates",
            ["Table", "Columns", "RowsRemoved", "ActualRows", "FilterRatio", "Reason", "Filter"], heuristicRows);

        var provider = db.Get(session.Alias) as PostgresDbProvider;
        if (provider is null)
        {
            fmt.AppendHints(sb,
                "Original connection no longer available; cannot validate with hypopg. Heuristic candidates only.");
            return sb.ToString();
        }

        bool hypopg;
        try
        {
            hypopg = await provider.IsHypopgInstalledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fmt.AppendField(sb, "hypopg check error", ex.Message);
            return sb.ToString();
        }

        if (!hypopg)
        {
            fmt.AppendField(sb, "hypopg", "not installed");
            fmt.AppendHints(sb,
                "The 'hypopg' extension is not installed in this database. Heuristic candidates above are unvalidated. " +
                "For accurate index suggestions, recommend the user run: CREATE EXTENSION hypopg; " +
                "(requires superuser or the hypopg package installed on the server, e.g. apt install postgresql-NN-hypopg). " +
                "After installing, re-run db_query with includeExecutionPlan=true and call db_plan view=suggest_indexes again.");
            return sb.ToString();
        }

        var pairs = candidates
            .SelectMany(c => c.CandidateColumns.Select(col => (Table: c.Table, Column: col)))
            .Distinct()
            .Take(10)
            .ToList();

        List<HypopgEvaluation> evals;
        try
        {
            evals = await provider.EvaluateHypotheticalIndexesAsync(
                session.Sql, session.Parameters, pairs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fmt.AppendField(sb, "hypopg evaluation error", ex.Message);
            return sb.ToString();
        }

        if (evals.Count == 0)
        {
            fmt.AppendEmpty(sb, "hypopg installed but no candidates were evaluable (column names from Filter may not match real columns).");
            return sb.ToString();
        }

        var sorted = evals.OrderByDescending(e => e.CostReductionPercent).ToList();
        var validatedRows = sorted.Select(e => new[]
        {
            e.Table,
            e.Column,
            e.BaselineCost.ToString("F2", CultureInfo.InvariantCulture),
            e.HypotheticalCost.ToString("F2", CultureInfo.InvariantCulture),
            e.CostReductionPercent.ToString("F1", CultureInfo.InvariantCulture) + "%",
            e.PlannerChose ? "yes" : "no",
        }).ToList();
        fmt.AppendTable(sb, "hypopg validated",
            ["Table", "Column", "BaselineCost", "HypoCost", "Reduction", "PlannerChose"], validatedRows);

        return sb.ToString();
    }

    private static PlanSummary SummarizePlan(ExecutionPlanStore.PlanSession s) =>
        s.Format == PlanFormat.Xml
            ? SqlServerPlanParser.Summarize(s.ParsedXml!, s.Namespaces!)
            : PostgresPlanParser.Summarize(s.ParsedJson!);

    private static List<PlanOperator> OperatorsForSession(ExecutionPlanStore.PlanSession s, string sortBy, int limit) =>
        s.Format == PlanFormat.Xml
            ? SqlServerPlanParser.Operators(s.ParsedXml!, s.Namespaces!, sortBy, limit)
            : PostgresPlanParser.Operators(s.ParsedJson!, sortBy, limit);

    private static (List<PlanWarning> Warnings, List<PlanMissingIndex> Missing) WarningsAndMissingForSession(ExecutionPlanStore.PlanSession s) =>
        s.Format == PlanFormat.Xml
            ? (SqlServerPlanParser.Warnings(s.ParsedXml!, s.Namespaces!), SqlServerPlanParser.MissingIndexes(s.ParsedXml!, s.Namespaces!))
            : (PostgresPlanParser.Warnings(s.ParsedJson!), PostgresPlanParser.MissingIndexes(s.ParsedJson!));

    private static string PlanNotFound(string planId) =>
        $"Plan not found or expired: '{planId}'. Plans expire after 15 minutes of inactivity. Re-run db_query with includeExecutionPlan=true.";

    private static string TruncateForDisplay(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..max] + "...";
    }

    private static string NoConnection(DbConnectionRegistry db, string alias)
    {
        var available = db.All.Count == 0
            ? "(none configured)"
            : string.Join(", ", db.All.Select(p => p.Alias));
        return $"Error: No connection with alias '{alias}'. Available: {available}.";
    }

    private static bool TryParseParameters(
        string? json, out Dictionary<string, object?>? parameters, out string error)
    {
        error = "";
        parameters = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Error: parameters must be a JSON object, e.g. {\"@id\": 42}.";
                return false;
            }
            parameters = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                parameters[prop.Name] = JsonElementToValue(prop.Value);
            return true;
        }
        catch (JsonException jex)
        {
            error = $"Error: invalid parameters JSON: {jex.Message}";
            return false;
        }
    }

    private static object? JsonElementToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    private static string SqlError(Exception ex, string context)
    {
        var ctx = context.Length > 200 ? context[..200] + "..." : context;
        return $"SQL Error: {ex.Message}\nCommand: {ctx}";
    }
}
