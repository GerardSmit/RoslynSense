namespace RoslynMCP.Services.Run;

/// <summary>
/// The console output of apps this process did not launch, on disk beside the running-process
/// registry.
/// </summary>
/// <remarks>
/// A chat reads its own apps' output from the live <see cref="AppSession"/>, which it holds in
/// memory. An app the user started in the editor has no such session here — its output arrives as
/// DAP <c>output</c> events in VS Code — so without a file on the side, "show me what the app
/// printed" is answerable for half the apps on the machine and not the other half.
///
/// Capped and trimmed from the front: this is a tail for diagnosis, not a transcript, and a
/// long-running web app would otherwise fill the disk.
/// </remarks>
public static class ProcessOutputLog
{
    private const long MaxBytes = 1024 * 1024;

    /// <summary>How much to keep when trimming — well under the cap, so trimming is rare.</summary>
    private const int KeepBytes = 256 * 1024;

    private static string Directory =>
        Path.Combine(Path.GetTempPath(), "roslyn-sense", "output");

    private static string FileFor(int pid) => Path.Combine(Directory, $"{pid}.log");

    public static void Append(int pid, string text)
    {
        if (pid <= 0 || text.Length == 0)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = FileFor(pid);

            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                Trim(path);

            File.AppendAllText(path, text);
        }
        catch
        {
            // Diagnostic only: an app's output is never worth failing a debug session over.
        }
    }

    /// <summary>The last <paramref name="lines"/> lines, or an empty string when nothing is logged.</summary>
    public static string Tail(int pid, int lines)
    {
        try
        {
            string path = FileFor(pid);
            if (!File.Exists(path))
                return "";

            var kept = new Queue<string>(lines);
            foreach (string line in File.ReadLines(path))
            {
                if (kept.Count == lines)
                    kept.Dequeue();
                kept.Enqueue(line);
            }
            return string.Join(Environment.NewLine, kept);
        }
        catch
        {
            return "";
        }
    }

    public static void Delete(int pid)
    {
        try { File.Delete(FileFor(pid)); }
        catch { }
    }

    /// <summary>How long an exited app's output stays readable.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <summary>
    /// Drops logs left by apps that have exited, after a grace period.
    /// </summary>
    /// <remarks>
    /// The grace period is the point: "the app died, what did it print" is asked after the exit,
    /// and deleting the log the moment the process ends answers it with silence. An hour is long
    /// enough for that question and short enough that a recycled PID does not inherit the text.
    /// </remarks>
    public static void Sweep()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return;

            var cutoff = DateTime.UtcNow - Retention;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch
        {
        }
    }

    /// <summary>Keeps the tail and drops the rest, at a line boundary.</summary>
    private static void Trim(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Seek(-KeepBytes, SeekOrigin.End);

            var buffer = new byte[KeepBytes];
            int read = stream.Read(buffer, 0, buffer.Length);

            // Start after the first newline in the kept block, so the file never opens mid-line.
            int start = Array.IndexOf(buffer, (byte)'\n', 0, read) + 1;
            if (start <= 0)
                start = 0;

            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(buffer, start, read - start);
            stream.SetLength(read - start);
        }
        catch
        {
        }
    }
}
