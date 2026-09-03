using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMCP.Services;
using RoslynMCP.Services.Designers;

namespace RoslynMCP.Tools;

[McpServerToolType]
public static class RegenerateDesignerTool
{
    [McpServerTool, Description(
        "Regenerate the generated .designer.cs companion for WebForms markup (.aspx/.ascx/.master) " +
        "and LINQ to SQL models (.dbml) — the files Visual Studio maintains via custom tools. " +
        "Use this after editing markup instead of hand-editing the .designer.cs, which is " +
        "overwritten whenever the file is regenerated. Accepts a single file, a .csproj, a .sln, " +
        "or a directory. Set dryRun to preview without writing.")]
    public static async Task<string> RegenerateDesigner(
        [Description("Path to a markup/.dbml file, a .csproj, a .sln, or a directory to sweep.")]
        string path,
        DesignerRegenerationService service,
        IOutputFormatter fmt,
        [Description("Report what would change without writing any file.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "Error: 'path' is required.";

            var resolved = PathHelper.NormalizePath(path);
            if (!File.Exists(resolved) && !Directory.Exists(resolved))
                return $"Error: '{path}' does not exist.";

            var results = await service.RegenerateManyAsync(resolved, dryRun, cancellationToken);
            return Format(results, resolved, dryRun, fmt);
        }
        catch (OperationCanceledException)
        {
            return "Error: Regeneration was cancelled.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string Format(
        List<DesignerRegeneration> results, string path, bool dryRun, IOutputFormatter fmt)
    {
        var considered = results.Where(r => r.Outcome != DesignerOutcome.Skipped).ToList();
        if (considered.Count == 0)
            return $"No .aspx/.ascx/.master/.dbml files found under '{path}'.";

        var sb = new StringBuilder();
        var updated = considered.Count(r => r.Outcome is DesignerOutcome.Updated or DesignerOutcome.WouldUpdate);
        var unchanged = considered.Count(
            r => r.Outcome is DesignerOutcome.Unchanged or DesignerOutcome.NotNeeded);
        var failed = considered.Where(r => r.Outcome == DesignerOutcome.Failed).ToList();

        sb.AppendLine(dryRun
            ? $"**Designer regeneration (dry run) — {updated} would change, {unchanged} unchanged, {failed.Count} failed**"
            : $"**Designer regeneration — {updated} updated, {unchanged} unchanged, {failed.Count} failed**");
        sb.AppendLine();

        // An all-unchanged sweep is the common case; listing every file would be noise.
        var interesting = considered
            .Where(r => r.Outcome is not (DesignerOutcome.Unchanged or DesignerOutcome.NotNeeded))
            .ToList();
        if (interesting.Count > 0)
        {
            sb.AppendLine("| File | Result |");
            sb.AppendLine("|------|--------|");
            foreach (var result in interesting)
            {
                var status = result.Outcome switch
                {
                    DesignerOutcome.Updated => "updated",
                    DesignerOutcome.WouldUpdate => "would update",
                    DesignerOutcome.Failed => "failed",
                    _ => result.Outcome.ToString().ToLowerInvariant(),
                };
                sb.AppendLine($"| {Path.GetFileName(result.SourcePath)} | {status} |");
            }
            sb.AppendLine();
        }

        foreach (var failure in failed)
        {
            sb.AppendLine($"**{Path.GetFileName(failure.SourcePath)}** — existing designer left untouched:");
            foreach (var error in failure.Errors)
                sb.AppendLine($"- {error}");
            sb.AppendLine();
        }

        if (dryRun)
        {
            foreach (var result in interesting.Where(r => r.ProposedContent is not null))
            {
                sb.AppendLine($"## {Path.GetFileName(result.DesignerPath)}");
                sb.AppendLine("```csharp");
                sb.AppendLine(result.ProposedContent!.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        if (!dryRun && updated > 0)
            fmt.AppendHints(sb, "Run GetRoslynDiagnostics on the affected code-behind to confirm it compiles");

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
