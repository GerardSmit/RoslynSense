using System.Diagnostics;
using System.Xml;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.DotSettings.Core;

namespace RoslynMCP.Services.Designers;

/// <summary>
/// Regenerates the <c>.designer.cs</c> for a LINQ to SQL <c>.dbml</c> model by invoking SqlMetal,
/// the shipped equivalent of Visual Studio's <c>MSLinqToSQLGenerator</c> custom tool.
/// </summary>
/// <remarks>
/// SqlMetal reads <c>Class</c>, <c>EntityNamespace</c> and <c>ContextNamespace</c> straight out of
/// the <c>.dbml</c>. Where the model omits a namespace, Visual Studio falls back to the project's
/// default namespace plus the model's folder path, so this passes the same fallback explicitly.
/// Output is not byte-identical to Visual Studio's in every case — serialization mode in
/// particular can differ.
/// </remarks>
internal sealed class DbmlDesignerGenerator : IDesignerGenerator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public bool CanHandle(string filePath) =>
        Path.GetExtension(filePath).Equals(".dbml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// LINQ to SQL replaces the extension rather than appending to it: <c>Northwind.dbml</c>
    /// produces <c>Northwind.designer.cs</c>, not <c>Northwind.dbml.designer.cs</c>.
    /// </summary>
    public string GetDesignerPath(string filePath) =>
        Path.ChangeExtension(filePath, ".designer.cs");

    public async Task<DesignerResult> GenerateAsync(
        string filePath, Project project, CancellationToken cancellationToken)
    {
        var designerPath = GetDesignerPath(filePath);

        var sqlMetal = NetFxToolchain.Info.SqlMetal;
        if (sqlMetal.Length == 0)
        {
            return DesignerResult.Failed(designerPath,
                "SqlMetal.exe was not found. Install the Windows SDK (it ships under " +
                @"'Microsoft SDKs\Windows\v*\bin\NETFX * Tools').");
        }

        // Generate to a temporary file so a SqlMetal failure can never truncate a working designer.
        var tempPath = Path.Combine(Path.GetTempPath(), $"roslynsense-dbml-{Guid.NewGuid():N}.cs");

        try
        {
            var (exitCode, output) = await RunSqlMetalAsync(
                sqlMetal, filePath, tempPath, project, cancellationToken);

            if (exitCode != 0 || !File.Exists(tempPath))
            {
                return DesignerResult.Failed(designerPath,
                    $"SqlMetal exited with code {exitCode}.",
                    output.Length > 0 ? output : "(no output)");
            }

            var content = await File.ReadAllTextAsync(tempPath, cancellationToken);
            return new DesignerResult(designerPath, content, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DesignerResult.Failed(designerPath, $"SqlMetal invocation failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunSqlMetalAsync(
        string sqlMetal, string dbmlPath, string outputPath, Project project,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sqlMetal,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dbmlPath) ?? Environment.CurrentDirectory,
        };

        startInfo.ArgumentList.Add($"/code:{outputPath}");
        startInfo.ArgumentList.Add("/language:csharp");

        // Only supply a namespace when the model does not carry one; otherwise the flag would
        // override the author's explicit choice.
        if (!DbmlDeclaresNamespace(dbmlPath) && InferDefaultNamespace(dbmlPath, project) is { } ns)
            startInfo.ArgumentList.Add($"/namespace:{ns}");

        startInfo.ArgumentList.Add(dbmlPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await BuildProcessHelper.KillAndDrainAsync(process);
            return (-1, $"SqlMetal timed out after {Timeout.TotalSeconds:0} seconds.");
        }

        var combined = string.Join(
            Environment.NewLine,
            new[] { await stdout, await stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (process.ExitCode, combined.Trim());
    }

    /// <summary>
    /// Whether the model's root <c>Database</c> element already names a namespace, in which case
    /// SqlMetal uses it and no override should be passed.
    /// </summary>
    private static bool DbmlDeclaresNamespace(string dbmlPath)
    {
        try
        {
            using var reader = XmlReader.Create(
                dbmlPath, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.LocalName != "Database")
                    continue;

                return !string.IsNullOrEmpty(reader.GetAttribute("ContextNamespace"))
                    || !string.IsNullOrEmpty(reader.GetAttribute("EntityNamespace"));
            }
        }
        catch
        {
            // Unreadable model: let SqlMetal report the real error.
        }

        return false;
    }

    /// <summary>
    /// Mirrors Visual Studio's custom-tool default: the project's root namespace, extended by the
    /// model's folder path relative to the project.
    /// </summary>
    private static string? InferDefaultNamespace(string dbmlPath, Project project)
    {
        var rootNamespace = project.DefaultNamespace;
        if (string.IsNullOrWhiteSpace(rootNamespace))
            rootNamespace = Path.GetFileNameWithoutExtension(project.FilePath);
        if (string.IsNullOrWhiteSpace(rootNamespace))
            return null;

        var projectDir = Path.GetDirectoryName(project.FilePath);
        var dbmlDir = Path.GetDirectoryName(dbmlPath);
        if (projectDir is null || dbmlDir is null)
            return rootNamespace;

        string relative;
        try
        {
            relative = Path.GetRelativePath(projectDir, dbmlDir);
        }
        catch
        {
            return rootNamespace;
        }

        // A model outside the project directory contributes no namespace segments.
        if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal))
            return rootNamespace;

        // The project's .DotSettings has the last word on which of these folders is a namespace:
        // a model under a folder marked "do not create a namespace" belongs to the folder's parent.
        var segments = ReSharperSettings.ForProject(project.FilePath ?? dbmlPath)
            .NamespaceSegments(relative)
            .Select(SanitizeNamespaceSegment)
            .Where(s => s.Length > 0);

        return string.Join('.', new[] { rootNamespace }.Concat(segments));
    }

    private static string SanitizeNamespaceSegment(string segment)
    {
        var chars = segment
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .ToArray();
        var result = new string(chars);
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }
}
