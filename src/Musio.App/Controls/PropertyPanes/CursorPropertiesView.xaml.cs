using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Mouse / cursor properties: cursor type, size, tilt and color. Markup only — the hosting
/// <c>EditorPage</c> wires the control events and owns all editing logic.
/// </summary>
public sealed partial class CursorPropertiesView : UserControl
{
    public CursorPropertiesView()
    {
        InitializeComponent();
    }
}
