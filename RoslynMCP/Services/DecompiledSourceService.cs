using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using MetadataReaderOptions = System.Reflection.Metadata.MetadataReaderOptions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.ExternalSource;
using DecompilerFullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;

namespace RoslynMCP.Services;

internal static class DecompiledSourceService
{
    internal const string ManifestFileName = "RoslynMCP.decompiled.json";

    private const string SourceFileName = "Decompiled.cs";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string s_rootDirectory = ExternalSourceCache.DecompiledDirectory;

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool IsGeneratedProjectPath(string projectPath) =>
        string.Equals(Path.GetFileName(projectPath), ManifestFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a path belongs to the decompiled cache — a source file, or the manifest that
    /// stands in for its project.
    /// </summary>
    public static bool IsDecompiledPath(string? path) =>
        path is { Length: > 0 } &&
        // The separator keeps a sibling like "...\DecompiledExtra" from matching by prefix.
        Path.GetFullPath(path).StartsWith(
            s_rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string? TryGetGeneratedProjectPath(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return null;

        string manifestPath = Path.Combine(directory, ManifestFileName);
        return File.Exists(manifestPath) ? manifestPath : null;
    }

    public static async Task<DecompiledSourceResult?> TryDecompileSymbolAsync(
        ISymbol symbol,
        Project contextProject,
        CancellationToken cancellationToken = default)
    {
        var containingType = SourceMemberLocator.GetOwningType(symbol);
        if (containingType is null)
            return null;

        string? assemblyPath = await ResolveAssemblyPathAsync(symbol, contextProject, cancellationToken);
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            return null;

        string reflectionTypeName = SourceMemberLocator.GetReflectionTypeName(containingType);

        // Compilations reference *reference assemblies* (SDK ref packs, nuget ref/ folders),
        // whose method bodies are all `throw null`. Redirect to the runtime implementation
        // assembly — following type forwarders (e.g. System.Runtime -> System.Private.CoreLib) —
        // so the decompiled source shows real bodies.
        assemblyPath = ReferenceAssemblyRedirector.RedirectToImplementation(assemblyPath, reflectionTypeName);

        string sourceText;
        try
        {
            sourceText = DecompileType(assemblyPath, reflectionTypeName, cancellationToken);
        }
        catch (ResolutionException ex)
        {
            Console.Error.WriteLine(
                $"[DecompiledSourceService] Decompilation skipped for '{reflectionTypeName}' in '{assemblyPath}': {ex.Message}");
            return null;
        }

        var (sourceFilePath, manifestPath) = PersistDecompiledType(assemblyPath, reflectionTypeName, sourceText);

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            manifestPath,
            targetFilePath: sourceFilePath,
            cancellationToken: cancellationToken);
        var document = WorkspaceService.FindDocumentInProject(project, sourceFilePath);

        if (document is null)
            return null;

        var sourceSymbol = await SourceMemberLocator.FindMatchingSourceSymbolAsync(
            document, symbol, cancellationToken);
        IReadOnlyList<Location> locations = sourceSymbol?.Locations.Where(location => location.IsInSource).ToList()
            ?? [];

        if (locations.Count == 0)
            locations = await SourceMemberLocator.FindMatchingLocationsBySyntaxAsync(
                document, symbol, cancellationToken);

        if (locations.Count == 0)
            return null;

        return new DecompiledSourceResult(assemblyPath, manifestPath, sourceFilePath, workspace, project, locations);
    }

    public static async Task<(Workspace Workspace, Project Project, string? TempDir)> OpenProjectAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);

        if (!File.Exists(manifest.SourceFilePath))
            throw new FileNotFoundException(
                $"Decompiled source file '{manifest.SourceFilePath}' does not exist.",
                manifest.SourceFilePath);

        var workspace = new AdhocWorkspace();
        string? tempDir = null;
        try
        {
            string projectName = BuildProjectName(manifest);
            var projectId = ProjectId.CreateNewId(projectName);

            var (metadataReferences, createdTempDir) = CreateMetadataReferences(manifest.AssemblyPath);
            tempDir = createdTempDir;

            var solution = workspace.CurrentSolution
                .AddProject(projectId, projectName, projectName, LanguageNames.CSharp)
                .WithProjectCompilationOptions(
                    projectId,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        allowUnsafe: true,
                        nullableContextOptions: NullableContextOptions.Enable))
                .WithProjectParseOptions(
                    projectId,
                    new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview))
                .AddMetadataReferences(projectId, metadataReferences);

            string sourceText = await File.ReadAllTextAsync(manifest.SourceFilePath, cancellationToken);
            var documentId = DocumentId.CreateNewId(projectId, Path.GetFileName(manifest.SourceFilePath));
            solution = solution.AddDocument(
                documentId,
                Path.GetFileName(manifest.SourceFilePath),
                SourceText.From(sourceText, s_utf8NoBom),
                filePath: manifest.SourceFilePath);

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException(
                    $"Failed to create AdhocWorkspace project for decompiled source '{manifest.SourceFilePath}'.");
            }

            var project = workspace.CurrentSolution.GetProject(projectId)
                ?? throw new InvalidOperationException(
                    $"Generated decompiled project '{projectName}' was not found after creation.");

            return (workspace, project, tempDir);
        }
        catch
        {
            workspace.Dispose();
            if (tempDir is not null)
                TryDeleteTempDir(tempDir);
            throw;
        }
    }

    /// <summary>Root for per-decompile temp copies of reference assemblies.</summary>
    private static readonly string s_decompileTempRoot =
        Path.Combine(Path.GetTempPath(), "RoslynMCP", "DecompileTemp");

    /// <summary>
    /// Deletes all orphaned decompile temp directories from previous runs. Called once at
    /// startup; safe because the copies only live for the duration of a process's workspaces.
    /// </summary>
    public static void CleanupOrphanedTempDirs()
    {
        try
        {
            if (Directory.Exists(s_decompileTempRoot))
                Directory.Delete(s_decompileTempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecompiledSourceService] Failed to clean temp root: {ex.Message}");
        }
    }

    internal static void TryDeleteTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecompiledSourceService] Failed to delete temp dir '{tempDir}': {ex.Message}");
        }
    }

    /// <summary>
    /// Decompiles one type by name, for callers that already know which assembly to look in —
    /// the editor opening a <c>roslynsense-metadata:</c> document, rather than a symbol
    /// navigation that has to work out the assembly first.
    /// </summary>
    /// <returns>The source, or <c>null</c> when the assembly or type cannot be read.</returns>
    public static async Task<string?> TryDecompileTypeAsync(
        string assemblyPath, string reflectionTypeName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
            return null;

        string resolved = ReferenceAssemblyRedirector.RedirectToImplementation(
            assemblyPath, reflectionTypeName);

        try
        {
            return await Task.Run(
                () => DecompileType(resolved, reflectionTypeName, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not decompile '{reflectionTypeName}' from '{Path.GetFileName(resolved)}': {ex.Message}",
                key: $"decompile:{resolved}:{reflectionTypeName}");
            return null;
        }
    }

    /// <summary>
    /// Decompiles a type into its cached on-disk file and returns where the declaration sits.
    /// The search panel's metadata hits resolve through this so that opening one lands in the
    /// same physical <c>Decompiled.cs</c> that F12 uses — with the manifest beside it, so the
    /// language services light up — rather than in a read-only virtual buffer.
    /// </summary>
    /// <returns>The file and the 0-based position of the type's identifier, or null when the
    /// assembly or type cannot be decompiled.</returns>
    public static async Task<(string FilePath, int Line, int Character)?> TryDecompileTypeToFileAsync(
        string assemblyPath, string reflectionTypeName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
            return null;

        string resolved = ReferenceAssemblyRedirector.RedirectToImplementation(
            assemblyPath, reflectionTypeName);

        try
        {
            string sourceText = await Task.Run(
                () => DecompileType(resolved, reflectionTypeName, cancellationToken), cancellationToken);

            var (sourceFilePath, _) = PersistDecompiledType(resolved, reflectionTypeName, sourceText);

            var (line, character) = SourceMemberLocator.FindTypeDeclaration(
                sourceText, reflectionTypeName, cancellationToken);
            return (sourceFilePath, line, character);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not decompile '{reflectionTypeName}' from '{Path.GetFileName(resolved)}': {ex.Message}",
                key: $"decompile:{resolved}:{reflectionTypeName}");
            return null;
        }
    }

    /// <summary>
    /// One writer for the decompile cache: F12 and the search panel both land here, and the lock
    /// keeps the two doors from tearing each other's <c>Decompiled.cs</c> or manifest.
    /// </summary>
    private static readonly object s_persistLock = new();

    private static (string SourceFilePath, string ManifestPath) PersistDecompiledType(
        string resolvedAssemblyPath, string reflectionTypeName, string sourceText)
    {
        string outputDirectory = GetOutputDirectory(resolvedAssemblyPath, reflectionTypeName);
        string sourceFilePath = Path.Combine(outputDirectory, SourceFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);

        lock (s_persistLock)
        {
            Directory.CreateDirectory(outputDirectory);
            WriteFileIfChanged(sourceFilePath, sourceText);
            WriteFileIfChanged(manifestPath, JsonSerializer.Serialize(new DecompiledSourceManifest
            {
                AssemblyPath = resolvedAssemblyPath,
                SourceFilePath = sourceFilePath,
                TypeReflectionName = reflectionTypeName
            }, s_jsonOptions));
        }

        return (sourceFilePath, manifestPath);
    }

    private static string DecompileType(
        string assemblyPath,
        string reflectionTypeName,
        CancellationToken cancellationToken)
    {
        var resolver = CreateLenientResolver(assemblyPath);
        var decompiler = new CSharpDecompiler(assemblyPath, resolver, new DecompilerSettings())
        {
            CancellationToken = cancellationToken
        };

        return decompiler.DecompileTypeAsString(new DecompilerFullTypeName(reflectionTypeName));
    }

    private static UniversalAssemblyResolver CreateLenientResolver(string assemblyPath)
    {
        string? targetFramework = null;
        string? runtimePack = null;

        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peFile = new PEFile(
                assemblyPath,
                stream,
                PEStreamOptions.PrefetchMetadata,
                MetadataReaderOptions.None);

            targetFramework = peFile.DetectTargetFrameworkId();
            runtimePack = peFile.DetectRuntimePack();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[DecompiledSourceService] Failed to detect target framework for '{assemblyPath}': {ex.Message}");
        }

        return new UniversalAssemblyResolver(
            assemblyPath,
            throwOnError: false,
            targetFramework,
            runtimePack,
            PEStreamOptions.PrefetchMetadata,
            MetadataReaderOptions.None);
    }

    private static async Task<string?> ResolveAssemblyPathAsync(
        ISymbol symbol,
        Project contextProject,
        CancellationToken cancellationToken)
    {
        var containingAssembly = symbol.ContainingAssembly;
        if (containingAssembly is null)
            return null;

        var compilation = await contextProject.GetCompilationAsync(cancellationToken);
        if (compilation is null)
            return null;

        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(reference.FilePath))
                continue;

            var referenceSymbol = compilation.GetAssemblyOrModuleSymbol(reference);

            if (referenceSymbol is IAssemblySymbol assemblySymbol &&
                SymbolEqualityComparer.Default.Equals(assemblySymbol, containingAssembly))
            {
                return Path.GetFullPath(reference.FilePath);
            }

            if (referenceSymbol is IModuleSymbol moduleSymbol &&
                SymbolEqualityComparer.Default.Equals(moduleSymbol.ContainingAssembly, containingAssembly))
            {
                return Path.GetFullPath(reference.FilePath);
            }
        }

        return null;
    }

    private static string GetOutputDirectory(string assemblyPath, string reflectionTypeName)
    {
        string assemblyName = SanitizePathSegment(Path.GetFileNameWithoutExtension(assemblyPath));
        string typeName = SanitizePathSegment(reflectionTypeName.Replace('+', '.'));
        string hash = ComputeHash($"{assemblyPath}\n{reflectionTypeName}");
        return Path.Combine(s_rootDirectory, $"{assemblyName}_{hash}", typeName);
    }

    private static string BuildProjectName(DecompiledSourceManifest manifest)
    {
        string assemblyName = Path.GetFileNameWithoutExtension(manifest.AssemblyPath);
        string typeName = manifest.TypeReflectionName.Replace('+', '.');
        return $"RoslynMCP.Decompiled.{assemblyName}.{typeName}";
    }

    /// <summary>
    /// Builds the metadata references for a decompiled project. Returns the references plus
    /// the temp directory (or <c>null</c>) the caller must delete when the workspace is evicted.
    /// <para>
    /// The target assembly and its co-located neighbours are frequently a project's <c>bin/</c>
    /// output. <see cref="MetadataReference.CreateFromFile"/> memory-maps the DLL and would lock
    /// it on disk for the cached workspace's lifetime, blocking the user's rebuild. So those are
    /// COPIED to a temp dir and referenced from the copy: the original stays unlocked, and the
    /// metadata stays OS-paged (cheap) rather than pinned as a managed byte array.
    /// TRUSTED_PLATFORM_ASSEMBLIES are immutable runtime/framework files — never a build target —
    /// so they are referenced directly from their original (memory-mapped) path.
    /// </para>
    /// </summary>
    private static (List<MetadataReference> References, string? TempDir) CreateMetadataReferences(string assemblyPath)
    {
        var references = new List<MetadataReference>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? tempDir = null;

        string EnsureTempDir() =>
            tempDir ??= CreateTempDir();

        void AddReference(string path, bool copyToTemp)
        {
            if (!File.Exists(path))
                return;

            string normalized = Path.GetFullPath(path);
            if (!seenPaths.Add(normalized))
                return;

            try
            {
                if (copyToTemp)
                {
                    string dest = Path.Combine(EnsureTempDir(), Path.GetFileName(normalized));
                    try
                    {
                        File.Copy(normalized, dest, overwrite: true);
                        references.Add(MetadataReference.CreateFromFile(dest));
                        return;
                    }
                    catch (Exception copyEx)
                    {
                        // Copy failed (e.g. a transient lock) — fall back to an in-memory image
                        // so we still never hold a lock on the original.
                        Console.Error.WriteLine(
                            $"[DecompiledSourceService] Temp-copy failed for '{normalized}', using in-memory image: {copyEx.Message}");
                        using var stream = new FileStream(
                            normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        references.Add(MetadataReference.CreateFromStream(stream, filePath: normalized));
                        return;
                    }
                }

                references.Add(MetadataReference.CreateFromFile(normalized));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DecompiledSourceService] Failed to add metadata reference '{normalized}': {ex.Message}");
            }
        }

        AddReference(assemblyPath, copyToTemp: true);

        // One framework, not two. This host runs on .NET 10, so its TRUSTED_PLATFORM_ASSEMBLIES
        // are CoreCLR's — correct for decompiling a Core assembly, and poison for a .NET Framework
        // one. Mixing them puts two definitions of the core library in a single compilation, and
        // the result is a decompiled file whose every framework type "does not exist": System.Web
        // resolves against .NET 10's System.Runtime rather than the mscorlib it was built for.
        // A Framework assembly's own directory carries the matching set, so that is used instead.
        if (!IsFrameworkAssembly(assemblyPath) &&
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (string path in trustedPlatformAssemblies.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddReference(path, copyToTemp: false);
            }
        }

        string? directory = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
                AddReference(path, copyToTemp: true);

            foreach (string path in Directory.EnumerateFiles(directory, "*.exe"))
                AddReference(path, copyToTemp: true);
        }

        return (references, tempDir);
    }

    /// <summary>
    /// Whether an assembly targets .NET Framework rather than CoreCLR.
    /// </summary>
    /// <remarks>
    /// Decided from what it references, not from where it sits: a Framework assembly references
    /// <c>mscorlib</c> as its core library, a Core one references <c>System.Runtime</c>. Paths
    /// would be a guess — reference assemblies, the GAC, unification directories and NuGet
    /// packages all hold both kinds.
    /// </remarks>
    internal static bool IsFrameworkAssembly(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!reader.HasMetadata)
                return false;

            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);

            // mscorlib itself references nothing, so it is recognised by its own name.
            if (metadata.GetString(metadata.GetAssemblyDefinition().Name)
                    .Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var handle in metadata.AssemblyReferences)
            {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // Unreadable: fall back to the host's own framework, which is what this did before.
        }

        return false;
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(s_decompileTempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<DecompiledSourceManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Decompiled manifest '{manifestPath}' does not exist.", manifestPath);

        string content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<DecompiledSourceManifest>(content)
            ?? throw new InvalidOperationException(
                $"Decompiled manifest '{manifestPath}' could not be deserialized.");

        if (string.IsNullOrWhiteSpace(manifest.AssemblyPath) ||
            string.IsNullOrWhiteSpace(manifest.SourceFilePath) ||
            string.IsNullOrWhiteSpace(manifest.TypeReflectionName))
        {
            throw new InvalidOperationException(
                $"Decompiled manifest '{manifestPath}' is missing required values.");
        }

        return manifest;
    }

    /// <summary>
    /// Writes a cache file, leaving it read-only.
    /// </summary>
    /// <remarks>
    /// Read-only because an edit here cannot survive: the next decompile of the same type
    /// overwrites the file, and losing someone's work silently is worse than refusing it. The
    /// attribute has to be cleared before the rewrite, or that overwrite throws.
    /// </remarks>
    private static void WriteFileIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
                return;

            ExternalSourceCache.ClearReadOnly(path);
        }

        File.WriteAllText(path, content, s_utf8NoBom);

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to protect the file is no reason not to have written it.
        }
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 12);

    private static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) || ch == '.'
                ? '_'
                : ch);
        }

        return builder.ToString();
    }

    private sealed class DecompiledSourceManifest
    {
        public string AssemblyPath { get; set; } = string.Empty;

        public string SourceFilePath { get; set; } = string.Empty;

        public string TypeReflectionName { get; set; } = string.Empty;
    }
}

internal sealed record DecompiledSourceResult(
    string AssemblyPath,
    string ProjectPath,
    string SourceFilePath,
    Workspace Workspace,
    Project Project,
    IReadOnlyList<Location> Locations);
