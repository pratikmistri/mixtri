using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Musio_App.Controls.PropertyPanes;
using Windows.UI;

namespace Musio_App.Controls;

/// <summary>
/// Identifies one of the property panels hosted by <see cref="PropertiesPane"/>.
/// </summary>
public enum PropertyPaneKind
{
    Scene,
    TextSlide,
    Cursor,
    Video,
    TextOverlay,
    Transition,
    Zoom,
}

/// <summary>
/// Adobe-style docked properties panel: a vertical icon rail on the outer edge switches
/// between panels, and clicking the active icon (or the header chevron) collapses the
/// panel body down to the rail.
/// </summary>
/// <remarks>
/// This control is purely presentational. It owns no editing logic — the hosting
/// <c>EditorPage</c> reaches into the individual views (<see cref="Scene"/>,
/// <see cref="TextSlide"/>, <see cref="Cursor"/>, <see cref="Video"/>,
/// <see cref="TextOverlay"/>, <see cref="Transition"/>, <see cref="Zoom"/>) to wire events
/// and push state.
/// </remarks>
public sealed partial class PropertiesPane : UserControl
{
    public PropertiesPane()
    {
        InitializeComponent();
        UpdateVisualState();
        BuildEdgeFades();
        ActualThemeChanged += (_, _) => BuildEdgeFades();
    }

    /// <summary>
    /// Reserves layout space for a rail label that has been rotated a quarter turn.
    /// </summary>
    /// <remarks>
    /// A <c>RenderTransform</c> is invisible to layout, so a rotated label would otherwise
    /// still occupy a wide, short box and the tab would size as if the text ran across the
    /// rail. Each label therefore sits in a <see cref="Canvas"/>, which measures it with no
    /// width constraint, and this handler transposes the label's real size onto the canvas
    /// so the tab grows downward instead. Driving this from <c>SizeChanged</c> (rather than
    /// an explicit measure pass) means it also works for tabs that start collapsed and
    /// become visible later, and it re-runs if the font or text scale changes.
    /// </remarks>
    private void PaneTabLabel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock label || label.Parent is not Canvas slot) return;

        double width = Math.Ceiling(e.NewSize.Width);
        double height = Math.Ceiling(e.NewSize.Height);
        if (width <= 0 || height <= 0) return;

        slot.Width = height;
        slot.Height = width;

        // The rotation pivots about the label's centre, so offset it until that centre
        // lines up with the (transposed) slot centre.
        Canvas.SetLeft(label, (height - width) / 2);
        Canvas.SetTop(label, (width - height) / 2);
    }

    /// <summary>Scene (frame style) panel.</summary>
    public ScenePropertiesView Scene => SceneView;

    /// <summary>Text slide panel.</summary>
    public TextSlidePropertiesView TextSlide => TextSlideView;

    /// <summary>Mouse / cursor panel.</summary>
    public CursorPropertiesView Cursor => CursorView;

    /// <summary>Video / camera overlay panel.</summary>
    public VideoPropertiesView Video => VideoView;

    /// <summary>Animated text overlay panel.</summary>
    public TextOverlayPropertiesView TextOverlay => TextOverlayView;

    /// <summary>Transition boundary panel.</summary>
    public TransitionPropertiesView Transition => TransitionView;

    /// <summary>Selected zoom segment panel.</summary>
    public ZoomPropertiesView Zoom => ZoomView;

    private PropertyPaneKind _selected = PropertyPaneKind.Scene;
    private bool _isOpen = true;

    /// <summary>
    /// Selects <paramref name="kind"/> and expands the panel body. Ignored when the
    /// panel's rail tab is currently hidden.
    /// </summary>
    public void ShowPane(PropertyPaneKind kind)
    {
        if (TabFor(kind).Visibility != Visibility.Visible) return;
        _selected = kind;
        _isOpen = true;
        UpdateVisualState();
    }

    /// <summary>Collapses the panel body, leaving only the icon rail.</summary>
    public void CollapsePane()
    {
        _isOpen = false;
        UpdateVisualState();
    }

    /// <summary>
    /// Shows or hides a panel's rail tab. Hiding the active panel falls back to
    /// <see cref="PropertyPaneKind.Scene"/>, which is always available.
    /// </summary>
    public void SetPaneAvailable(PropertyPaneKind kind, bool available)
    {
        TabFor(kind).Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available && _selected == kind)
            _selected = PropertyPaneKind.Scene;
        UpdateVisualState();
    }

    private void PaneTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tab) return;
        var kind = KindFor(tab);

        // Adobe behaviour: clicking the active tab collapses the panel body.
        if (_isOpen && _selected == kind)
        {
            _isOpen = false;
        }
        else
        {
            _selected = kind;
            _isOpen = true;
        }

        UpdateVisualState();
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapsePane();

    private ToggleButton TabFor(PropertyPaneKind kind) => kind switch
    {
        PropertyPaneKind.TextSlide => TextSlideTab,
        PropertyPaneKind.Cursor => CursorTab,
        PropertyPaneKind.Video => VideoTab,
        PropertyPaneKind.TextOverlay => TextOverlayTab,
        PropertyPaneKind.Transition => TransitionTab,
        PropertyPaneKind.Zoom => ZoomTab,
        _ => SceneTab,
    };

    private PropertyPaneKind KindFor(ToggleButton tab)
    {
        if (tab == TextSlideTab) return PropertyPaneKind.TextSlide;
        if (tab == CursorTab) return PropertyPaneKind.Cursor;
        if (tab == VideoTab) return PropertyPaneKind.Video;
        if (tab == TextOverlayTab) return PropertyPaneKind.TextOverlay;
        if (tab == TransitionTab) return PropertyPaneKind.Transition;
        if (tab == ZoomTab) return PropertyPaneKind.Zoom;
        return PropertyPaneKind.Scene;
    }

    private static string TitleFor(PropertyPaneKind kind) => kind switch
    {
        PropertyPaneKind.TextSlide => "Text Slide",
        PropertyPaneKind.Cursor => "Mouse",
        PropertyPaneKind.Video => "Video",
        PropertyPaneKind.TextOverlay => "Text Overlay",
        PropertyPaneKind.Transition => "Transition",
        PropertyPaneKind.Zoom => "Zoom Segment",
        _ => "Scene",
    };

    private PropertyPaneKind? _renderedPane;

    private void UpdateVisualState()
    {
        PaneBody.Visibility = _isOpen ? Visibility.Visible : Visibility.Collapsed;
        PaneTitle.Text = TitleFor(_selected);

        SceneTab.IsChecked = _isOpen && _selected == PropertyPaneKind.Scene;
        TextSlideTab.IsChecked = _isOpen && _selected == PropertyPaneKind.TextSlide;
        CursorTab.IsChecked = _isOpen && _selected == PropertyPaneKind.Cursor;
        VideoTab.IsChecked = _isOpen && _selected == PropertyPaneKind.Video;
        TextOverlayTab.IsChecked = _isOpen && _selected == PropertyPaneKind.TextOverlay;
        TransitionTab.IsChecked = _isOpen && _selected == PropertyPaneKind.Transition;
        ZoomTab.IsChecked = _isOpen && _selected == PropertyPaneKind.Zoom;

        SceneView.Visibility = Vis(PropertyPaneKind.Scene);
        TextSlideView.Visibility = Vis(PropertyPaneKind.TextSlide);
        CursorView.Visibility = Vis(PropertyPaneKind.Cursor);
        VideoView.Visibility = Vis(PropertyPaneKind.Video);
        TextOverlayView.Visibility = Vis(PropertyPaneKind.TextOverlay);
        TransitionView.Visibility = Vis(PropertyPaneKind.Transition);
        ZoomView.Visibility = Vis(PropertyPaneKind.Zoom);

        // Each panel starts at the top rather than inheriting the previous panel's scroll.
        if (_renderedPane != _selected)
        {
            _renderedPane = _selected;
            PaneScroller.ChangeView(null, 0, null, true);
        }

        UpdateEdgeFades();

        Visibility Vis(PropertyPaneKind kind)
            => _selected == kind ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Scrolled-content edge fades ────────────────────────────────────

    private void PaneScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => UpdateEdgeFades();

    private void PaneContent_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateEdgeFades();

    /// <summary>
    /// Builds the top/bottom fade gradients from the panel background so the scrim blends
    /// into the panel instead of tinting it. Both stops share the background colour and
    /// only differ in alpha, which avoids the grey halo a fade to <c>Transparent</c>
    /// (#00000000) would produce.
    /// </summary>
    private void BuildEdgeFades()
    {
        if (PaneRoot.Background is not SolidColorBrush background) return;
        var opaque = background.Color;
        var clear = Color.FromArgb(0, opaque.R, opaque.G, opaque.B);

        TopFade.Background = BuildFade(opaque, clear);
        BottomFade.Background = BuildFade(clear, opaque);

        static LinearGradientBrush BuildFade(Color from, Color to) => new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = from, Offset = 0 },
                new GradientStop { Color = to, Offset = 1 },
            },
        };
    }

    /// <summary>
    /// Shows each fade only when there is content scrolled past that edge, so a panel that
    /// fits entirely has no scrim at all.
    /// </summary>
    private void UpdateEdgeFades()
    {
        if (PaneScroller is null) return;

        double offset = PaneScroller.VerticalOffset;
        double scrollable = PaneScroller.ScrollableHeight;

        TopFade.Opacity = offset > 1 ? 1 : 0;
        BottomFade.Opacity = scrollable - offset > 1 ? 1 : 0;
    }
}
