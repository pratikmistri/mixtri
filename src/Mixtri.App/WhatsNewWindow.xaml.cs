using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Interop;
using Mixtri.Core.Settings;
using Mixtri_App.Helpers;
using Windows.Graphics;

namespace Mixtri_App;

/// <summary>
/// A one-time, centred notice: a heading, some paragraphs and a dismiss button.
/// </summary>
/// <remarks>
/// <para>
/// A window rather than a <c>ContentDialog</c>, because a ContentDialog renders inside its
/// host window and the app's default launch surface is the Mini pill — a small bar docked at
/// the bottom of the screen, which a dialog would be clipped by and could not be centred in.
/// </para>
/// <para>
/// Content and the "seen" key are supplied by the caller so this can carry any release's
/// notes; see <see cref="CreateRebrandNotice"/> for the first use. Each notice must pass its
/// OWN key — sharing one would let whichever shipped first suppress every later notice.
/// </para>
/// <para>
/// The seen flag is written when the user DISMISSES the notice, never when it is shown, so a
/// launch that is killed or crashes before they read it shows the notice again rather than
/// burning it silently. Dismissal is deliberately available three ways — the button, Escape,
/// and the window's own close — and all route through <see cref="MarkSeen"/>.
/// </para>
/// </remarks>
public sealed partial class WhatsNewWindow : Window
{
    private const string BrandMarkAsset = "ms-appx:///Assets/Square44x44Logo.targetsize-256.png";

    private readonly string _seenSettingKey;
    private bool _dismissed;
    private bool _resizing;
    private SizeInt32 _lastAppliedSize;

    public WhatsNewWindow(
        string heading,
        IReadOnlyList<string> paragraphs,
        string seenSettingKey,
        object? headingContent = null,
        string dismissText = "Got it")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seenSettingKey);

        _seenSettingKey = seenSettingKey;

        InitializeComponent();

        Title = heading;
        HeadingHost.Content = headingContent ?? new TextBlock
        {
            Text = heading,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        };
        Paragraphs.ItemsSource = paragraphs;
        DismissButton.Content = dismissText;

        // SizeChanged as well as Loaded: the ItemsControl realises its paragraphs after the
        // first layout pass, so measuring only on Loaded produces a window too short for its
        // own content and visibly clips the dismiss button.
        RootGrid.Loaded += (_, _) => MeasureAndCentre();
        RootGrid.SizeChanged += (_, _) => MeasureAndCentre();
        RootGrid.KeyDown += OnRootKeyDown;

        ConfigureWindow();
    }

    /// <summary>
    /// The Musio → Mixtri rename notice. Kept here so the copy, its heading and its settings
    /// key stay together and cannot drift apart.
    /// </summary>
    public static WhatsNewWindow CreateRebrandNotice() => new(
        "Musio is now Mixtri",
        [
            "Same app, same features — just a new name. Nothing else has changed.",
            "Your existing projects still open, including ones saved as .musio files. New projects are saved as .mixtri.",
        ],
        Mixtri.Core.Shell.WhatsNewNotice.RebrandSeenSettingKey,
        headingContent: BuildRebrandHeading());

    /// <summary>
    /// <c>[mark] Musio → [mark] Mixtri</c>. The SAME brand mark appears on both sides on
    /// purpose: it is the clearest way to say the app itself has not changed, only its name.
    /// </summary>
    private static UIElement BuildRebrandHeading()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(BrandMark());
        row.Children.Add(NameText("Musio"));
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE72A", // Forward
            FontSize = 15,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        row.Children.Add(BrandMark());
        row.Children.Add(NameText("Mixtri"));

        return row;
    }

    /// <summary>Decorative: the names beside it already carry the meaning for a screen reader.</summary>
    private static Image BrandMark()
    {
        var image = new Image
        {
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri(BrandMarkAsset)),
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        return image;
    }

    private static TextBlock NameText(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
    };

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeadingHost);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // The shell behind this (the Mini pill) is itself always-on-top, so without this
            // the notice would open behind the very surface it is explaining.
            presenter.IsAlwaysOnTop = true;
        }

        // SetBorderAndTitleBar leaves the DWM frame, which draws a light 1px outline that is
        // very visible against the acrylic. This clears it properly.
        WindowChrome.ApplyBorderlessRounded(hwnd);

        // Reachable from Alt-Tab, unlike the pill. An always-on-top window the user has
        // clicked past must still be findable, or the app looks stuck behind a notice they
        // cannot get back to.
        AppWindow.IsShownInSwitchers = true;

        if (DesktopAcrylicController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();
        else
            SystemBackdrop = new MicaBackdrop();

        // Closing by any route (X, Alt+F4, shell teardown) counts as dismissal, so the notice
        // cannot reappear after the user has visibly acknowledged it.
        AppWindow.Closing += (_, _) => MarkSeen();
    }

    /// <summary>
    /// Sizes the window around its content and centres it on the work area of the display it
    /// opened on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against the root's FIXED width rather than infinity, so the paragraphs wrap
    /// exactly as they will on screen. Measuring unconstrained lets them report a single-line
    /// width, and the height that comes back is then far too short for the wrapped text —
    /// which is what clipped the dismiss button.
    /// </para>
    /// <para>
    /// <see cref="AppWindow"/> works in PHYSICAL pixels while XAML measures in DIPs, so the
    /// desired size is scaled by this window's own monitor DPI — never a system-wide value,
    /// which would be wrong the moment the notice opens on a differently-scaled display.
    /// </para>
    /// </remarks>
    private void MeasureAndCentre()
    {
        // Resize raises SizeChanged, which lands back here.
        if (_resizing) return;

        try
        {
            RootGrid.Measure(new Windows.Foundation.Size(
                RootGrid.Width, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize;
            if (desired.Width <= 0 || desired.Height <= 0) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = MonitorInterop.GetDpiForWindow(hwnd) / 96.0;

            var size = new SizeInt32(
                (int)Math.Ceiling(desired.Width * scale),
                (int)Math.Ceiling(desired.Height * scale));

            if (size.Width == _lastAppliedSize.Width && size.Height == _lastAppliedSize.Height)
                return;

            _resizing = true;
            try
            {
                AppWindow.Resize(size);
                _lastAppliedSize = size;

                var workArea = DisplayArea
                    .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                    .WorkArea;

                AppWindow.Move(new PointInt32(
                    workArea.X + ((workArea.Width - size.Width) / 2),
                    workArea.Y + ((workArea.Height - size.Height) / 2)));
            }
            finally
            {
                _resizing = false;
            }
        }
        catch (Exception ex)
        {
            // Wherever the OS put it is still readable and still dismissible.
            DiagLog.Write("WhatsNew", $"Could not size or centre the notice: {ex.Message}");
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        Dismiss();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Dismiss()
    {
        MarkSeen();
        try { Close(); }
        catch (Exception ex) { DiagLog.Write("WhatsNew", $"Close failed: {ex.Message}"); }
    }

    /// <summary>
    /// Records the dismissal. Idempotent, because Close() raises Closing and both the button
    /// and Escape have already been through here.
    /// </summary>
    private void MarkSeen()
    {
        if (_dismissed) return;
        _dismissed = true;

        try
        {
            AppSettings.Instance.Set(_seenSettingKey, true);
        }
        catch (Exception ex)
        {
            // Worst case it is shown once more; never worth failing the dismissal.
            DiagLog.Write("WhatsNew", $"Could not persist the seen flag: {ex.Message}");
        }
    }
}
