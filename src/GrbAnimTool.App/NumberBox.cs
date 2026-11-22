using System.Windows;
using System.Windows.Controls;

namespace GrbAnimTool.App;

/// <summary>A text box for a short number (a length in seconds), in the theme's text box style without its clear button. The Fluent
/// theme's text box shows a clear button (its template's <see cref="ClearButtonName"/>, at least 30 wide with its margin) inside the box
/// whenever the box has the keyboard and holds text; in a box sized for a number it covers what is typed. The button is hidden here by a
/// local value, which outranks the template's own triggers, every time a template is applied (a change of theme applies a new one).</summary>
public sealed class NumberBox : TextBox
{
    /// <summary>The name of the Fluent text box template's clear button.</summary>
    public const string ClearButtonName = "DeleteButton";

    public NumberBox()
    {
        // the theme's text box style, looked up as a text box's own (an implicit style is found by the element's type, which this isn't)
        SetResourceReference(StyleProperty, typeof(TextBox));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild(ClearButtonName) is UIElement clear) clear.Visibility = Visibility.Collapsed;
    }
}
