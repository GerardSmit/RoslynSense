using Docker.DotNet;

namespace RoslynMCP.Tests;

internal static class DockerAvailability
{
    private static readonly Lazy<bool> s_supportsLinuxContainers = new(Probe);

    public static bool IsAvailable => s_supportsLinuxContainers.Value;

    private static bool Probe()
    {
        try
        {
            using var cfg = new DockerClientConfiguration();
            using var client = cfg.CreateClient();
            client.System.PingAsync().GetAwaiter().GetResult();
            // Testcontainers' PostgreSQL/MSSQL images are Linux-only; a daemon in
            // Windows-container mode will fail to pull them. Skip in that case.
            var info = client.System.GetSystemInfoAsync().GetAwaiter().GetResult();
            return !string.Equals(info.OSType, "windows", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
