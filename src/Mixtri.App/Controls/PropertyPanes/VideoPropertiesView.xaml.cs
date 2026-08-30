using Microsoft.UI.Xaml.Controls;

namespace Mixtri_App.Controls.PropertyPanes;

/// <summary>
/// Video / camera overlay properties: shape, border, mirroring and the per-segment
/// fullscreen animation. Markup only — the hosting <c>EditorPage</c> wires the control
/// events and owns all editing logic.
/// </summary>
public sealed partial class VideoPropertiesView : UserControl
{
    public VideoPropertiesView()
    {
        InitializeComponent();
    }
}
