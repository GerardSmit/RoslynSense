using System.Diagnostics;

namespace RoslynMCP;

/// <summary>
/// Lets an installed <c>roslyn-sense</c> hand the whole session to a build sitting in a working
/// tree, so a host that launches the tool by name can be pointed at development bits.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves: a host like Claude Code launches the MCP server as bare
/// <c>roslyn-sense</c>, resolved from PATH, with no way to name a different binary. Changing that
/// means editing the host's own configuration, which is fine once and tedious every time — and
/// easy to forget, so an afternoon gets spent testing yesterday's build.
/// </para>
/// <para>
/// Set <c>ROSLYNSENSE_SERVER</c> to a built executable and the installed tool becomes a pipe to
/// it: same arguments, stdio joined end to end, and the child's exit code returned. Unset it and
/// nothing happens at all. Development only — it is a redirect to whatever that variable names.
/// </para>
/// </remarks>
internal static class DevBuildRedirect
{
    public const string Variable = "ROSLYNSENSE_SERVER";

    /// <summary>Set in the child so a misconfigured redirect cannot spawn itself forever.</summary>
    private const string GuardVariable = "ROSLYNSENSE_SERVER_REDIRECTED";

    /// <summary>
    /// Runs the redirect target if one is configured.
    /// </summary>
    /// <returns>The child's exit code, or <c>null</c> when there is nothing to redirect to and
    /// this process should carry on being the server itself.</returns>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable(GuardVariable) == "1")
            return null;

        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } target)
            return null;

        if (!File.Exists(target))
        {
            await Console.Error.WriteLineAsync(
                $"[roslyn-sense] {Variable} points at '{target}', which does not exist. " +
                "Running this build instead.");
            return null;
        }

        // Redirecting to ourselves would be a fork bomb with extra steps.
        string? current = Environment.ProcessPath;
        if (current is not null &&
            Path.GetFullPath(target).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment[GuardVariable] = "1";

        using var child = Process.Start(startInfo);
        if (child is null)
        {
            await Console.Error.WriteLineAsync($"[roslyn-sense] Could not start '{target}'.");
            return null;
        }

        await Console.Error.WriteLineAsync($"[roslyn-sense] Using the build at {target}.");

        // Raw stream copies rather than line reads: this carries a protocol, and reformatting it
        // on the way through is how a proxy becomes a bug.
        var pumps = new[]
        {
            Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream),
            child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
            child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()),
        };

        await child.WaitForExitAsync();

        // The child has gone; whichever pump is still waiting on its stream never will.
        await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(TimeSpan.FromSeconds(2)));

        return child.ExitCode;
    }
}
