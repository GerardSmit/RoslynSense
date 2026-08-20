using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMCP.Config;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Lsp.Search;
using RoslynMCP.Services;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// What the settings page needs from the solution: the values a setting can take here, and which
/// members a configured call shape actually selects.
/// </summary>
/// <remarks>
/// A settings form built from a JSON Schema can draw every control except the two that matter.
/// The schema knows a <c>containingType</c> is a string; it does not know the strings that would
/// resolve. It knows <c>fallbacks</c> is a list of strings; it does not know which ids exist in
/// this solution. Both answers live in the server, and both are the difference between a form
/// somebody can fill in and a form somebody has to already know the answer to.
/// </remarks>
internal static class SettingsAssistHandler
{
    /// <summary>Enough to choose from; more than a dropdown should ever show.</summary>
    private const int MaxSuggestions = 30;

    // ---- Choices ---------------------------------------------------------------------------------

    /// <summary>
    /// The values one setting can currently take. An unknown path answers with nothing rather than
    /// an error: the page asks about every setting it draws, and a setting with no dynamic choices
    /// is the common case.
    /// </summary>
    public static SettingChoicesResult Choices(SettingChoicesParams p) =>
        new(p.Path switch
        {
            "resources.lookups[].fallbacks" => ConventionIds(p.Config),
            _ => [],
        });

    /// <summary>
    /// The root conventions in effect: the preset's, plus whatever the file adds, merged by the
    /// same code the running pack uses.
    /// </summary>
    /// <remarks>
    /// Read from the config the page sent rather than from the server's own settings. The page is
    /// editing files the server has not necessarily reloaded, and a convention someone just typed
    /// has to be offerable as a fallback before it is saved — otherwise the two fields have to be
    /// filled in across two visits to the page.
    /// </remarks>
    private static SettingChoice[] ConventionIds(JsonElement? config)
    {
        var settings = ResourceSettings.Resolve(
            enabled: true, ResourcesOf(config), warnings: []);

        return [.. settings.Conventions.Select(
            convention => new SettingChoice(convention.Id, Describe(convention)))];
    }

    /// <summary>Where the convention looks, in the words the field itself uses.</summary>
    private static string Describe(ResourceRootConvention convention)
    {
        string where = convention.SiblingFolder is { Length: > 0 } sibling
            ? $"{sibling} beside the file"
            : convention.RootFolder is { Length: > 0 } root
                ? $"{root} at the project root"
                : "the file's own folder";

        return convention.FixedName is { Length: > 0 } name ? $"{where}, {name}" : where;
    }

    private static ResourcesConfig? ResourcesOf(JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return resources.Deserialize<ResourcesConfig>(
                RoslynSenseConfigLoader.SerializerOptions);
        }
        catch (JsonException)
        {
            // A half-typed file is the normal state of one being edited; the preset's own
            // conventions are still worth offering.
            return null;
        }
    }

    // ---- Member shape ----------------------------------------------------------------------------

    /// <summary>
    /// Which members a type/name/signature triple selects, and what to suggest for the parts of it
    /// that are still blank.
    /// </summary>
    public static Task<MemberShapeResult> MemberShapeAsync(
        MemberShapeParams p, CancellationToken ct) =>
        WorkspaceService.TryGetMostRecentSolution() is { } solution
            ? MemberShapeAsync(solution, p, ct)
            : Task.FromResult(
                new MemberShapeResult([], [], [], Problem: "No solution is loaded yet."));

    /// <summary>The same, against a solution the caller already has.</summary>
    public static async Task<MemberShapeResult> MemberShapeAsync(
        Solution solution, MemberShapeParams p, CancellationToken ct)
    {
        string type = (p.ContainingType ?? "").Trim();
        string member = (p.MemberName ?? "").Trim();

        // A shape with no type is the deliberate escape hatch — it matches any type declaring a
        // member of that name — so it is a state to be helped through, not an error.
        if (type.Length == 0)
        {
            return new MemberShapeResult(
                [], [], [],
                Problem: member.Length == 0
                    ? "Name a type, or leave it empty to match any type declaring this member."
                    : "Matching any type that declares this member.");
        }

        var resolved = await ResolveAsync(solution, type, ct);

        if (resolved is null)
        {
            // Said even when there are near names to offer. The suggestions are the remedy, not a
            // reason to keep quiet about the miss: an entry naming a type that is not there is
            // exactly the silent failure this whole answer exists to break.
            return new MemberShapeResult(
                await TypeSuggestionsAsync(solution, type, ct), [], [],
                Problem: $"No type named '{type}' in this solution or its references.");
        }

        var declared = Members(resolved).ToList();
        string[] names = [.. declared.Select(m => Spelling(m)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        if (member.Length == 0)
        {
            return new MemberShapeResult(
                [], [.. names.Take(MaxSuggestions)], [],
                ResolvedType: resolved.ToDisplayString(MemberSignature.DeclarationName));
        }

        var expected = p.ParameterTypes is { Length: > 0 }
            ? ImmutableArray.Create(p.ParameterTypes)
            : (ImmutableArray<string>?)null;

        var overloads = declared
            .Where(m => Spelling(m).Equals(member, StringComparison.Ordinal))
            .ToList();

        var matches = overloads
            .Select(m => new MemberShapeMatch(
                (m.ContainingType?.ToDisplayString(MemberSignature.DeclarationName)) ?? "",
                Spelling(m),
                Signature(m),
                [.. MemberSignature.Parameters(m).Select(
                    parameter => new MemberShapeParameter(
                        parameter.Name, parameter.Type.ToDisplayString(MemberSignature.TypeName)))],
                expected is not { } wanted || MemberSignature.Matches(m, wanted)))
            .OrderByDescending(m => m.Matched)
            .ThenBy(m => m.Parameters.Length)
            .Take(p.MaxResults is > 0 and <= 50 ? p.MaxResults : 20)
            .ToArray();

        return new MemberShapeResult(
            [],
            [.. names.Take(MaxSuggestions)],
            matches,
            ResolvedType: resolved.ToDisplayString(MemberSignature.DeclarationName),
            Problem: matches.Length == 0
                ? $"'{resolved.Name}' declares no member named '{member}'."
                : null);
    }

    /// <summary>The name a configured shape writes: an indexer is <c>Item</c>, as the CLR has it.</summary>
    private static string Spelling(ISymbol member) =>
        member is IPropertySymbol { IsIndexer: true } ? "Item" : member.Name;

    private static string Signature(ISymbol member)
    {
        var parameters = MemberSignature.Parameters(member).Select(
            parameter => $"{parameter.Type.ToDisplayString(s_shortType)} {parameter.Name}");

        return member is IPropertySymbol { IsIndexer: true }
            ? $"this[{string.Join(", ", parameters)}]"
            : $"{member.Name}({string.Join(", ", parameters)})";
    }

    /// <summary>Unqualified, because the row is already about one type and the column is narrow.</summary>
    private static readonly SymbolDisplayFormat s_shortType = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Everything a shape could name on this type: its own members, its bases', and its
    /// interfaces'.
    /// </summary>
    /// <remarks>
    /// The whole chain because that is what binding does — a configured
    /// <c>PortalModuleBase.LocalizeText</c> is reached from every module that derives from it, and
    /// someone who typed the derived module's name should still see the member they meant.
    /// </remarks>
    private static IEnumerable<ISymbol> Members(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in Chain(type))
        {
            foreach (var member in scope.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method
                    && !method.IsImplicitlyDeclared)
                {
                    if (seen.Add(method.Name + "/" + Signature(method)))
                        yield return method;
                }
                else if (member is IPropertySymbol { IsIndexer: true } indexer
                    && seen.Add("Item/" + Signature(indexer)))
                {
                    yield return indexer;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Chain(INamedTypeSymbol type)
    {
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
            yield return candidate;

        foreach (var contract in type.AllInterfaces)
            yield return contract;
    }

    // ---- Finding the type ------------------------------------------------------------------------

    /// <summary>
    /// The type a fully-qualified name means, from the first compilation that has one.
    /// </summary>
    /// <remarks>
    /// Metadata names, not C# ones: a nested type is <c>Outer+Inner</c> and a generic one carries
    /// its arity. Someone writing configuration types the C# spelling, so the dots are tried as
    /// nesting from the right and the arities are tried in turn — which costs a handful of failed
    /// dictionary lookups and saves anyone from having to know either convention.
    /// </remarks>
    private static async Task<INamedTypeSymbol?> ResolveAsync(
        Solution solution, string name, CancellationToken ct)
    {
        var candidates = MetadataNames(name).ToList();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            if (project.Language != LanguageNames.CSharp
                || await project.GetCompilationAsync(ct) is not { } compilation)
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                if (compilation.GetTypeByMetadataName(candidate) is { } type)
                    return type;
            }
        }

        return null;
    }

    private static IEnumerable<string> MetadataNames(string name)
    {
        for (string current = name; ; )
        {
            yield return current;

            for (int arity = 1; arity <= 4; arity++)
                yield return current + "`" + arity;

            int dot = current.LastIndexOf('.');
            if (dot < 0)
                yield break;

            current = current[..dot] + "+" + current[(dot + 1)..];
        }
    }

    /// <summary>
    /// Types whose name the fragment could have been the start of — the solution's own first, then
    /// the references.
    /// </summary>
    /// <remarks>
    /// Matched on the last dotted segment, because that is the part someone types from memory. The
    /// namespace in front is what they are trying to be told, not what they are searching by.
    /// </remarks>
    private static async Task<string[]> TypeSuggestionsAsync(
        Solution solution, string fragment, CancellationToken ct)
    {
        int dot = fragment.LastIndexOf('.');
        string tail = dot < 0 ? fragment : fragment[(dot + 1)..];

        if (tail.Length < 2)
            return [];

        // What the fragment is a piece of, first: half-written is the ordinary case, and a name
        // containing what was typed is a better answer than one that merely resembles it.
        var found = await SearchTypesAsync(
            solution, name => name.Contains(tail, StringComparison.OrdinalIgnoreCase), ct);

        // Then what it is one keystroke away from. A misspelling is not a substring of the name it
        // meant, so without this pass the only suggestion worth making — the name actually
        // intended — is the one never offered, and the entry stays wrong with no hint why.
        if (found.Length == 0)
            found = await SearchTypesAsync(solution, name => NearMiss(name, tail), ct);

        return found;
    }

    private static async Task<string[]> SearchTypesAsync(
        Solution solution, Func<string, bool> accept, CancellationToken ct)
    {
        var found = new List<string>();

        foreach (var symbol in await SymbolFinder.FindSourceDeclarationsAsync(
            solution, accept, SymbolFilter.Type, ct))
        {
            found.Add(symbol.ToDisplayString(MemberSignature.DeclarationName));

            if (found.Count >= MaxSuggestions)
                break;
        }

        if (found.Count < MaxSuggestions)
        {
            foreach (var (_, types) in MetadataTypeIndex.ForSolution(solution, ct))
            {
                foreach (var type in types)
                {
                    if (!accept(type.Name))
                        continue;

                    found.Add(CSharpSpelling(type.ReflectionName));

                    if (found.Count >= MaxSuggestions)
                        break;
                }

                if (found.Count >= MaxSuggestions)
                    break;
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// One typo away: a mistyped, missing, extra or swapped character.
    /// </summary>
    /// <remarks>
    /// Bounded at one edit and at four characters typed, because two edits from a short name is
    /// simply a different name, and a list of those is noise rather than a suggestion. The first
    /// character and the length have to line up before any comparison happens — this runs against
    /// every type the references declare, and those two tests reject nearly all of them.
    /// </remarks>
    private static bool NearMiss(string name, string typed) =>
        typed.Length >= 4
        && Math.Abs(name.Length - typed.Length) <= 1
        && Same(name[0], typed[0])
        && OneEditApart(name, typed);

    private static bool OneEditApart(string candidate, string typed)
    {
        int i = 0, j = 0;
        bool spent = false;

        while (i < candidate.Length && j < typed.Length)
        {
            if (Same(candidate[i], typed[j]))
            {
                i++;
                j++;
                continue;
            }

            if (spent)
                return false;

            spent = true;

            if (candidate.Length > typed.Length)
            {
                i++;                                    // a character of the name went untyped
            }
            else if (candidate.Length < typed.Length)
            {
                j++;                                    // one character too many was typed
            }
            else if (i + 1 < candidate.Length
                     && Same(candidate[i], typed[j + 1])
                     && Same(candidate[i + 1], typed[j]))
            {
                i += 2;                                 // two neighbours came out swapped
                j += 2;
            }
            else
            {
                i++;                                    // one character stood in for another
                j++;
            }
        }

        // An unspent edit covers whatever is left over, which the length test caps at one.
        return !spent || (i == candidate.Length && j == typed.Length);
    }

    private static bool Same(char a, char b) =>
        char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    /// <summary>A reflection name as C# writes it: nesting with a dot, arity dropped.</summary>
    private static string CSharpSpelling(string reflectionName)
    {
        string name = reflectionName.Replace('+', '.');
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }
}
