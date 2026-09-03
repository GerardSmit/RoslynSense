using System.Text.Json.Nodes;
using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The generated schema, and the copy of it the extension ships.
/// </summary>
/// <remarks>
/// The copy is checked in because the editor has to validate <c>roslynsense.json</c> without
/// running the server first, and the settings page builds its form from the same file. Checked-in
/// generated output goes stale silently, so the first test here is what stops it.
/// </remarks>
public class ConfigSchemaTests
{
    private static string? CheckedInSchemaPath =>
        FixturePaths.RepoRoot is { } root
            ? Path.Combine(root, "vscode-extension", "schemas", "roslynsense.schema.json")
            : null;

    [Fact]
    public void TheCheckedInSchemaMatchesTheGeneratedOne()
    {
        if (CheckedInSchemaPath is not { } path)
            return; // running outside the checkout; nothing to compare against

        string generated = ConfigSchema.GenerateText();
        string checkedIn = File.ReadAllText(path);

        Assert.True(
            Normalize(generated) == Normalize(checkedIn),
            $"{path} is out of date. Regenerate it with:\n"
            + "    dotnet run --project RoslynMCP -- --config-schema vscode-extension/schemas/roslynsense.schema.json");
    }

    /// <summary>
    /// Every field a person can reach from the settings page needs a label and a sentence — the
    /// fields inside a list item included, since those are the ones the page used to render as bare
    /// property names with nothing to explain them.
    /// </summary>
    [Fact]
    public void EverySettingHasATitleAndADescription()
    {
        var missing = new List<string>();
        Walk((JsonObject)ConfigSchema.Generate(), path: "", missing);

        Assert.Empty(missing);
    }

    [Fact]
    public void TheSchemaDescribesNothingThatIsNotASetting()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect((JsonObject)ConfigSchema.Generate(), path: "", known);

        var stale = ConfigSchema.DescribedPaths.Where(path => !known.Contains(path)).ToList();

        Assert.Empty(stale);
    }

    private static void Walk(JsonObject node, string path, List<string> missing)
    {
        if (node["items"] is JsonObject item)
            Walk(item, path + "[]", missing);

        if (node["properties"] is not JsonObject properties)
            return;

        foreach (var (name, child) in properties)
        {
            if (child is not JsonObject childObject)
                continue;

            string childPath = path.Length == 0 ? name : $"{path}.{name}";

            // A list of plain strings has an element with nothing on it to describe; the list
            // itself carries the sentence.
            if (childObject["title"] is null || childObject["description"] is null)
                missing.Add(childPath);

            Walk(childObject, childPath, missing);
        }
    }

    private static void Collect(JsonObject node, string path, HashSet<string> into)
    {
        if (node["items"] is JsonObject item)
        {
            into.Add(path + "[]");
            Collect(item, path + "[]", into);
        }

        if (node["properties"] is not JsonObject properties)
            return;

        foreach (var (name, child) in properties)
        {
            if (child is not JsonObject childObject)
                continue;

            string childPath = path.Length == 0 ? name : $"{path}.{name}";
            into.Add(childPath);
            Collect(childObject, childPath, into);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
