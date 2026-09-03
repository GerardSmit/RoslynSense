namespace RoslynSense.Tray;

/// <summary>
/// One tray icon for every RoslynSense host on the machine.
/// </summary>
/// <remarks>
/// Started by a host as it comes online (see <c>TrayLauncher</c>) rather than by the user, so the
/// two things that matter are that a second copy never appears and that this one goes away again
/// on its own once there is nothing left to show.
/// </remarks>
internal static class Program
{
    /// <summary>Session-scoped, matching the name the host probes before spawning.</summary>
    private const string SingleInstanceMutex = @"Local\RoslynSenseTray";

    [STAThread]
    private static void Main()
    {
        // Held for the process lifetime: releasing it is what tells the next host to spawn a
        // replacement, so it must outlive the message loop, not the check below.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
