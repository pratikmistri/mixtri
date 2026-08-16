using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Text slide properties: text, animation window, typography, colors and background. Markup
/// only — the hosting <c>EditorPage</c> wires the control events and owns all editing logic.
/// </summary>
public sealed partial class TextSlidePropertiesView : UserControl
{
    public TextSlidePropertiesView()
    {
        InitializeComponent();
    }
}
