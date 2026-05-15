using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Path;

namespace RoslynMCP.Services.Database;

public static class PostgresPlanParser
{
    private static readonly HashSet<string> s_filterStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "null", "true", "false", "is", "like", "ilike", "in", "any", "all", "between", "case", "when", "then", "else", "end",
        "text", "integer", "int", "int2", "int4", "int8", "bigint", "smallint", "numeric", "decimal", "real", "double", "boolean", "bool",
        "date", "timestamp", "timestamptz", "time", "timetz", "interval", "uuid", "json", "jsonb", "bytea", "varchar", "char",
        "lower", "upper", "length", "trim", "btrim", "substr", "substring", "coalesce", "nullif", "array", "row", "extract", "now", "current_date",
        "current_timestamp", "to_char", "to_date", "to_timestamp", "abs", "round", "floor", "ceil", "ceiling",
    };

    private static readonly Regex s_identifier = new(@"\b([a-z_][a-z0-9_]*)\b", RegexOptions.Compiled);

    public static List<IndexCandidate> IndexCandidates(JsonNode root)
    {
        var result = new List<IndexCandidate>();
        var first = FirstRoot(root);
        if (first is null) return result;
        var plan = first["Plan"];
        if (plan is null) return result;

        foreach (var node in WalkAll(plan))
        {
            var nodeType = AsString(node["Node Type"]);
            var filter = AsString(node["Filter"]);
            if (string.IsNullOrEmpty(filter)) continue;

            var actualRows = (long)(AsDouble(node["Actual Rows"]) ?? 0);
            var removed = (long)(AsDouble(node["Rows Removed by Filter"]) ?? 0);
            var total = actualRows + removed;
            if (total < 100) continue;
            double ratio = total == 0 ? 0 : (double)removed / total;
            if (ratio < 0.9 && removed < 10_000) continue;

            var table = AsString(node["Relation Name"]);
            if (string.IsNullOrEmpty(table)) continue;
            var alias = AsString(node["Alias"]);

            var cols = ExtractColumnCandidates(filter);
            if (cols.Length == 0) continue;

            string reason = nodeType switch
            {
                "Seq Scan" => "SeqScanFilter",
                "Index Scan" or "Index Only Scan" or "Bitmap Heap Scan" => "PostIndexFilter",
                _ => nodeType,
            };

            result.Add(new IndexCandidate(
                table, string.IsNullOrEmpty(alias) || alias == table ? null : alias,
                cols, filter, removed, actualRows, ratio, reason));
        }

        return result;
    }

    private static IEnumerable<JsonNode> WalkAll(JsonNode node)
    {
        yield return node;
        if (node["Plans"] is JsonArray children)
        {
            foreach (var c in children)
            {
                if (c is null) continue;
                foreach (var d in WalkAll(c)) yield return d;
            }
        }
    }

    private static string[] ExtractColumnCandidates(string filter)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = s_identifier.Matches(filter);
        foreach (Match m in matches)
        {
            var id = m.Groups[1].Value;
            if (s_filterStopwords.Contains(id)) continue;
            if (id.Length <= 1) continue;
            if (double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
            seen.Add(id);
        }
        if (seen.Count == 0 || seen.Count > 5) return Array.Empty<string>();
        return seen.ToArray();
    }


    public static PlanSummary Summarize(JsonNode root)
    {
        var operators = AllOperators(root);
        double totalCost = 0;
        double? totalActual = null;

        var first = FirstRoot(root);
        if (first is not null)
        {
            var planNode = first["Plan"];
            if (planNode is not null)
                totalCost = AsDouble(planNode["Total Cost"]) ?? 0;
            totalActual = AsDouble(first["Execution Time"]) ?? AsDouble(planNode?["Actual Total Time"]);
        }

        if (totalCost == 0 && operators.Count > 0)
            totalCost = operators.Max(o => o.EstimatedTotalSubtreeCost);

        var warnings = Warnings(root);
        var top = operators
            .OrderByDescending(o => o.EstimatedTotalSubtreeCost)
            .Take(5)
            .ToList();

        return new PlanSummary(totalCost, totalActual, operators.Count, warnings.Count, 0, top);
    }

    public static List<PlanOperator> Operators(JsonNode root, string sortBy, int limit)
    {
        var ops = AllOperators(root);
        IEnumerable<PlanOperator> sorted = sortBy?.ToLowerInvariant() switch
        {
            "actual_rows" => ops.OrderByDescending(o => o.ActualRows ?? double.MinValue),
            "actual_elapsed" => ops.OrderByDescending(o => o.ActualElapsedMs ?? double.MinValue),
            "estimate_rows" => ops.OrderByDescending(o => o.EstimateRows),
            _ => ops.OrderByDescending(o => o.EstimatedTotalSubtreeCost),
        };
        if (limit > 0) sorted = sorted.Take(limit);
        return sorted.ToList();
    }

    public static List<PlanWarning> Warnings(JsonNode root)
    {
        var result = new List<PlanWarning>();
        foreach (var (op, json) in WalkPlanNodes(root))
        {
            var actual = AsDouble(json["Actual Rows"]);
            var planned = AsDouble(json["Plan Rows"]);
            var loops = AsDouble(json["Actual Loops"]) ?? 1;
            if (actual is { } a && planned is { } p && loops > 0)
            {
                var actualPerLoop = a;
                var ratio = actualPerLoop > 0 && p > 0
                    ? Math.Max(actualPerLoop / p, p / actualPerLoop)
                    : 0;
                if (ratio >= 10 && Math.Max(actualPerLoop, p) >= 100)
                {
                    result.Add(new PlanWarning(
                        "EstimateMismatch",
                        $"actual={a:F0} planned={p:F0} ratio={ratio:F1}",
                        op.NodeId));
                }
            }

            var nodeType = AsString(json["Node Type"]);
            if (nodeType == "Seq Scan" && (actual ?? 0) >= 10000)
            {
                result.Add(new PlanWarning(
                    "LargeSeqScan",
                    $"table={AsString(json["Relation Name"])} rows={actual:F0}",
                    op.NodeId));
            }
        }

        var first = FirstRoot(root);
        if (first is JsonObject obj && obj["Triggers"] is JsonArray triggers)
        {
            foreach (var t in triggers)
            {
                if (t is null) continue;
                result.Add(new PlanWarning(
                    "Trigger",
                    $"name={AsString(t["Trigger Name"])} time={AsDouble(t["Time"]):F2}ms calls={AsDouble(t["Calls"]):F0}",
                    null));
            }
        }

        return result;
    }

    public static List<PlanMissingIndex> MissingIndexes(JsonNode root) => new();

    public static List<JsonNode> RunJsonPath(JsonNode root, string jsonPath, int maxResults)
    {
        var path = JsonPath.Parse(jsonPath);
        var result = path.Evaluate(root);
        var nodes = new List<JsonNode>();
        if (result.Matches is null) return nodes;
        foreach (var m in result.Matches)
        {
            if (m.Value is null) continue;
            nodes.Add(m.Value);
            if (maxResults > 0 && nodes.Count >= maxResults) break;
        }
        return nodes;
    }

    private static List<PlanOperator> AllOperators(JsonNode root)
    {
        var result = new List<PlanOperator>();
        foreach (var (op, _) in WalkPlanNodes(root))
            result.Add(op);
        return result;
    }

    private static IEnumerable<(PlanOperator Op, JsonNode Json)> WalkPlanNodes(JsonNode root)
    {
        var first = FirstRoot(root);
        if (first is null) yield break;
        var plan = first["Plan"];
        if (plan is null) yield break;

        var counter = 0;
        foreach (var pair in Walk(plan, () => counter++))
            yield return pair;
    }

    private static IEnumerable<(PlanOperator Op, JsonNode Json)> Walk(JsonNode node, Func<int> nextId)
    {
        var id = nextId().ToString();
        var nodeType = AsString(node["Node Type"]);
        var totalCost = AsDouble(node["Total Cost"]) ?? 0;
        var startupCost = AsDouble(node["Startup Cost"]) ?? 0;
        var planRows = AsDouble(node["Plan Rows"]) ?? 0;
        var actualRows = AsDouble(node["Actual Rows"]);
        var actualElapsed = AsDouble(node["Actual Total Time"]);
        var actualLoops = AsDouble(node["Actual Loops"]);

        string? objRef = null;
        var rel = AsString(node["Relation Name"]);
        var alias = AsString(node["Alias"]);
        var idx = AsString(node["Index Name"]);
        if (!string.IsNullOrEmpty(rel))
        {
            objRef = string.IsNullOrEmpty(alias) || alias == rel ? rel : $"{rel} ({alias})";
            if (!string.IsNullOrEmpty(idx)) objRef += $" [{idx}]";
        }
        else if (!string.IsNullOrEmpty(idx))
            objRef = $"[{idx}]";

        yield return (new PlanOperator(
            id,
            nodeType,
            nodeType,
            planRows,
            actualRows,
            startupCost,
            totalCost - startupCost,
            totalCost,
            actualElapsed,
            actualLoops is null ? null : (int)actualLoops,
            objRef), node);

        if (node["Plans"] is JsonArray children)
        {
            foreach (var c in children)
            {
                if (c is null) continue;
                foreach (var pair in Walk(c, nextId))
                    yield return pair;
            }
        }
    }

    private static JsonNode? FirstRoot(JsonNode root)
    {
        if (root is JsonArray arr && arr.Count > 0) return arr[0];
        return root;
    }

    private static double? AsDouble(JsonNode? n)
    {
        if (n is null) return null;
        try { return (double?)n.AsValue(); }
        catch { return null; }
    }

    private static string AsString(JsonNode? n)
    {
        if (n is null) return "";
        try { return n.AsValue().ToString() ?? ""; }
        catch { return ""; }
    }
}
