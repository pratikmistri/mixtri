using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Properties for the currently selected zoom segment: zoom level, edit-region shortcut,
/// per-segment camera drift, and removal. Markup only — the hosting <c>EditorPage</c>
/// wires the control events and owns all editing logic.
/// </summary>
public sealed partial class ZoomPropertiesView : UserControl
{
    public ZoomPropertiesView()
    {
        InitializeComponent();
    }
}
