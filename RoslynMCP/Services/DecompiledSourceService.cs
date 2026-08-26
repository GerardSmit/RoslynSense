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
using RoslynMCP.Debugger;
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

        return await OpenSingleFileProjectAsync(
            manifest.AssemblyPath,
            manifest.SourceFilePath,
            BuildProjectName(manifest),
            cancellationToken);
    }

    /// <summary>
    /// An ad-hoc project holding one file, referencing everything beside the assembly it came
    /// from. What gives a file outside the solution hover, navigation and completion.
    /// </summary>
    /// <remarks>
    /// Shared with the fetched-source paths, which have a real file and a real assembly but no
    /// decompiler in sight. The compilation is not expected to be clean — framework source names
    /// partial declarations that are not here — but a semantic model does not need it to be, and
    /// diagnostics are suppressed for these files anyway.
    /// </remarks>
    internal static async Task<(Workspace Workspace, Project Project, string? TempDir)> OpenSingleFileProjectAsync(
        string assemblyPath,
        string sourceFilePath,
        string projectName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException(
                $"Source file '{sourceFilePath}' does not exist.", sourceFilePath);

        var workspace = new AdhocWorkspace();
        string? tempDir = null;
        try
        {
            var projectId = ProjectId.CreateNewId(projectName);

            var (metadataReferences, createdTempDir) = CreateMetadataReferences(assemblyPath);
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

            string sourceText = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
            var documentId = DocumentId.CreateNewId(projectId, Path.GetFileName(sourceFilePath));
            solution = solution.AddDocument(
                documentId,
                Path.GetFileName(sourceFilePath),
                SourceText.From(sourceText, s_utf8NoBom),
                filePath: sourceFilePath);

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException(
                    $"Failed to create AdhocWorkspace project for '{sourceFilePath}'.");
            }

            var project = workspace.CurrentSolution.GetProject(projectId)
                ?? throw new InvalidOperationException(
                    $"Generated project '{projectName}' was not found after creation.");

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
    /// Decompiles the type declaring a stopped frame's method and maps the frame's IL offset to
    /// a line in the decompiled text, through the sequence points the decompiler emits for its
    /// own output. This is what makes stepping into a dependency without symbols land on the
    /// executing statement rather than at the top of the file.
    /// </summary>
    /// <returns>The cached decompiled file and the 1-based position of the statement the IL
    /// offset falls in; the type declaration when the method has no mappable statement there;
    /// null when the type cannot be decompiled.</returns>
    public static async Task<(string FilePath, int Line, int Column)?> TryDecompileFrameAsync(
        string assemblyPath,
        string reflectionTypeName,
        int methodToken,
        int ilOffset,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            var map = await Task.Run(
                () => FrameMapFor(assemblyPath, reflectionTypeName, cancellationToken),
                cancellationToken);

            if (map.PointsByToken.TryGetValue(methodToken, out var points))
            {
                int picked = DebugFrameSource.PickSequencePoint(
                    [.. points.Select(p => (p.Offset, IsHidden: false))], ilOffset);
                if (picked >= 0)
                    return (map.FilePath, points[picked].Line, points[picked].Column);
            }

            var (line, character) = SourceMemberLocator.FindTypeDeclaration(
                map.SourceText, reflectionTypeName, cancellationToken);
            return (map.FilePath, line + 1, character + 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not map a frame into '{reflectionTypeName}' from " +
                $"'{Path.GetFileName(assemblyPath)}': {ex.Message}",
                key: $"decompile-frame:{assemblyPath}:{reflectionTypeName}");
            return null;
        }
    }

    /// <summary>
    /// The frame map read backwards: which MethodDef token and IL offset a line of the decompiled
    /// text corresponds to, so a breakpoint set inside a <c>Decompiled.cs</c> can bind on the IL.
    /// Slides down to the next line carrying a sequence point, like breakpoints in real source do.
    /// </summary>
    /// <returns>The token, offset, and the 1-based line actually mapped; null when no line at or
    /// below the requested one carries a sequence point, or the on-disk text has drifted from the
    /// text the map was built for.</returns>
    public static async Task<(int MethodToken, int IlOffset, int Line, int Column)?> TryMapLineToIlAsync(
        string assemblyPath,
        string reflectionTypeName,
        string filePath,
        int line,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            var map = await Task.Run(
                () => FrameMapFor(assemblyPath, reflectionTypeName, cancellationToken),
                cancellationToken);

            // The map's lines are only meaningful against the text it was built from. The cache
            // file is deterministic, but guard against an edited or stale copy on disk.
            string onDisk = await File.ReadAllTextAsync(map.FilePath, cancellationToken);
            if (!string.Equals(onDisk, map.SourceText, StringComparison.Ordinal)
                || !string.Equals(
                    Path.GetFullPath(map.FilePath), Path.GetFullPath(filePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            (int Token, int Offset, int Line, int Column)? best = null;
            foreach (var (token, points) in map.PointsByToken)
            {
                foreach (var (offset, pointLine, column, _, _) in points)
                {
                    if (pointLine < line)
                        continue;
                    bool better = best is not { } b
                        || pointLine < b.Line
                        || (pointLine == b.Line && offset < b.Offset);
                    if (better)
                        best = (token, offset, pointLine, column);
                }
            }

            return best is { } picked
                ? (picked.Token, picked.Offset, picked.Line, picked.Column)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not map line {line} of a decompiled file back into " +
                $"'{Path.GetFileName(assemblyPath)}': {ex.Message}",
                key: $"decompile-line:{assemblyPath}:{reflectionTypeName}");
            return null;
        }
    }

    /// <summary>
    /// The same decompiled type as symbols the debug engine can read, rather than as an answer for
    /// one frame.
    /// </summary>
    /// <remarks>
    /// The data is identical either way — this is the map that <see cref="TryDecompileFrameAsync"/>
    /// already builds and caches. Handing it over means the engine can locate a frame, range a
    /// step, and bind a breakpoint inside the type itself, instead of the engine giving up and the
    /// host patching a file and line into the answer afterwards. The second of those covered the
    /// stack and nothing else: stepping over a line in decompiled code had no statement to run to.
    /// </remarks>
    public static async Task<DecompiledSymbolMap?> TrySymbolsForAsync(
        string assemblyPath,
        string reflectionTypeName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            var map = await Task.Run(
                () => FrameMapFor(assemblyPath, reflectionTypeName, cancellationToken),
                cancellationToken);

            var symbols = new DecompiledSymbolMap { FilePath = map.FilePath };
            foreach (var (token, points) in map.PointsByToken)
            {
                symbols.Methods[token] =
                    [.. points.Select(p => new DecompiledPoint(
                        p.Offset, p.Line, p.Column, p.EndLine, p.EndColumn))];
            }

            return symbols.IsEmpty ? null : symbols;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not build symbols for '{reflectionTypeName}' from " +
                $"'{Path.GetFileName(assemblyPath)}': {ex.Message}",
                key: $"decompile-symbols:{assemblyPath}:{reflectionTypeName}");
            return null;
        }
    }

    /// <summary>The manifest beside a decompiled source file, when the path is one.</summary>
    public static async Task<(string AssemblyPath, string TypeReflectionName)?> TryReadFrameManifestAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        if (!IsDecompiledPath(filePath) || TryGetGeneratedProjectPath(filePath) is not { } manifestPath)
            return null;

        try
        {
            var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
            return (manifest.AssemblyPath, manifest.TypeReflectionName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn(
                $"Could not read the decompiled manifest beside '{filePath}': {ex.Message}",
                key: $"decompile-manifest:{filePath}");
            return null;
        }
    }

    /// <summary>One decompiled type with its IL-offset→line map, cached because a stop usually
    /// steps through the same method many times.</summary>
    private sealed record DecompiledFrameMap(
        string FilePath,
        string SourceText,
        IReadOnlyDictionary<int, List<(int Offset, int Line, int Column, int EndLine, int EndColumn)>> PointsByToken);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string Assembly, long Stamp, string Type), DecompiledFrameMap> s_frameMaps = new();

    private static DecompiledFrameMap FrameMapFor(
        string assemblyPath, string reflectionTypeName, CancellationToken cancellationToken)
    {
        long stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(assemblyPath).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = 0;
        }

        return s_frameMaps.GetOrAdd(
            (assemblyPath, stamp, reflectionTypeName),
            _ => BuildFrameMap(assemblyPath, reflectionTypeName, cancellationToken));
    }

    /// <remarks>
    /// The text has to be written through a token writer that records positions back into the
    /// syntax tree — sequence points are built from those positions, and text produced any other
    /// way would leave them all at line zero. No reference-assembly redirect happens here, unlike
    /// the navigation path: the module and token came from the debuggee's loader, and they are
    /// only meaningful against that exact file.
    /// </remarks>
    private static DecompiledFrameMap BuildFrameMap(
        string assemblyPath, string reflectionTypeName, CancellationToken cancellationToken)
    {
        var settings = new DecompilerSettings();
        var decompiler = new CSharpDecompiler(assemblyPath, CreateLenientResolver(assemblyPath), settings)
        {
            CancellationToken = cancellationToken
        };

        var tree = decompiler.DecompileType(new DecompilerFullTypeName(reflectionTypeName));

        var writer = new StringWriter();
        var tokenWriter = ICSharpCode.Decompiler.CSharp.OutputVisitor.TokenWriter
            .CreateWriterThatSetsLocationsInAST(writer);
        tree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.CSharpOutputVisitor(
            tokenWriter, settings.CSharpFormattingOptions));
        string sourceText = writer.ToString();

        var pointsByToken =
            new Dictionary<int, List<(int Offset, int Line, int Column, int EndLine, int EndColumn)>>();
        foreach (var (function, points) in decompiler.CreateSequencePoints(tree))
        {
            if (function?.Method is not { MetadataToken.IsNil: false } method)
                continue;

            int token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(method.MetadataToken);
            // The end of each point as well as its start: a statement's span is what an active
            // statement is reported as and what a step has to run to, and neither can be recovered
            // from the start alone.
            var mapped = points
                .Where(p => !p.IsHidden)
                .Select(p => (p.Offset, p.StartLine, p.StartColumn,
                    p.EndLine == 0 ? p.StartLine : p.EndLine, p.EndColumn))
                .OrderBy(p => p.Offset)
                .ToList();

            if (mapped.Count > 0)
                pointsByToken[token] = mapped;
        }

        var (filePath, _) = PersistDecompiledType(assemblyPath, reflectionTypeName, sourceText);
        return new DecompiledFrameMap(filePath, sourceText, pointsByToken);
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
