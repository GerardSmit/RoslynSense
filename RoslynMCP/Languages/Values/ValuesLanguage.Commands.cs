using System.Text.Json;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// Reloading the sets, which is the only thing about them a person ever has to ask for.
/// </summary>
/// <remarks>
/// The values are read once and kept until this runs — see <see cref="Core.ValueSetCatalog"/> for
/// why that is not a timed cache. What that trades away is freshness after a migration, and this
/// buys it back: one command, and the next diagnostics pass judges against the new rows.
/// </remarks>
internal sealed partial class ValuesLanguage : ILanguageCommandProvider
{
    /// <summary><c>[setId?]</c> → the values re-read from the database.</summary>
    public const string RefreshCommand = "roslynSense.refreshValueSets";

    public bool CanExecute(string command) => command is RefreshCommand;

    public Task<object> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct) =>
        Task.FromResult(p.Command is RefreshCommand
            ? Refresh(Text(p, 0))
            : (object)new ValueSetRefreshResult(false, $"Unknown command '{p.Command}'.", []));

    private object Refresh(string? id)
    {
        if (id is { Length: > 0 } && Settings.Set(id) is null)
            return new ValueSetRefreshResult(false, $"No value set named '{id}'.", []);

        _catalog.Refresh(id);

        // The open documents were told the old values were the whole truth, and the diagnostics
        // they are showing were computed against them. Nothing else invalidates those.
        LspSessionRegistry.ScheduleRefresh(RefreshKind.Diagnostics);

        string[] refreshed = id is { Length: > 0 }
            ? [id]
            : [.. Settings.Sets.Select(set => set.Id)];

        return new ValueSetRefreshResult(true, null, refreshed);
    }

    private static string? Text(ExecuteCommandParams p, int index) =>
        p.Arguments is { } arguments && index < arguments.Length
        && arguments[index] is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
}

/// <summary>What a refresh did, for the client that asked.</summary>
internal sealed record ValueSetRefreshResult(bool Ok, string? Problem, string[] Sets);
