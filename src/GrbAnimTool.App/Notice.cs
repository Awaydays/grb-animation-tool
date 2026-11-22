using System.Windows;
using System.Windows.Controls;

namespace GrbAnimTool.App;

/// <summary>A message with an OK button, in a small window of the theme shown (a system message box would stay light in a dark
/// theme).</summary>
static class Notice
{
    public static void Show(string text, Window? owner = null)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        body.Children.Add(ok);
        var w = new Window
        {
            Title = "GRB Anim Tool",
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        w.SetResourceReference(Control.BackgroundProperty, Theme.Key("Surface"));
        w.SetResourceReference(Control.ForegroundProperty, Theme.Key("Text"));
        ok.Click += (_, _) => w.Close();
        w.ShowDialog();
    }
}
