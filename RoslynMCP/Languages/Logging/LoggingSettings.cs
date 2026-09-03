using RoslynMCP.Config;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// The logging pack's gate and which of its rules run.
/// </summary>
/// <remarks>
/// One switch per rule rather than one for the pack, because the rules overlap different things in
/// different solutions. <see cref="UnknownPlaceholder"/> and <see cref="UnusedValue"/> restate
/// SYSLIB1014 and SYSLIB1015 for <c>[LoggerMessage]</c> — more precisely placed, on the hole and on
/// the parameter rather than on the method, but the same claim — so a solution where the source
/// generator already reports them turns these two off and keeps the rest.
/// </remarks>
internal sealed record LoggingSettings(
    bool Enabled,
    bool UnknownPlaceholder,
    bool UnusedValue,
    bool ValueCount,
    bool ExceptionPosition,
    bool TemplateSyntax)
{
    public static LoggingSettings Disabled { get; } =
        new(false, false, false, false, false, false);

    public static LoggingSettings Resolve(bool enabled, LoggingConfig? config)
    {
        if (!enabled)
            return Disabled;

        return new LoggingSettings(
            Enabled: true,
            UnknownPlaceholder: config?.UnknownPlaceholder ?? true,
            UnusedValue: config?.UnusedValue ?? true,
            ValueCount: config?.ValueCount ?? true,
            ExceptionPosition: config?.ExceptionPosition ?? true,
            TemplateSyntax: config?.TemplateSyntax ?? true);
    }
}
