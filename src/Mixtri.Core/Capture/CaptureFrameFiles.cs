using System.Runtime.InteropServices;
using Mixtri.Core.Diagnostics;

namespace Mixtri.Core.Capture;

/// <summary>Publishes immutable frame files and shares their bytes across CFR hold slots.</summary>
internal sealed class CaptureFrameFiles
{
    private const int ErrorTooManyLinks = 1142;
    private readonly Func<string, string, int> _link;
    private bool _linksAvailable = true;

    internal long LinkedFrames { get; private set; }
    internal long CopiedBytes { get; private set; }

    internal CaptureFrameFiles(Func<string, string, int>? link = null)
        => _link = link ?? CreateLink;

    internal static void Publish(string temporaryPath, string framePath)
        => File.Move(temporaryPath, framePath, overwrite: true);

    internal void Duplicate(string sourcePath, string destinationPath)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A duplicated frame must have a different path.", nameof(destinationPath));

        // Replace a directory entry, never the contents of a possibly linked older frame.
        File.Delete(destinationPath);
        if (_linksAvailable)
        {
            int error = _link(destinationPath, sourcePath);
            if (error == 0)
            {
                LinkedFrames++;
                return;
            }
            if (error != ErrorTooManyLinks)
            {
                _linksAvailable = false;
                DiagLog.Write("Capture", $"Frame hard links unavailable (Win32 error {error}); using byte-identical file copies.");
            }
        }

        File.Copy(sourcePath, destinationPath);
        CopiedBytes += new FileInfo(destinationPath).Length;
    }

    private static int CreateLink(string destination, string source)
        => CreateHardLink(destination, source, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string destination, string source, IntPtr securityAttributes);
}
