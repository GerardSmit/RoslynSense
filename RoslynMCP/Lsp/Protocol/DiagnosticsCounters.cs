using System.Text.Json.Serialization;

namespace RoslynMCP.Lsp.Protocol;

// Custom extension (roslynSense/diagnosticsCounters): a test seam, not a feature. It surfaces
// process-internal counters that a real editor has no use for, so an out-of-process test driving
// the actual `--lsp` server can assert on server-internal behaviour — such as whether an
// incidental request silently pulled a project into the workspace — without instrumenting the
// transport itself.

public sealed record DiagnosticsCounters(
    [property: JsonPropertyName("incrementalLoadCount")] int IncrementalLoadCount);
