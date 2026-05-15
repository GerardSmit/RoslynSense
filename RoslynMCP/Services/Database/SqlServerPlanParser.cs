using System.Globalization;
using System.Xml;

namespace RoslynMCP.Services.Database;

public static class SqlServerPlanParser
{
    public static PlanSummary Summarize(XmlDocument doc, XmlNamespaceManager ns)
    {
        var operators = AllOperators(doc, ns);
        var stmts = doc.SelectNodes("//sp:StmtSimple", ns);
        double totalCost = 0;
        if (stmts is not null)
        {
            foreach (XmlNode s in stmts)
                totalCost += DoubleAttr(s, "StatementSubTreeCost");
        }
        if (totalCost == 0)
            totalCost = operators.Sum(o => o.EstimatedTotalSubtreeCost);

        double? actualElapsed = null;
        foreach (var op in operators)
        {
            if (op.ActualElapsedMs is { } v && (actualElapsed is null || v > actualElapsed))
                actualElapsed = v;
        }

        var warnings = Warnings(doc, ns);
        var missing = MissingIndexes(doc, ns);

        var top = operators
            .OrderByDescending(o => o.EstimatedTotalSubtreeCost)
            .Take(5)
            .ToList();

        return new PlanSummary(totalCost, actualElapsed, operators.Count, warnings.Count, missing.Count, top);
    }

    public static List<PlanOperator> Operators(XmlDocument doc, XmlNamespaceManager ns, string sortBy, int limit)
    {
        var ops = AllOperators(doc, ns);
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

    public static List<PlanWarning> Warnings(XmlDocument doc, XmlNamespaceManager ns)
    {
        var result = new List<PlanWarning>();
        var warningContainers = doc.SelectNodes("//sp:Warnings", ns);
        if (warningContainers is null) return result;

        foreach (XmlNode container in warningContainers)
        {
            var nodeId = FindAncestorNodeId(container);
            foreach (XmlNode child in container.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                var detail = BuildAttrSummary(child);
                result.Add(new PlanWarning(child.LocalName, detail, nodeId));
            }
        }
        return result;
    }

    public static List<PlanMissingIndex> MissingIndexes(XmlDocument doc, XmlNamespaceManager ns)
    {
        var result = new List<PlanMissingIndex>();
        var groups = doc.SelectNodes("//sp:MissingIndexes/sp:MissingIndexGroup", ns);
        if (groups is null) return result;

        foreach (XmlNode g in groups)
        {
            double impact = DoubleAttr(g, "Impact");
            var idx = g.SelectSingleNode("sp:MissingIndex", ns);
            if (idx is null) continue;
            var db = StringAttr(idx, "Database").Trim('[', ']');
            var schema = StringAttr(idx, "Schema").Trim('[', ']');
            var table = StringAttr(idx, "Table").Trim('[', ']');

            var eq = new List<string>();
            var ineq = new List<string>();
            var inc = new List<string>();
            var groupsByUsage = idx.SelectNodes("sp:ColumnGroup", ns);
            if (groupsByUsage is not null)
            {
                foreach (XmlNode cg in groupsByUsage)
                {
                    var usage = StringAttr(cg, "Usage");
                    var target = usage switch
                    {
                        "EQUALITY" => eq,
                        "INEQUALITY" => ineq,
                        "INCLUDE" => inc,
                        _ => null,
                    };
                    if (target is null) continue;
                    var cols = cg.SelectNodes("sp:Column", ns);
                    if (cols is null) continue;
                    foreach (XmlNode c in cols)
                        target.Add(StringAttr(c, "Name").Trim('[', ']'));
                }
            }

            result.Add(new PlanMissingIndex(impact, db, schema, table, eq.ToArray(), ineq.ToArray(), inc.ToArray()));
        }
        return result;
    }

    public static List<XmlNode> RunXPath(XmlDocument doc, XmlNamespaceManager ns, string xpath, int maxResults)
    {
        var nodes = doc.SelectNodes(xpath, ns);
        var result = new List<XmlNode>();
        if (nodes is null) return result;
        foreach (XmlNode n in nodes)
        {
            result.Add(n);
            if (maxResults > 0 && result.Count >= maxResults) break;
        }
        return result;
    }

    private static List<PlanOperator> AllOperators(XmlDocument doc, XmlNamespaceManager ns)
    {
        var result = new List<PlanOperator>();
        var relops = doc.SelectNodes("//sp:RelOp", ns);
        if (relops is null) return result;

        foreach (XmlNode op in relops)
        {
            var nodeId = StringAttr(op, "NodeId");
            var phys = StringAttr(op, "PhysicalOp");
            var logic = StringAttr(op, "LogicalOp");
            var estRows = DoubleAttr(op, "EstimateRows");
            var estCpu = DoubleAttr(op, "EstimateCPU");
            var estIo = DoubleAttr(op, "EstimateIO");
            var subtree = DoubleAttr(op, "EstimatedTotalSubtreeCost");

            double? actualRows = null;
            double? actualElapsed = null;
            int? actualExec = null;
            var counters = op.SelectNodes("sp:RunTimeInformation/sp:RunTimeCountersPerThread", ns);
            if (counters is not null && counters.Count > 0)
            {
                double rowsSum = 0, elapsedMax = 0;
                int execSum = 0;
                bool any = false;
                foreach (XmlNode c in counters)
                {
                    any = true;
                    rowsSum += DoubleAttr(c, "ActualRows");
                    var elapsed = DoubleAttr(c, "ActualElapsedms");
                    if (elapsed > elapsedMax) elapsedMax = elapsed;
                    execSum += (int)DoubleAttr(c, "ActualExecutions");
                }
                if (any)
                {
                    actualRows = rowsSum;
                    actualElapsed = elapsedMax;
                    actualExec = execSum;
                }
            }

            string? objectRef = null;
            var obj = op.SelectSingleNode(".//sp:Object[@Table]", ns);
            if (obj is not null)
            {
                var schema = StringAttr(obj, "Schema").Trim('[', ']');
                var table = StringAttr(obj, "Table").Trim('[', ']');
                var index = StringAttr(obj, "Index").Trim('[', ']');
                var refStr = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
                if (!string.IsNullOrEmpty(index)) refStr += $" [{index}]";
                objectRef = refStr;
            }

            result.Add(new PlanOperator(
                nodeId, phys, logic,
                estRows, actualRows,
                estCpu, estIo, subtree,
                actualElapsed, actualExec,
                objectRef));
        }
        return result;
    }

    private static string? FindAncestorNodeId(XmlNode node)
    {
        var cur = node.ParentNode;
        while (cur is not null)
        {
            if (cur.NodeType == XmlNodeType.Element && cur.LocalName == "RelOp")
            {
                var id = StringAttr(cur, "NodeId");
                return string.IsNullOrEmpty(id) ? null : id;
            }
            cur = cur.ParentNode;
        }
        return null;
    }

    private static string BuildAttrSummary(XmlNode node)
    {
        if (node.Attributes is null || node.Attributes.Count == 0) return "";
        var parts = new List<string>();
        foreach (XmlAttribute a in node.Attributes)
            parts.Add($"{a.LocalName}={a.Value}");
        return string.Join(", ", parts);
    }

    private static string StringAttr(XmlNode node, string name)
    {
        if (node.Attributes is null) return "";
        var a = node.Attributes[name];
        return a?.Value ?? "";
    }

    private static double DoubleAttr(XmlNode node, string name)
    {
        var s = StringAttr(node, name);
        if (string.IsNullOrEmpty(s)) return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
