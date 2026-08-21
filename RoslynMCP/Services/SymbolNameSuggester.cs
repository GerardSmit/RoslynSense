using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Produces "did you mean" candidates when a fully-qualified symbol name does not resolve.
/// A miss is usually a stale name (the type was renamed upstream) or a misspelling, so
/// candidates are ranked by name similarity: exact, containment, shared camel-case words,
/// and edit distance. Candidates come from every type the compilation can see — including
/// metadata references — so renamed framework types are found without manual decompiling.
/// </summary>
internal static class SymbolNameSuggester
{
    private const int MaxSuggestions = 10;
    private const int MinScore = 70;

    /// <summary>
    /// Finds type names similar to <paramref name="simpleName"/> (arity suffix allowed)
    /// across the compilation's source and all metadata references. Returns fully-qualified
    /// metadata names (backtick arity, '+' for nesting) usable directly in a retry.
    /// </summary>
    public static IReadOnlyList<string> SuggestTypes(
        Compilation compilation, string simpleName, string? namespaceHint, CancellationToken cancellationToken)
    {
        string query = StripArity(simpleName);
        if (query.Length < 2)
            return [];

        string[] queryHumps = SplitHumps(query);
        var scored = new List<(int Score, string Name)>();

        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(compilation.GlobalNamespace);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = pending.Pop();

            if (scope is INamespaceSymbol ns)
            {
                foreach (var child in ns.GetNamespaceMembers())
                    pending.Push(child);
            }

            foreach (var type in scope.GetTypeMembers())
            {
                pending.Push(type);

                int score = ScoreName(query, queryHumps, type.Name);
                if (score < MinScore)
                    continue;

                if (namespaceHint is not null &&
                    string.Equals(type.ContainingNamespace?.ToDisplayString(), namespaceHint, StringComparison.Ordinal))
                    score += 5;

                scored.Add((score, GetMetadataQualifiedName(type)));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => s.Name)
            .Distinct()
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>
    /// Finds members of <paramref name="type"/> whose name is similar to
    /// <paramref name="memberName"/>. Returns plain member names.
    /// </summary>
    public static IReadOnlyList<string> SuggestMembers(INamedTypeSymbol type, string memberName)
    {
        string[] queryHumps = SplitHumps(memberName);

        return type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m is not IMethodSymbol { AssociatedSymbol: not null })
            .Select(m => m.Name)
            .Distinct()
            .Select(name => (Score: ScoreName(memberName, queryHumps, name), Name: name))
            .Where(s => s.Score >= MinScore)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => s.Name)
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>
    /// Fully-qualified name in metadata form: backtick arity and '+' between nested types,
    /// so the result can be pasted straight back into a symbol lookup.
    /// </summary>
    public static string GetMetadataQualifiedName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            parts.Push(t.Arity > 0 ? $"{t.Name}`{t.Arity}" : t.Name);

        string typePart = string.Join('+', parts);
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? $"{ns.ToDisplayString()}.{typePart}" : typePart;
    }

    private static int ScoreName(string query, string[] queryHumps, string candidate)
    {
        if (string.Equals(query, candidate, StringComparison.Ordinal))
            return 100;
        if (string.Equals(query, candidate, StringComparison.OrdinalIgnoreCase))
            return 95;

        // One name contained in the other (e.g. a Prefix/Suffix variant of the same type).
        if (Math.Min(query.Length, candidate.Length) >= 5 &&
            (query.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
             candidate.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 78;

        double humpSimilarity = HumpOverlap(queryHumps, SplitHumps(candidate));
        double editSimilarity = EditSimilarity(query, candidate);
        double best = Math.Max(humpSimilarity, editSimilarity);

        return (int)(45 + 50 * best);
    }

    /// <summary>
    /// Fraction of camel-case words the two names share. Catches a single renamed word in
    /// a long name ('ExtensionMethodImport…' vs 'ExtensionMemberImport…') that edit
    /// distance would rate poorly relative to the name's length.
    /// </summary>
    private static double HumpOverlap(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string hump in a)
            counts[hump] = counts.GetValueOrDefault(hump) + 1;

        int shared = 0;
        foreach (string hump in b)
        {
            if (counts.TryGetValue(hump, out int left) && left > 0)
            {
                counts[hump] = left - 1;
                shared++;
            }
        }

        return (double)shared / Math.Max(a.Length, b.Length);
    }

    private static double EditSimilarity(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        // A large length difference cannot be a near-miss; skip the O(n*m) work.
        if (max < 4 || Math.Abs(a.Length - b.Length) > max * 0.4)
            return 0;

        int distance = Levenshtein(a, b);
        return 1.0 - (double)distance / max;
    }

    private static int Levenshtein(string a, string b)
    {
        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[b.Length];
    }

    private static string[] SplitHumps(string name)
    {
        var humps = new List<string>();
        int start = 0;

        for (int i = 1; i < name.Length; i++)
        {
            bool boundary = char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])
                            || char.IsLetter(name[i]) != char.IsLetter(name[i - 1]);
            if (boundary)
            {
                humps.Add(name[start..i]);
                start = i;
            }
        }

        humps.Add(name[start..]);
        return humps.ToArray();
    }

    private static string StripArity(string name)
    {
        int backtick = name.IndexOf('`');
        return backtick < 0 ? name : name[..backtick];
    }
}
