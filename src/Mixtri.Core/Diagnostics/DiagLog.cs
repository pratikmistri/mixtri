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
                // Keep the file from growing without bound across sessions.
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.Delete(path);

                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Diagnostics must never break the feature they are diagnosing.
        }
    }
}
