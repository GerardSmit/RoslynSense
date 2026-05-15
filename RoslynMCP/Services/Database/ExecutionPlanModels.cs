namespace RoslynMCP.Services.Database;

public enum PlanFormat
{
    Xml,
    Json,
}

public sealed record PlanOperator(
    string NodeId,
    string PhysicalOp,
    string LogicalOp,
    double EstimateRows,
    double? ActualRows,
    double EstimateCpu,
    double EstimateIo,
    double EstimatedTotalSubtreeCost,
    double? ActualElapsedMs,
    int? ActualExecutions,
    string? ObjectRef);

public sealed record PlanSummary(
    double TotalEstimatedCost,
    double? TotalActualElapsedMs,
    int OperatorCount,
    int WarningCount,
    int MissingIndexCount,
    List<PlanOperator> TopByEstimatedCost);

public sealed record PlanWarning(string Type, string Detail, string? NodeId);

public sealed record PlanMissingIndex(
    double Impact,
    string Database,
    string Schema,
    string Table,
    string[] EqualityColumns,
    string[] InequalityColumns,
    string[] IncludeColumns);

public sealed record IndexCandidate(
    string Table,
    string? Alias,
    string[] CandidateColumns,
    string FilterExpression,
    long RowsRemoved,
    long ActualRows,
    double ImpactRatio,
    string Reason);

public sealed record HypopgEvaluation(
    string Table,
    string Column,
    double BaselineCost,
    double HypotheticalCost,
    double CostReductionPercent,
    bool PlannerChose);
