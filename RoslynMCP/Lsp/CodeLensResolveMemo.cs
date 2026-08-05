using System.Collections.Concurrent;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp;

/// <summary>
/// Keeps resolved code lenses for as long as the state they were computed from is unchanged, for
/// any language pack that can say what that state is.
/// </summary>
/// <remarks>
/// <para>
/// A code lens is not resolved once. The client re-requests the list on every edit and re-resolves
/// whatever scrolls into view, so the cost of a lens is paid continuously rather than at open. That
/// is fine when a lens is a syntactic count and expensive when it is a semantic one: measured warm
/// on the proto pack's fixture, sixteen lenses on one file cost 851 ms of <c>SymbolFinder</c> sweeps
/// and 214 ms of symbol-set building — and every one of those milliseconds was spent again the next
/// time the gutter came back.
/// </para>
/// <para>
/// The shape is the one an incremental generator uses: name the inputs, cache the output against
/// them, and let a changed input invalidate exactly what depended on it. What differs per pack is
/// only what the inputs <em>are</em>, which is why that is the one thing a pack supplies — see
/// <see cref="ILanguageCodeLensGeneration"/>. Everything else is the same for all of them, because
/// <see cref="CodeLensData"/> already identifies a lens the same way for all of them.
/// </para>
/// <para>
/// A pack that does not implement the interface is passed straight through, so this is opt-in and
/// adding a pack costs nothing until it wants the memo. A pack that opts in and returns
/// <see langword="null"/> — no view for that file yet, nothing built — is also passed through, so
/// there is no state in which a lens goes unanswered because the memo could not describe it.
/// </para>
/// <para>
/// Deliberately not a batch that resolves a whole file at once on first touch. That was tried on
/// the proto pack and measured: one declaration is often five or six symbols, so seventeen lenses
/// together is on the order of eighty concurrent solution-wide searches contending for the document
/// indexes they are all trying to build, and it came out consistently slower than leaving them
/// serial. Memoising adds nothing to the first pass and so cannot regress it.
/// </para>
/// </remarks>
internal static class CodeLensResolveMemo
{
    /// <summary>
    /// How many files to keep answers for. An entry can hold a reference to a whole solution
    /// snapshot through its generation, which is what makes this a cap rather than a free-for-all.
    /// </summary>
    private const int MaxFiles = 8;

    /// <summary>A resolvable lens, identified the way the client asks for it.</summary>
    private readonly record struct Slot(int Line, int Character, string Kind, string? PackId);

    private sealed record Entry(object Generation, ConcurrentDictionary<Slot, Lazy<Task<Command?>>> Answers);

    private static readonly ConcurrentDictionary<string, Entry> s_byUri =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <paramref name="lens"/> through <paramref name="provider"/>, reusing an answer
    /// computed earlier for the same lens against the same state.
    /// </summary>
    public static async Task<CodeLens> ResolveAsync(
        ILanguageCodeLensProvider provider, CodeLens lens, CancellationToken ct)
    {
        if (lens.Data is not { } data
            || provider is not ILanguageCodeLensGeneration source
            || await source.LensGenerationAsync(data.Uri, ct) is not { } generation)
        {
            return await provider.ResolveCodeLensAsync(lens, ct);
        }

        var entry = s_byUri.AddOrUpdate(
            data.Uri,
            _ => new Entry(generation, new()),
            (_, existing) => existing.Generation.Equals(generation)
                ? existing
                : new Entry(generation, new()));

        // Bounded rather than evicted precisely: an entry is per file, and dropping one costs a
        // recomputation and not a wrong answer.
        if (s_byUri.Count > MaxFiles)
            s_byUri.Clear();

        // Lazy rather than a bare task factory: ConcurrentDictionary does not hold its lock across
        // GetOrAdd's factory, so two resolves racing on one lens would otherwise both start the
        // search and one full result would be computed only to be thrown away.
        var answer = entry.Answers.GetOrAdd(
            new Slot(data.Line, data.Character, data.Kind, data.PackId),
            _ => new Lazy<Task<Command?>>(
                async () => (await provider.ResolveCodeLensAsync(lens, CancellationToken.None)).Command,
                LazyThreadSafetyMode.ExecutionAndPublication));

        // Not cancelled by this request: another resolve may be waiting on the same entry, and one
        // client abandoning a scroll must not make the others start over.
        //
        // The command is what is kept rather than the whole lens. Resolving is defined as filling
        // the command in, and the range belongs to the lens the client sent — which is the one it
        // will render against, and which it is entitled to have adjusted.
        return lens with { Command = await answer.Value.WaitAsync(ct) };
    }

    /// <summary>Drops every kept answer. For tests that need a cold measurement.</summary>
    internal static void Clear() => s_byUri.Clear();
}
