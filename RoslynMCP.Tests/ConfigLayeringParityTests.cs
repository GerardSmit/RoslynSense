using System.Text.Json;
using System.Text.Json.Nodes;
using RoslynMCP.Config;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The half of the layering contract this suite owns.
/// </summary>
/// <remarks>
/// <para>
/// The rule lives twice — here and in <c>vscode-extension/src/roslynsenseConfig.ts</c>, because the
/// document selector and the settings page both need it before there is a server to ask. The cost
/// of that is a rule that can drift, and <c>Fixtures/ConfigLayering/parity.json</c> is what stops
/// it: the extension's <c>parity.test.ts</c> reads the same file and expects the same answers.
/// </para>
/// <para>
/// In the serialized collection because the home directory is an environment variable.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public sealed class ConfigLayeringParityTests : IDisposable
{
    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonObject s_fixture =
        (JsonObject)JsonNode.Parse(
            File.ReadAllText(FixturePaths.ConfigLayeringParityFile),
            documentOptions: s_documentOptions)!;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "roslynsense-parity-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previousHome =
        Environment.GetEnvironmentVariable(ConfigPaths.HomeOverrideVariable);

    private string Home => Path.Combine(_root, "home");

    public ConfigLayeringParityTests()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, Home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    public static TheoryData<string, string> MangledDirectories()
    {
        string platform = OperatingSystem.IsWindows() ? "windows" : "posix";
        var cases = (JsonArray)s_fixture["mangledDirectories"]![platform]!;

        var data = new TheoryData<string, string>();
        foreach (var entry in cases.Cast<JsonObject>())
            data.Add((string)entry["directory"]!, (string)entry["expected"]!);

        return data;
    }

    [Theory]
    [MemberData(nameof(MangledDirectories))]
    public void MangledDirectoriesMatchTheSharedFixture(string directory, string expected) =>
        Assert.Equal(expected, ConfigPaths.MangleDirectory(directory));

    public static TheoryData<string> MergeCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ((JsonArray)s_fixture["mergeCases"]!).Cast<JsonObject>())
            data.Add((string)entry["name"]!);

        return data;
    }

    [Theory]
    [MemberData(nameof(MergeCaseNames))]
    public void MergeCasesMatchTheSharedFixture(string name)
    {
        var testCase = ((JsonArray)s_fixture["mergeCases"]!)
            .Cast<JsonObject>()
            .Single(entry => (string)entry["name"]! == name);

        string parent = Path.Combine(_root, "checkout");
        string repo = Path.Combine(parent, "app");
        Directory.CreateDirectory(repo);

        foreach (var (scope, contents) in (JsonObject)testCase["files"]!)
            Write(FilePathFor(scope, parent, repo), contents);

        var layered = RoslynSenseConfigLoader.LoadLayers(repo);
        var expected = testCase["expected"];

        Assert.True(
            JsonNode.DeepEquals(expected, layered.MergedJson),
            $"{name}: expected {expected?.ToJsonString()}, merged {layered.MergedJson?.ToJsonString() ?? "null"}");

        if (testCase["expectLoadError"]?.GetValue<bool>() == true)
            Assert.NotNull(layered.LoadError);
        else
            Assert.Null(layered.LoadError);
    }

    private string FilePathFor(string scope, string parent, string repo) => scope switch
    {
        "global" => Path.Combine(Home, RoslynSenseConfigLoader.FileName),
        "personal" => ConfigPaths.PersonalConfigFile(repo)!,
        "parent" => Path.Combine(parent, RoslynSenseConfigLoader.FileName),
        "parentLocal" => Path.Combine(parent, RoslynSenseConfigLoader.LocalFileName),
        "repo" => Path.Combine(repo, RoslynSenseConfigLoader.FileName),
        "repoLocal" => Path.Combine(repo, RoslynSenseConfigLoader.LocalFileName),
        _ => throw new InvalidOperationException($"The fixture names an unknown scope: {scope}"),
    };

    /// <summary>
    /// A string in the fixture is the file's text verbatim — that is how it describes a file that
    /// does not parse, or one with comments in it. Anything else is written as JSON.
    /// </summary>
    private static void Write(string path, JsonNode? contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            contents is JsonValue value && value.TryGetValue(out string? text)
                ? text
                : contents?.ToJsonString() ?? "null");
    }
}
