using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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

    private static readonly string s_rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RoslynMCP",
        "Decompiled");

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly SymbolDisplayFormat s_typeMatchDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static bool IsGeneratedProjectPath(string projectPath) =>
        string.Equals(Path.GetFileName(projectPath), ManifestFileName, StringComparison.OrdinalIgnoreCase);

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
        var containingType = GetOwningType(symbol);
        if (containingType is null)
            return null;

        string? assemblyPath = await ResolveAssemblyPathAsync(symbol, contextProject, cancellationToken);
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            return null;

        string reflectionTypeName = GetReflectionTypeName(containingType);
        string outputDirectory = GetOutputDirectory(assemblyPath, reflectionTypeName);
        string sourceFilePath = Path.Combine(outputDirectory, SourceFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);

        Directory.CreateDirectory(outputDirectory);

        string sourceText = DecompileType(assemblyPath, reflectionTypeName, cancellationToken);
        WriteFileIfChanged(sourceFilePath, sourceText);

        var manifest = new DecompiledSourceManifest
        {
            AssemblyPath = assemblyPath,
            SourceFilePath = sourceFilePath,
            TypeReflectionName = reflectionTypeName
        };
        WriteFileIfChanged(manifestPath, JsonSerializer.Serialize(manifest, s_jsonOptions));

        var (workspace, project) = await WorkspaceService.GetOrOpenProjectAsync(
            manifestPath,
            targetFilePath: sourceFilePath,
            cancellationToken: cancellationToken);
        var document = WorkspaceService.FindDocumentInProject(project, sourceFilePath);

        if (document is null)
            return null;

        var sourceSymbol = await FindMatchingSourceSymbolAsync(document, symbol, cancellationToken);
        IReadOnlyList<Location> locations = sourceSymbol?.Locations.Where(location => location.IsInSource).ToList()
            ?? [];

        if (locations.Count == 0)
            locations = await FindMatchingLocationsBySyntaxAsync(document, symbol, cancellationToken);

        if (locations.Count == 0)
            return null;

        return new DecompiledSourceResult(assemblyPath, manifestPath, sourceFilePath, workspace, project, locations);
    }

    public static async Task<(Workspace Workspace, Project Project)> OpenProjectAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);

        if (!File.Exists(manifest.SourceFilePath))
            throw new FileNotFoundException(
                $"Decompiled source file '{manifest.SourceFilePath}' does not exist.",
                manifest.SourceFilePath);

        var workspace = new AdhocWorkspace();
        try
        {
            string projectName = BuildProjectName(manifest);
            var projectId = ProjectId.CreateNewId(projectName);

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
                .AddMetadataReferences(projectId, CreateMetadataReferences(manifest.AssemblyPath));

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

            return (workspace, project);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static string DecompileType(
        string assemblyPath,
        string reflectionTypeName,
        CancellationToken cancellationToken)
    {
        var decompiler = new CSharpDecompiler(assemblyPath, new DecompilerSettings())
        {
            CancellationToken = cancellationToken
        };

        return decompiler.DecompileTypeAsString(new DecompilerFullTypeName(reflectionTypeName));
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

    private static async Task<ISymbol?> FindMatchingSourceSymbolAsync(
        Document document,
        ISymbol originalSymbol,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root is null || semanticModel is null)
            return null;

        foreach (var candidate in EnumerateDeclaredSymbols(root, semanticModel, cancellationToken))
        {
            if (SymbolsMatch(candidate, originalSymbol))
                return candidate;
        }

        return null;
    }

    private static async Task<IReadOnlyList<Location>> FindMatchingLocationsBySyntaxAsync(
        Document document,
        ISymbol originalSymbol,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
            return [];

        return originalSymbol switch
        {
            IMethodSymbol method => FindMethodLocations(root, method),
            IPropertySymbol property => FindPropertyLocations(root, property),
            IFieldSymbol field => FindFieldLocations(root, field),
            IEventSymbol @event => FindEventLocations(root, @event),
            INamedTypeSymbol type => FindTypeLocations(root, type),
            _ => []
        };
    }

    private static IEnumerable<ISymbol> EnumerateDeclaredSymbols(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ISymbol? symbol = node switch
            {
                MemberDeclarationSyntax member => semanticModel.GetDeclaredSymbol(member, cancellationToken),
                VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax =>
                    semanticModel.GetDeclaredSymbol(variable, cancellationToken),
                _ => null
            };

            if (symbol is not null)
                yield return symbol;
        }
    }

    private static IReadOnlyList<Location> FindMethodLocations(SyntaxNode root, IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.Constructor)
        {
            return root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .Where(candidate => ParametersLookCompatible(candidate.ParameterList.Parameters, method.Parameters))
                .Select(candidate => candidate.Identifier.GetLocation())
                .ToList();
        }

        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, method.Name, StringComparison.Ordinal) &&
                ParametersLookCompatible(candidate.ParameterList.Parameters, method.Parameters))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindPropertyLocations(SyntaxNode root, IPropertySymbol property)
    {
        var locations = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, property.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(candidate.Type, property.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (locations.Count > 0)
            return locations;

        return root.DescendantNodes()
            .OfType<IndexerDeclarationSyntax>()
            .Where(candidate => ParametersLookCompatible(candidate.ParameterList.Parameters, property.Parameters))
            .Select(candidate => candidate.ThisKeyword.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindFieldLocations(SyntaxNode root, IFieldSymbol field) =>
        root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate =>
                candidate.Parent?.Parent is FieldDeclarationSyntax declaration &&
                string.Equals(candidate.Identifier.ValueText, field.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(declaration.Declaration.Type, field.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

    private static IReadOnlyList<Location> FindEventLocations(SyntaxNode root, IEventSymbol @event)
    {
        var eventLocations = root.DescendantNodes()
            .OfType<EventDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, @event.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(candidate.Type, @event.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (eventLocations.Count > 0)
            return eventLocations;

        return root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate =>
                candidate.Parent?.Parent is EventFieldDeclarationSyntax declaration &&
                string.Equals(candidate.Identifier.ValueText, @event.Name, StringComparison.Ordinal) &&
                TypesLookCompatible(declaration.Declaration.Type, @event.Type))
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static IReadOnlyList<Location> FindTypeLocations(SyntaxNode root, INamedTypeSymbol type)
    {
        var locations = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, type.Name, StringComparison.Ordinal) &&
                GetTypeParameterCount(candidate) == type.Arity)
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();

        if (locations.Count > 0)
            return locations;

        return root.DescendantNodes()
            .OfType<DelegateDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, type.Name, StringComparison.Ordinal) &&
                candidate.TypeParameterList?.Parameters.Count == type.Arity)
            .Select(candidate => candidate.Identifier.GetLocation())
            .ToList();
    }

    private static bool SymbolsMatch(ISymbol candidate, ISymbol original)
    {
        if (candidate.Kind != original.Kind)
            return false;

        if (!string.Equals(
            GetContainingTypeIdentity(candidate),
            GetContainingTypeIdentity(original),
            StringComparison.Ordinal))
        {
            return false;
        }

        return (candidate, original) switch
        {
            (INamedTypeSymbol candidateType, INamedTypeSymbol originalType) =>
                string.Equals(
                    GetReflectionTypeName(candidateType),
                    GetReflectionTypeName(originalType),
                    StringComparison.Ordinal),
            (IMethodSymbol candidateMethod, IMethodSymbol originalMethod) =>
                MethodsMatch(candidateMethod, originalMethod),
            (IPropertySymbol candidateProperty, IPropertySymbol originalProperty) =>
                MembersMatch(candidateProperty, originalProperty) &&
                ParametersMatch(candidateProperty.Parameters, originalProperty.Parameters) &&
                TypesMatch(candidateProperty.Type, originalProperty.Type),
            (IFieldSymbol candidateField, IFieldSymbol originalField) =>
                MembersMatch(candidateField, originalField) &&
                TypesMatch(candidateField.Type, originalField.Type),
            (IEventSymbol candidateEvent, IEventSymbol originalEvent) =>
                MembersMatch(candidateEvent, originalEvent) &&
                TypesMatch(candidateEvent.Type, originalEvent.Type),
            _ => false
        };
    }

    private static bool MethodsMatch(IMethodSymbol candidate, IMethodSymbol original)
    {
        if (!MembersMatch(candidate, original) ||
            candidate.MethodKind != original.MethodKind ||
            candidate.Arity != original.Arity ||
            !ParametersMatch(candidate.Parameters, original.Parameters))
        {
            return false;
        }

        return candidate.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
            ? true
            : TypesMatch(candidate.ReturnType, original.ReturnType);
    }

    private static bool MembersMatch(ISymbol candidate, ISymbol original) =>
        string.Equals(candidate.MetadataName, original.MetadataName, StringComparison.Ordinal);

    private static bool ParametersMatch(
        ImmutableArray<IParameterSymbol> candidateParameters,
        ImmutableArray<IParameterSymbol> originalParameters)
    {
        if (candidateParameters.Length != originalParameters.Length)
            return false;

        for (int i = 0; i < candidateParameters.Length; i++)
        {
            if (candidateParameters[i].RefKind != originalParameters[i].RefKind ||
                !TypesMatch(candidateParameters[i].Type, originalParameters[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParametersLookCompatible(
        SeparatedSyntaxList<ParameterSyntax> candidateParameters,
        ImmutableArray<IParameterSymbol> originalParameters)
    {
        if (candidateParameters.Count != originalParameters.Length)
            return false;

        for (int i = 0; i < candidateParameters.Count; i++)
        {
            var candidateParameter = candidateParameters[i];
            var originalParameter = originalParameters[i];

            if (!ModifiersLookCompatible(candidateParameter.Modifiers, originalParameter.RefKind))
                return false;

            if (!TypesLookCompatible(candidateParameter.Type, originalParameter.Type))
                return false;
        }

        return true;
    }

    private static bool TypesMatch(ITypeSymbol candidate, ITypeSymbol original)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, original))
            return true;

        return string.Equals(
            candidate.ToDisplayString(s_typeMatchDisplayFormat),
            original.ToDisplayString(s_typeMatchDisplayFormat),
            StringComparison.Ordinal);
    }

    private static bool TypesLookCompatible(TypeSyntax? candidateType, ITypeSymbol originalType)
    {
        if (candidateType is null)
            return false;

        string candidateText = NormalizeTypeText(candidateType.ToString());
        var expectedTexts = GetExpectedTypeTexts(originalType);
        return expectedTexts.Contains(candidateText);
    }

    private static HashSet<string> GetExpectedTypeTexts(ITypeSymbol type)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal)
        {
            NormalizeTypeText(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
            NormalizeTypeText(type.ToDisplayString(s_typeMatchDisplayFormat))
        };

        if (type is INamedTypeSymbol namedType)
        {
            texts.Add(NormalizeTypeText(namedType.Name));

            if (!namedType.ContainingNamespace.IsGlobalNamespace)
                texts.Add(NormalizeTypeText($"{namedType.ContainingNamespace.ToDisplayString()}.{namedType.Name}"));
        }

        return texts;
    }

    private static string NormalizeTypeText(string text) =>
        text.Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace("?", string.Empty, StringComparison.Ordinal)
            .Replace("scoped", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool ModifiersLookCompatible(SyntaxTokenList modifiers, RefKind refKind)
    {
        bool hasRef = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword));
        bool hasOut = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OutKeyword));
        bool hasIn = modifiers.Any(modifier => modifier.IsKind(SyntaxKind.InKeyword));

        return refKind switch
        {
            RefKind.None => !hasRef && !hasOut && !hasIn,
            RefKind.Ref => hasRef,
            RefKind.Out => hasOut,
            RefKind.In => hasIn,
            _ => true
        };
    }

    private static int GetTypeParameterCount(BaseTypeDeclarationSyntax declaration) => declaration switch
    {
        TypeDeclarationSyntax typeDeclaration => typeDeclaration.TypeParameterList?.Parameters.Count ?? 0,
        _ => 0
    };

    private static string GetContainingTypeIdentity(ISymbol symbol) =>
        symbol.ContainingType is null ? string.Empty : GetReflectionTypeName(symbol.ContainingType);

    private static INamedTypeSymbol? GetOwningType(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type,
        _ when symbol.ContainingType is not null => symbol.ContainingType,
        _ => null
    };

    private static string GetReflectionTypeName(INamedTypeSymbol type)
    {
        var containingTypes = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            containingTypes.Push(current.MetadataName);

        string typeName = string.Join("+", containingTypes);
        return type.ContainingNamespace.IsGlobalNamespace
            ? typeName
            : $"{type.ContainingNamespace.ToDisplayString()}.{typeName}";
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

    private static IEnumerable<MetadataReference> CreateMetadataReferences(string assemblyPath)
    {
        var references = new List<MetadataReference>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(string path)
        {
            if (!File.Exists(path))
                return;

            string normalized = Path.GetFullPath(path);
            if (!seenPaths.Add(normalized))
                return;

            try
            {
                references.Add(MetadataReference.CreateFromFile(normalized));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DecompiledSourceService] Failed to add metadata reference '{normalized}': {ex.Message}");
            }
        }

        AddReference(assemblyPath);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (string path in trustedPlatformAssemblies.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddReference(path);
            }
        }

        string? directory = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
                AddReference(path);

            foreach (string path in Directory.EnumerateFiles(directory, "*.exe"))
                AddReference(path);
        }

        return references;
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

    private static void WriteFileIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
                return;
        }

        File.WriteAllText(path, content, s_utf8NoBom);
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
