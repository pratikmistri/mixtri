using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Animated text overlay properties: presets, text, animation, typography, placement and
/// background. Markup only — the hosting <c>EditorPage</c> wires the control events and
/// owns all editing logic.
/// </summary>
public sealed partial class TextOverlayPropertiesView : UserControl
{
    public TextOverlayPropertiesView()
    {
        InitializeComponent();
    }
}
