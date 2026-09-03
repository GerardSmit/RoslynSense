using System.Security.Cryptography;
using System.Text;

namespace RoslynMCP.Tests;

/// <summary>
/// Knobs for <see cref="LargeSolutionFixture.Create"/>.
/// </summary>
/// <param name="ProjectCount">How many ordinary, proto-free projects to generate. This is the
/// bulk of the solution and the number a scale test should tune; it is independent of
/// <paramref name="ConsumerProjectCount"/> so a caller can shrink it to a handful for a fast
/// smoke test without also having to shrink the gRPC side.</param>
/// <param name="FolderDepth">How many directory levels a project sits under, including the
/// project's own leaf folder — <c>src/Area01/Group02/Sub03/ProjectName/</c> for a depth of 4.
/// Solution folders mirror the same nesting, so this is what makes the generated tree exercise a
/// deep hierarchy rather than one flat <c>src/</c> full of siblings.</param>
/// <param name="FilesPerProject">How many <c>.cs</c> files each ordinary or consumer project
/// gets, nested a couple of folders into the project itself.</param>
/// <param name="ConsumerProjectCount">How many projects reference Contracts and call the
/// generated gRPC client — the population <c>ProtoGeneratedIndex</c> has to bind for every one
/// of them.</param>
internal sealed record LargeSolutionOptions(
    int ProjectCount = 60,
    int FolderDepth = 4,
    int FilesPerProject = 12,
    int ConsumerProjectCount = 20);

/// <summary>
/// A synthetic solution materialised on disk by <see cref="LargeSolutionFixture.Create"/>: one
/// Contracts project real callers reach through gRPC, a population of hand-written callers, and a
/// deep tree of ordinary projects wired into a realistic reference graph.
/// </summary>
/// <remarks>
/// Every path here is absolute and points into <see cref="Directory"/>, which is a temp directory
/// created for this instance alone — nothing is shared between two <see cref="LargeSolution"/>
/// created from the same options, so parallel tests never collide on disk.
/// </remarks>
internal sealed class LargeSolution : IDisposable
{
    public string Directory { get; }
    public string SolutionPath { get; }
    public IReadOnlyList<string> ProjectPaths { get; }
    public string ContractsProjectPath { get; }
    public string WidgetsProtoPath { get; }
    public IReadOnlyList<string> ConsumerFiles { get; }

    internal LargeSolution(
        string directory,
        string solutionPath,
        IReadOnlyList<string> projectPaths,
        string contractsProjectPath,
        string widgetsProtoPath,
        IReadOnlyList<string> consumerFiles)
    {
        Directory = directory;
        SolutionPath = solutionPath;
        ProjectPaths = projectPaths;
        ContractsProjectPath = contractsProjectPath;
        WidgetsProtoPath = widgetsProtoPath;
        ConsumerFiles = consumerFiles;
    }

    /// <summary>
    /// Deletes <see cref="Directory"/>, best-effort. A generated tree can be large enough that a
    /// virus scanner or a leftover MSBuild node still has a handle on something inside it, and a
    /// test's teardown failing over a stray lock would be a worse outcome than leaving a temp
    /// directory behind for the OS to reclaim.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Builds a large, deeply nested solution for tests that care about scale rather than about any
/// one file's content — solution-tree rendering, workspace load time, and the proto index doing
/// its work across dozens of projects instead of the four <c>ProtoSolution</c> holds.
/// </summary>
/// <remarks>
/// <para>
/// Hermetic by construction. The only <c>PackageReference</c>s anywhere in the generated tree are
/// the two <c>ProtoSolution/Contracts</c> already pins — <c>Google.Protobuf</c> and
/// <c>Grpc.Core.Api</c> — at the exact versions that fixture restores, so this fixture's restore
/// never reaches the network on a machine that has already built the test suite once. Every other
/// project carries only <c>ProjectReference</c>s.
/// </para>
/// <para>
/// The Contracts project's <c>.proto</c> and its generated C# are not reinvented here: they are
/// read straight from <c>ProtoSolution/Contracts</c> and republished under this fixture's own
/// namespace. Retyping protoc's output by hand is how a "faithful copy" quietly drifts from the
/// anchors <c>ProtoGeneratedIndex</c> actually reads — the source header, the
/// <c>Descriptor.MessageTypes[N]</c> chain, the <c>…FieldNumber</c> constants and
/// <c>__ServiceName</c> — and copying the file instead of its shape makes drift impossible.
/// </para>
/// <para>
/// Everything else is generated from the options alone, with no <see cref="Random"/> anywhere:
/// names come from indexing into small vocabularies by project and file position, and the
/// reference graph comes from a fixed multiplicative hash of the project index. Two calls with
/// the same options produce byte-identical trees, which is what makes a generation-time or
/// restore-time regression reproducible instead of a one-off.
/// </para>
/// </remarks>
internal static class LargeSolutionFixture
{
    private const string TargetWidgetsNamespace = "LargeSolution.Widgets";
    private const string SourceWidgetsNamespace = "ProtoSolution.Widgets";

    private const string CSharpProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
    private const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

    /// <summary>How many buckets each solution-folder level splits its projects into. Fixed
    /// rather than derived from <see cref="LargeSolutionOptions"/>: the point is a tree with
    /// several projects per leaf folder at any project count, not a perfectly balanced one.</summary>
    private const int BranchingFactor = 4;

    private static readonly string[] s_folderLevelNames =
    [
        "Area", "Group", "Sub", "Division", "Cluster", "Module", "Layer", "Branch", "Segment", "Zone",
    ];

    private static readonly string[] s_fileFolders =
    [
        "Domain", "Services", "Models", "Support", "Internal", "Shared",
    ];

    private static readonly string[] s_nouns =
    [
        "Gadget", "Sprocket", "Cog", "Gizmo", "Contraption", "Doohickey",
        "Flange", "Grommet", "Bracket", "Ratchet", "Ferrule", "Spindle",
    ];

    private static readonly string[] s_roles =
    [
        "Manager", "Registry", "Processor", "Coordinator", "Validator", "Tracker",
    ];

    private static readonly string[] s_verbs =
    [
        "Assemble", "Calibrate", "Inspect", "Package", "Ship", "Retire",
        "Refurbish", "Catalog", "Measure", "Label", "Polish", "Sort",
    ];

    private const string ContractsCsprojContent = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Library</OutputType>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>LargeSolution.Contracts</RootNamespace>
            <!--
              Generated\ is republished from ProtoSolution's own Contracts fixture, so it carries the
              same nullable-oblivious protoc output and needs the same warnings silenced to build clean.
            -->
            <NoWarn>$(NoWarn);CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625;CS8765;CS8767</NoWarn>
          </PropertyGroup>

          <!--
            Deliberately no Grpc.Tools and no protoc: only the runtime packages the generated code
            binds against, pinned to the versions ProtoSolution/Contracts already restores, so a
            solution built entirely from this fixture never touches the network.
          -->
          <ItemGroup>
            <PackageReference Include="Google.Protobuf" Version="3.32.1" />
            <PackageReference Include="Grpc.Core.Api" Version="2.71.0" />
          </ItemGroup>

        </Project>
        """;

    /// <summary>
    /// Generates a fresh solution under a new temp directory and returns a handle to it.
    /// </summary>
    /// <remarks>
    /// Layout: <c>Contracts/</c> sits at the solution root exactly as it does in
    /// <c>ProtoSolution</c>, since nothing about the proto binder benefits from nesting it; every
    /// consumer lives under <c>src/Consumers/…</c> and every ordinary project under
    /// <c>src/Ordinary/…</c>, each nested <see cref="LargeSolutionOptions.FolderDepth"/> levels
    /// deep with solution folders that mirror the same tree.
    /// </remarks>
    public static LargeSolution Create(LargeSolutionOptions? options = null)
    {
        var opts = options ?? new LargeSolutionOptions();

        string root = Path.Combine(Path.GetTempPath(), $"rsense-large-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var solution = new SolutionBuilder(root);
        var projectPaths = new List<string>();
        var consumerFiles = new List<string>();

        (string contractsProjectPath, string widgetsProtoPath) =
            WriteContractsProject(root, solution);
        projectPaths.Add(contractsProjectPath);

        int groupLevels = Math.Max(opts.FolderDepth - 1, 0);

        for (int i = 0; i < opts.ConsumerProjectCount; i++)
        {
            string csprojPath = WriteConsumerProject(
                root, solution, i, groupLevels, contractsProjectPath, opts.FilesPerProject, consumerFiles);
            projectPaths.Add(csprojPath);
        }

        var ordinaryCsprojPaths = new List<string>();
        for (int i = 0; i < opts.ProjectCount; i++)
        {
            string csprojPath = WriteOrdinaryProject(
                root, solution, i, groupLevels, ordinaryCsprojPaths, opts.FilesPerProject);
            ordinaryCsprojPaths.Add(csprojPath);
            projectPaths.Add(csprojPath);
        }

        string solutionPath = Path.Combine(root, "LargeSolution.sln");
        File.WriteAllText(solutionPath, solution.Build());

        return new LargeSolution(
            root, solutionPath, projectPaths, contractsProjectPath, widgetsProtoPath, consumerFiles);
    }

    // ---- Contracts ----------------------------------------------------------------------------

    private static (string ProjectPath, string ProtoPath) WriteContractsProject(
        string root, SolutionBuilder solution)
    {
        string dir = Path.Combine(root, "Contracts");
        Directory.CreateDirectory(dir);

        string csprojPath = Path.Combine(dir, "Contracts.csproj");
        File.WriteAllText(csprojPath, ContractsCsprojContent);

        string protoDir = Path.Combine(dir, "widgets");
        Directory.CreateDirectory(protoDir);
        string protoPath = Path.Combine(protoDir, "widgets.proto");
        File.WriteAllText(protoPath, RepublishedWidgetsSource(FixturePaths.ProtoSolutionWidgetsProtoFile));

        string generatedDir = Path.Combine(dir, "Generated", "widgets");
        Directory.CreateDirectory(generatedDir);
        File.WriteAllText(
            Path.Combine(generatedDir, "Widgets.cs"),
            RepublishedWidgetsSource(FixturePaths.ProtoSolutionWidgetsGeneratedFile));
        File.WriteAllText(
            Path.Combine(generatedDir, "WidgetsGrpc.cs"),
            RepublishedWidgetsSource(FixturePaths.ProtoSolutionWidgetsGrpcGeneratedFile));

        solution.AddProject("Contracts", csprojPath, folderChain: []);
        return (csprojPath, protoPath);
    }

    /// <summary>Reads one of ProtoSolution's own Contracts files and swaps its namespace for this
    /// fixture's, so two large solutions generated side by side never declare the same
    /// fully-qualified type as the fixture they were copied from.</summary>
    private static string RepublishedWidgetsSource(string sourcePath) =>
        File.ReadAllText(sourcePath).Replace(SourceWidgetsNamespace, TargetWidgetsNamespace, StringComparison.Ordinal);

    // ---- Consumers ------------------------------------------------------------------------------

    private static string WriteConsumerProject(
        string root,
        SolutionBuilder solution,
        int index,
        int groupLevels,
        string contractsProjectPath,
        int filesPerProject,
        List<string> consumerFiles)
    {
        string name = $"Consumer{index + 1:D3}";
        var folderChain = BuildFolderChain(["src", "Consumers"], index, groupLevels);
        string dir = CombineAll(root, folderChain, name);
        Directory.CreateDirectory(dir);

        string ns = $"LargeSolution.Consumers.{name}";
        string relativeToContracts = Path.GetRelativePath(dir, contractsProjectPath);

        string csprojPath = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(csprojPath, BuildLibraryCsproj(ns, [relativeToContracts]));

        for (int f = 0; f < filesPerProject; f++)
        {
            if (f == 0)
                consumerFiles.Add(WriteConsumerCallerFile(dir, ns, name));
            else
                WriteFillerFile(dir, ns, projectIndex: index, fileIndex: f);
        }

        solution.AddProject(name, csprojPath, folderChain);
        return csprojPath;
    }

    private static string WriteConsumerCallerFile(string dir, string namespaceName, string projectName)
    {
        string className = $"{projectName}Gateway";
        string content = $$"""
            using LargeSolution.Widgets;

            namespace {{namespaceName}};

            /// <summary>Calls widgets.WidgetService through the client Contracts generated — the
            /// only way this project reaches the contract, since it references Contracts and
            /// nothing else.</summary>
            public sealed class {{className}}
            {
                private readonly WidgetService.WidgetServiceClient _client;

                public {{className}}(WidgetService.WidgetServiceClient client)
                {
                    _client = client;
                }

                public async Task<List<string>> FetchLabelsAsync(IEnumerable<long> ids)
                {
                    var request = new GetWidgetsByIdRequest();
                    request.Ids.Add(ids);

                    var reply = await _client.GetWidgetsByIdAsync(request);

                    var labels = new List<string>();
                    foreach (var widget in reply.Widgets)
                    {
                        labels.Add(widget.Label);
                    }

                    return labels;
                }
            }

            """;

        string path = Path.Combine(dir, $"{className}.cs");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- Ordinary projects ----------------------------------------------------------------------

    private static string WriteOrdinaryProject(
        string root,
        SolutionBuilder solution,
        int index,
        int groupLevels,
        IReadOnlyList<string> earlierCsprojPaths,
        int filesPerProject)
    {
        string name = $"Ordinary{index + 1:D3}";
        var folderChain = BuildFolderChain(["src", "Ordinary"], index, groupLevels);
        string dir = CombineAll(root, folderChain, name);
        Directory.CreateDirectory(dir);

        string ns = $"LargeSolution.Ordinary.{name}";

        var references = PickReferenceIndices(index)
            .Select(r => Path.GetRelativePath(dir, earlierCsprojPaths[r]))
            .ToList();

        string csprojPath = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(csprojPath, BuildLibraryCsproj(ns, references));

        for (int f = 0; f < filesPerProject; f++)
            WriteFillerFile(dir, ns, projectIndex: index, fileIndex: f);

        solution.AddProject(name, csprojPath, folderChain);
        return csprojPath;
    }

    /// <summary>
    /// Which earlier ordinary projects (by index, all strictly less than <paramref name="index"/>)
    /// project <paramref name="index"/> references.
    /// </summary>
    /// <remarks>
    /// Only earlier indices are ever chosen, so a cycle is structurally impossible — no cycle
    /// detection is needed because none can occur. The spread across 1-3 references and across
    /// which earlier projects they land on comes from a fixed multiplicative hash of the index
    /// rather than <see cref="Random"/>, so the graph a caller inspects today is the graph it will
    /// see again tomorrow from the same options.
    /// </remarks>
    private static IReadOnlyList<int> PickReferenceIndices(int index)
    {
        if (index == 0)
            return [];

        int available = index;
        int count = Math.Min(available, 1 + index % 3);
        var chosen = new SortedSet<int>();

        uint h = unchecked((uint)index * 2654435761u);
        for (int guard = 0; chosen.Count < count && guard < count * 8; guard++)
        {
            h = unchecked(h * 2654435761u + (uint)guard + 1);
            chosen.Add((int)(h % (uint)available));
        }

        // A run of hash collisions is vanishingly unlikely at these counts, but filling
        // deterministically from the front keeps the result exactly `count` long even if it
        // happens, instead of silently under-wiring the graph.
        for (int fill = 0; chosen.Count < count; fill++)
            chosen.Add(fill % available);

        return [.. chosen];
    }

    // ---- Shared file/project generation ----------------------------------------------------------

    private static string BuildLibraryCsproj(string rootNamespace, IReadOnlyList<string> projectReferences)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <OutputType>Library</OutputType>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine($"    <RootNamespace>{rootNamespace}</RootNamespace>");
        sb.AppendLine("  </PropertyGroup>");

        if (projectReferences.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (string reference in projectReferences)
                sb.AppendLine($"    <ProjectReference Include=\"{reference}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    /// <summary>
    /// A small, self-contained type that pads a project out to its target file count without
    /// depending on anything another project declares — so the <c>ProjectReference</c> graph
    /// built in <see cref="WriteOrdinaryProject"/> stays the only thing a solution-wide search
    /// has to follow, rather than every file also being a second, code-level path between projects.
    /// </summary>
    private static void WriteFillerFile(string projectDir, string namespaceName, int projectIndex, int fileIndex)
    {
        string folderA = s_fileFolders[(projectIndex + fileIndex) % s_fileFolders.Length];
        string folderB = s_fileFolders[(projectIndex + fileIndex * 3 + 1) % s_fileFolders.Length];
        string dir = Path.Combine(projectDir, folderA, folderB);
        Directory.CreateDirectory(dir);

        string noun = s_nouns[fileIndex % s_nouns.Length];
        string role = s_roles[fileIndex % s_roles.Length];
        string verb1 = s_verbs[fileIndex % s_verbs.Length];
        string verb2 = s_verbs[(fileIndex + 5) % s_verbs.Length];

        // The file index alone already makes the name unique within the project regardless of how
        // the vocabularies above happen to repeat at a given FilesPerProject.
        string className = $"{noun}{role}{fileIndex:D2}";
        string recordName = $"{noun}Record{fileIndex:D2}";

        string content = $$"""
            namespace {{namespaceName}};

            /// <summary>Self-contained filler type — see
            /// <see cref="RoslynMCP.Tests.LargeSolutionFixture"/> for why it never references
            /// another project's types.</summary>
            public sealed class {{className}}
            {
                private readonly List<{{recordName}}> _items = [];

                public int {{verb1}}Count { get; private set; }

                public string {{verb1}}({{recordName}} item)
                {
                    {{verb1}}Count++;
                    _items.Add(item);
                    return item.Name;
                }

                public IReadOnlyList<{{recordName}}> {{verb2}}All() => _items;
            }

            /// <summary>Paired with <see cref="{{className}}"/>.</summary>
            public sealed record {{recordName}}(string Name, int Value);

            """;

        File.WriteAllText(Path.Combine(dir, $"{className}.cs"), content);
    }

    // ---- Directory / solution-folder nesting -----------------------------------------------------

    /// <summary>
    /// The chain of directory segments a project sits under, from the solution root down to —
    /// but not including — the project's own leaf folder: <paramref name="prefix"/> followed by
    /// <paramref name="groupLevels"/> computed segments that fan the project out into a balanced
    /// tree instead of one flat directory of siblings.
    /// </summary>
    private static List<string> BuildFolderChain(string[] prefix, int index, int groupLevels)
    {
        var chain = new List<string>(prefix);

        for (int level = 0; level < groupLevels; level++)
        {
            int divisor = IntPow(BranchingFactor, groupLevels - 1 - level);
            int levelIndex = index / divisor % BranchingFactor;
            string levelName = s_folderLevelNames[level % s_folderLevelNames.Length];
            chain.Add($"{levelName}{levelIndex + 1:D2}");
        }

        return chain;
    }

    private static int IntPow(int value, int exponent)
    {
        int result = 1;
        for (int i = 0; i < exponent; i++)
            result *= value;
        return result;
    }

    private static string CombineAll(string root, IReadOnlyList<string> segments, string leaf)
    {
        string path = root;
        foreach (string segment in segments)
            path = Path.Combine(path, segment);
        return Path.Combine(path, leaf);
    }

    // ---- .sln assembly -----------------------------------------------------------------------

    /// <summary>
    /// Accumulates projects and the solution folders they nest under, then renders a classic
    /// <c>.sln</c>. A separate builder rather than string-building inline in <c>Create</c>: the
    /// folder tree has to be deduplicated across every project that shares a branch of it before
    /// a single line of the file can be written, so the whole tree has to be known before any of
    /// it is rendered.
    /// </summary>
    private sealed class SolutionBuilder
    {
        private readonly string _root;
        private readonly List<(string Guid, string Name, string RelativePath)> _projects = [];

        // Keyed by the logical folder path ("/src/Ordinary/Area01") rather than by name, so two
        // different branches that happen to pick the same bucket name at the same level — which
        // the fixed branching factor guarantees will happen — still get distinct folders.
        private readonly Dictionary<string, string> _folderGuidsByPath = new(StringComparer.Ordinal);

        // Every folder and every project that has a parent, folder or project GUID to folder GUID.
        // A Dictionary rather than a list: two projects sharing a folder chain must record that
        // folder's parent only once, and keying on the child GUID makes that automatic.
        private readonly Dictionary<string, string> _parentByGuid = new(StringComparer.Ordinal);

        public SolutionBuilder(string root) => _root = root;

        public void AddProject(string name, string absoluteCsprojPath, IReadOnlyList<string> folderChain)
        {
            string guid = FormatGuid(DeterministicGuid("project:" + name));
            string relative = Path.GetRelativePath(_root, absoluteCsprojPath);
            _projects.Add((guid, name, relative));

            if (folderChain.Count > 0)
                _parentByGuid[guid] = EnsureFolderChain(folderChain);
        }

        private string EnsureFolderChain(IReadOnlyList<string> chain)
        {
            string? parent = null;
            var cumulative = new StringBuilder();

            foreach (string segment in chain)
            {
                cumulative.Append('/').Append(segment);
                string key = cumulative.ToString();

                if (!_folderGuidsByPath.TryGetValue(key, out string? guid))
                {
                    guid = FormatGuid(DeterministicGuid("folder:" + key));
                    _folderGuidsByPath[key] = guid;
                    if (parent is not null)
                        _parentByGuid[guid] = parent;
                }

                parent = guid;
            }

            return parent!;
        }

        public string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");

            foreach ((string path, string guid) in _folderGuidsByPath)
            {
                string name = path[(path.LastIndexOf('/') + 1)..];
                sb.AppendLine($"Project(\"{SolutionFolderTypeGuid}\") = \"{name}\", \"{name}\", \"{guid}\"");
                sb.AppendLine("EndProject");
            }

            foreach ((string guid, string name, string relativePath) in _projects)
            {
                sb.AppendLine($"Project(\"{CSharpProjectTypeGuid}\") = \"{name}\", \"{relativePath}\", \"{guid}\"");
                sb.AppendLine("EndProject");
            }

            sb.AppendLine("Global");

            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach ((string guid, _, _) in _projects)
            {
                sb.AppendLine($"\t\t{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
                sb.AppendLine($"\t\t{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
                sb.AppendLine($"\t\t{guid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
                sb.AppendLine($"\t\t{guid}.Release|Any CPU.Build.0 = Release|Any CPU");
            }
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("\t\tHideSolutionNode = FALSE");
            sb.AppendLine("\tEndGlobalSection");

            if (_parentByGuid.Count > 0)
            {
                sb.AppendLine("\tGlobalSection(NestedProjects) = preSolution");
                foreach ((string child, string parent) in _parentByGuid)
                    sb.AppendLine($"\t\t{child} = {parent}");
                sb.AppendLine("\tEndGlobalSection");
            }

            sb.AppendLine("EndGlobal");
            return sb.ToString();
        }
    }

    /// <summary>A GUID derived from a seed string rather than <see cref="Guid.NewGuid"/>, so the
    /// same seed always yields the same GUID and the whole <c>.sln</c> comes out byte-identical
    /// across two generations run with the same options.</summary>
    private static Guid DeterministicGuid(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash[..16]);
    }

    private static string FormatGuid(Guid guid) => guid.ToString("B").ToUpperInvariant();
}
