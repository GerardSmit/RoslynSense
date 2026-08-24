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

    /// <summary>
    /// How much of a method name has to be typed before the solution is searched for it.
    /// </summary>
    /// <remarks>
    /// The search walks every declaration in the solution, and this runs on a keystroke rather
    /// than on a keypress the way Ctrl+T does. One or two characters name nothing anyone was
    /// looking for and would find several thousand things.
    /// </remarks>
    private const int MinimumSearch = 3;

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
            "valueSets.bindings[].set" => ValueSetIds(p.Config),
            "valueSets.properties[].set" => ValueSetIds(p.Config),
            "valueSets.sets[].connection" => ConnectionAliases(p.Config),
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

    /// <summary>
    /// The value sets declared in the file being edited.
    /// </summary>
    /// <remarks>
    /// Read from the raw JSON rather than through <see cref="ValueSetsConfig"/> for the same reason
    /// the conventions are: a set someone just typed has to be bindable before the file is saved,
    /// and half-typed JSON does not deserialize. The one field needed is the id.
    /// </remarks>
    private static SettingChoice[] ValueSetIds(JsonElement? config)
    {
        if (Section(config, "valueSets") is not { } valueSets
            || !valueSets.TryGetProperty("sets", out var sets)
            || sets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<SettingChoice>();

        foreach (var set in sets.EnumerateArray())
        {
            if (Text(set, "id") is { Length: > 0 } id)
                found.Add(new SettingChoice(id, DescribeSet(set)));
        }

        return [.. found];
    }

    /// <summary>Where the set's values come from, short enough to sit beside its name.</summary>
    private static string DescribeSet(JsonElement set)
    {
        if (Text(set, "query") is { Length: > 0 } query)
        {
            string connection = Text(set, "connection") is { Length: > 0 } alias ? $"{alias}: " : "";
            return connection + query;
        }

        return set.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? $"{values.GetArrayLength()} values listed here"
            : "no query and no values";
    }

    /// <summary>
    /// The database connections the file configures, so a set can be pointed at one by name.
    /// </summary>
    /// <remarks>
    /// The file's rather than the server's registered ones, which are a superset: a connection
    /// added over the wire for one chat is not something a checked-in value set should be resolved
    /// against, and would not resolve on anyone else's machine.
    /// </remarks>
    private static SettingChoice[] ConnectionAliases(JsonElement? config)
    {
        if (Section(config, "database") is not { } database
            || !database.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var found = new List<SettingChoice>();

        foreach (var connection in connections.EnumerateObject())
        {
            if (connection.Name.Length > 0)
                found.Add(new SettingChoice(connection.Name, Text(connection.Value, "provider")));
        }

        return [.. found];
    }

    private static JsonElement? Section(JsonElement? config, string name) =>
        config is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(name, out var section)
        && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

        int max = p.MaxResults is > 0 and <= 50 ? p.MaxResults : 20;
        var kinds = KindsOf(p.Kinds);

        // A shape with no type is the deliberate escape hatch — it matches any type declaring a
        // member of that name — so it is a state to be helped through, not an error.
        if (type.Length == 0)
        {
            if (member.Length == 0)
            {
                return new MemberShapeResult(
                    [], [], [],
                    Problem: $"Name a class and a {Noun(kinds)}, or a {Noun(kinds)} on its own to "
                        + "match it on any class.");
            }

            var anywhere = await SearchAsync(solution, member, max, kinds, ct);

            return new MemberShapeResult(
                [], [], anywhere,
                Problem: anywhere.Length == 0
                    ? "Matching any type that declares this member."
                    : $"Matching any class that declares this {Noun(kinds)}. Choose one below to "
                        + "name its class.");
        }

        var resolved = await ResolveAsync(solution, type, ct);

        if (resolved is null)
        {
            // The whole line handed to the search box's own grammar: `DotNetNuke.GetString` means
            // a method called GetString somewhere under DotNetNuke, and somebody writing a lookup
            // knows the method they saw at a call site and not the namespace it was declared in.
            var anywhere = await SearchAsync(solution, $"{type}.{member}", max, kinds, ct);

            // The miss is said even when there are near names to offer. The suggestions are the
            // remedy, not a reason to keep quiet about it: an entry naming a type that is not
            // there is exactly the silent failure this whole answer exists to break.
            return new MemberShapeResult(
                await TypeSuggestionsAsync(solution, type, ct), [], anywhere,
                Problem: anywhere.Length > 0
                    ? $"No class named '{type}'. Choose a {Noun(kinds)} below to name its class."
                    : $"No type named '{type}' in this solution or its references.");
        }

        var declared = Members(resolved, kinds).ToList();
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
                [.. MemberSignature.CallParameters(m).Select(
                    parameter => new MemberShapeParameter(
                        parameter.Name, parameter.Type.ToDisplayString(MemberSignature.TypeName)))],
                expected is not { } wanted || MemberSignature.Matches(m, wanted),
                KindName(m)))
            .OrderByDescending(m => m.Matched)
            .ThenBy(m => m.Parameters.Length)
            .Take(max)
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

    /// <summary>
    /// Members of that name anywhere in the solution, as the shapes that would name them.
    /// </summary>
    /// <remarks>
    /// The answer to "I know the member and not the namespace it lives in", which is the ordinary
    /// state of someone writing one of these: they are looking at a call site, where the namespace
    /// is a <c>using</c> at the top of some other file. Ranked by
    /// <see cref="SearchEverywhere.FindMembersAsync"/> so the query grammar is the one Ctrl+T
    /// already taught them — a leading word narrows by container.
    /// </remarks>
    private static async Task<MemberShapeMatch[]> SearchAsync(
        Solution solution, string query, int max, MemberKinds kinds, CancellationToken ct)
    {
        if (Named(query).Length < MinimumSearch)
            return [];

        var found = await SearchEverywhere.FindMembersAsync(
            solution, query, max, member => Wanted(member, kinds), ct);

        return [.. found.Select(member => new MemberShapeMatch(
            member.ContainingType?.ToDisplayString(MemberSignature.DeclarationName) ?? "",
            Spelling(member),
            Signature(member),
            [.. MemberSignature.CallParameters(member).Select(
                parameter => new MemberShapeParameter(
                    parameter.Name, parameter.Type.ToDisplayString(MemberSignature.TypeName)))],
            // Every one of them is a member that could be named here; which overload a signature
            // then selects is the question asked once a class is settled on.
            Matched: true,
            KindName(member)))];
    }

    /// <summary>The part of a query that names the thing rather than where it lives.</summary>
    private static string Named(string query)
    {
        int dot = query.LastIndexOf('.');
        return dot < 0 ? query : query[(dot + 1)..];
    }

    /// <summary>The name a configured shape writes: an indexer is <c>Item</c>, as the CLR has it.</summary>
    private static string Spelling(ISymbol member) =>
        member is IPropertySymbol { IsIndexer: true } ? "Item" : member.Name;

    /// <summary>
    /// The member as a call site writes it, which is the form a configured signature is written
    /// against — so an extension method loses the receiver it is invoked on.
    /// </summary>
    /// <remarks>
    /// A property or a field is not called at all, so it is written the way it is declared instead:
    /// the type in front of the name, and for a property which of the two accessors it has. That
    /// last part is the one thing about a property this page can say that the name does not — a
    /// get-only property is a member a literal is compared against and never assigned to.
    /// </remarks>
    private static string Signature(ISymbol member)
    {
        if (member is IFieldSymbol field)
            return $"{field.Type.ToDisplayString(s_shortType)} {field.Name}";

        if (member is IPropertySymbol { IsIndexer: false } property)
        {
            string accessors = property switch
            {
                { GetMethod: not null, SetMethod: not null } => "get; set;",
                { GetMethod: not null } => "get;",
                _ => "set;",
            };

            return $"{property.Type.ToDisplayString(s_shortType)} {property.Name} {{ {accessors} }}";
        }

        var parameters = MemberSignature.CallParameters(member).Select(
            parameter => $"{parameter.Type.ToDisplayString(s_shortType)} {parameter.Name}");

        return member is IPropertySymbol { IsIndexer: true }
            ? $"this[{string.Join(", ", parameters)}]"
            : $"{member.Name}({string.Join(", ", parameters)})";
    }

    /// <summary>Which kind of member a row is about, in the words the request asks for them by.</summary>
    private static string KindName(ISymbol member) => member switch
    {
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        _ => "method",
    };

    /// <summary>
    /// Which kinds of member an answer may contain.
    /// </summary>
    /// <remarks>
    /// A setting naming a call shape wants methods and indexers; a setting naming a member that
    /// holds a value wants properties and fields. Asked for by the request rather than decided here,
    /// because the schema already says which a given setting is — and a page offering a property
    /// where only a call will bind is a page walking someone into an entry that does nothing.
    /// </remarks>
    [Flags]
    private enum MemberKinds
    {
        Method = 1,
        Indexer = 2,
        Property = 4,
        Field = 8,

        /// <summary>What every caller wanted before any of them wanted anything else.</summary>
        Called = Method | Indexer,
    }

    /// <summary>
    /// The requested kinds, defaulting to the callable ones. An unrecognised name is ignored rather
    /// than refused: it can only come from a newer page than this server, and answering with the
    /// kinds it did recognise is more useful than answering with nothing.
    /// </summary>
    private static MemberKinds KindsOf(string[]? requested)
    {
        if (requested is not { Length: > 0 })
            return MemberKinds.Called;

        MemberKinds kinds = 0;

        foreach (string name in requested)
        {
            kinds |= name.Trim().ToLowerInvariant() switch
            {
                "method" => MemberKinds.Method,
                "indexer" => MemberKinds.Indexer,
                "property" => MemberKinds.Property,
                "field" => MemberKinds.Field,
                _ => 0,
            };
        }

        return kinds == 0 ? MemberKinds.Called : kinds;
    }

    /// <summary>What to call the thing being named, so the sentences fit what is being looked for.</summary>
    private static string Noun(MemberKinds kinds) =>
        kinds == MemberKinds.Called ? "method"
            : (kinds & MemberKinds.Called) == 0 ? "property"
            : "member";

    /// <summary>
    /// Whether a member is one of the kinds asked for and something configuration could name at
    /// all.
    /// </summary>
    /// <remarks>
    /// A backing field is skipped by the same rule that skips an accessor: it is the compiler's
    /// spelling of a member already listed, and offering both would have someone choose the one
    /// nothing can be written against. Accessibility is deliberately not filtered — it never was
    /// for methods, and a configuration file is written by someone who can see the source.
    /// </remarks>
    private static bool Wanted(ISymbol member, MemberKinds kinds) =>
        !member.IsImplicitlyDeclared
        && member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => kinds.HasFlag(MemberKinds.Method),
            IPropertySymbol { IsIndexer: true } => kinds.HasFlag(MemberKinds.Indexer),
            IPropertySymbol => kinds.HasFlag(MemberKinds.Property),
            IFieldSymbol { AssociatedSymbol: null } => kinds.HasFlag(MemberKinds.Field),
            _ => false,
        };

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
    private static IEnumerable<ISymbol> Members(INamedTypeSymbol type, MemberKinds kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in Chain(type))
        {
            foreach (var member in scope.GetMembers())
            {
                // The kind is part of the key because an override and the member it overrides are
                // two symbols spelling one thing, while a property and a method of the same name
                // are two things — and only the second pair should both be listed.
                if (Wanted(member, kinds)
                    && seen.Add(KindName(member) + "/" + Spelling(member) + "/" + Signature(member)))
                {
                    yield return member;
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
