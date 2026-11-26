using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace GrbAnimTool.App;

/// <summary>A small "?" beside a heading that opens a popup with a few lines of help (<see cref="Text"/>); a click elsewhere or Escape
/// closes it. Keyboard and screen readers reach it as a button named "Help: " and its heading.</summary>
sealed class HelpButton : Button
{
    readonly Popup _popup;
    readonly TextBlock _body;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpButton), new PropertyMetadata("", (d, e) => ((HelpButton)d)._body.Text = (string)e.NewValue));

    /// <summary>The help shown.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The heading it stands beside, for its accessible name.</summary>
    public string Heading { get => _heading; set { _heading = value; AutomationProperties.SetName(this, "Help: " + value); } }
    string _heading = "";

    public HelpButton()
    {
        Content = "?";
        Padding = new Thickness(6, 0, 6, 0);
        Margin = new Thickness(4, 0, 4, 0);
        MinWidth = 0;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        ToolTip = "Help";
        _body = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(12, 10, 12, 10) };
        var border = new Border { Child = _body, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, Theme.Key("Surface"));
        border.SetResourceReference(Border.BorderBrushProperty, Theme.Key("ListBorder"));
        _body.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key("Text"));
        _popup = new Popup { Child = border, PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false };
        _popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; Focus(); e.Handled = true; } };
        Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
    }

    /// <summary>Whether the popup is open (for tests and pictures).</summary>
    public bool IsOpen { get => _popup.IsOpen; set => _popup.IsOpen = value; }
}
