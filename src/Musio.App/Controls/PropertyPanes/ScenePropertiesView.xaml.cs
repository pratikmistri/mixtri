using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Scene (frame style) properties: aspect ratio, fit mode, zoom scope, crop anchor and
/// background styling. Markup only — the hosting <c>EditorPage</c> wires the control
/// events and owns all editing logic.
/// </summary>
public sealed partial class ScenePropertiesView : UserControl
{
    public ScenePropertiesView()
    {
        InitializeComponent();
    }
}
