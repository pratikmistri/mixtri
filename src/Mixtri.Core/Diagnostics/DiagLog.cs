using System.Diagnostics;
using System.Text;

namespace Mixtri.Core.Diagnostics;

/// <summary>
/// Appends timestamped diagnostic lines to a file under LocalAppData.
/// </summary>
/// <remarks>
/// <see cref="Debug.WriteLine"/> is invisible when the app is launched normally rather
/// than under a debugger, which is exactly when most reproductions happen. This gives a
/// durable trace that can be read after the fact.
/// </remarks>
public static class DiagLog
{
    private static readonly object Gate = new();

    /// <summary>Size at which the log is rotated to <c>diag.log.old</c>.</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The size is checked on the first write and then every this-many writes, not on
    /// every line: a FileInfo stat per write was a synchronous file-system round trip on
    /// whichever thread (often the UI thread) happened to log.
    /// </summary>
    private const int SizeCheckInterval = 64;

    private static int _writesSinceSizeCheck = SizeCheckInterval;
    private static readonly Lazy<string?> LogPath = new(CreatePath);

    private static string? CreatePath()
    {
        try
        {
            var dir = AppDataPaths.Root;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "diag.log");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Path of the log file, or null when it could not be created.</summary>
    public static string? FilePath => LogPath.Value;

    public static void Write(string category, string message)
    {
        Debug.WriteLine($"[{category}] {message}");

        var path = LogPath.Value;
        if (path is null)
            return;

        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                .Append(" [").Append(category).Append("] ")
                .Append(message)
                .Append(Environment.NewLine)
                .ToString();

            lock (Gate)
            {
                // Keep the file from growing without bound across sessions. Rotate rather
                // than delete, so the lines leading up to a problem survive the rollover.
                if (++_writesSinceSizeCheck >= SizeCheckInterval)
                {
                    _writesSinceSizeCheck = 0;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                        File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Diagnostics must never break the feature they are diagnosing.
        }
    }
}
