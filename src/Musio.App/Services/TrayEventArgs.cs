using Musio_App.ViewModels;

namespace Musio_App.Services;

/// <summary>
/// Event arguments for the tray's "new recording" entries. A <c>null</c>
/// <see cref="PreselectedMode"/> means "summon Mini Setup with no mode
/// change"; a non-null value means "set the capture mode and (for Window /
/// CustomRegion) auto-launch the picker after summon".
/// </summary>
public sealed class NewRecordingRequestedEventArgs : System.EventArgs
{
    public CaptureMode? PreselectedMode { get; }
    public NewRecordingRequestedEventArgs(CaptureMode? mode) { PreselectedMode = mode; }
}

/// <summary>
/// Event arguments for the tray's "Open Musio / Settings" entries. The
/// optional <see cref="Page"/> string is one of <c>"record"</c>,
/// <c>"editor"</c>, <c>"settings"</c>, or <c>null</c> (no specific page).
/// </summary>
public sealed class OpenFullRequestedEventArgs : System.EventArgs
{
    public string? Page { get; }
    public OpenFullRequestedEventArgs(string? page) { Page = page; }
}
