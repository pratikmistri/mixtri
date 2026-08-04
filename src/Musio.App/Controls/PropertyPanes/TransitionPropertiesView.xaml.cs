using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Controls.PropertyPanes;

/// <summary>
/// Transition boundary properties: type (family + variant), duration, easing, and the
/// "apply to all boundaries" action. Markup only — the hosting <c>EditorPage</c> wires the
/// control events and owns all editing logic.
/// </summary>
public sealed partial class TransitionPropertiesView : UserControl
{
    public TransitionPropertiesView()
    {
        InitializeComponent();
    }
}
