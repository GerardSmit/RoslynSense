using System.Composition;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Storage;

namespace RoslynMCP.Services;

/// <summary>
/// Where the persistent index database lives. Roslyn's default configuration keys the cache
/// folder on <c>Process.MainModule.FileName</c>, which for a dotnet tool changes on every
/// version bump and would orphan the whole cache each upgrade; this one keys purely on the
/// solution path, so indexes built by one daemon version are read by the next. Kept out of
/// <c>%TEMP%</c> deliberately — the cache is only worth having if it survives.
/// </summary>
[ExportWorkspaceService(typeof(IPersistentStorageConfiguration), ServiceLayer.Host), Shared]
internal sealed class StableStorageConfiguration : IPersistentStorageConfiguration
{
    [ImportingConstructor]
    public StableStorageConfiguration()
    {
    }

    /// <summary>Failure falls back to recompute — never take the daemon down over a cache.</summary>
    public bool ThrowOnFailure => false;

    public string? TryGetStorageLocation(SolutionKey solutionKey)
    {
        if (solutionKey.FilePath is not { Length: > 0 } solutionPath)
            return null;

        try
        {
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(solutionPath.ToLowerInvariant()));
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RoslynSense", "index-cache", Convert.ToHexString(hash.AsSpan(0, 8)));
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
