using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BridgeTransfer.App.Views;

public enum MessageKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>Message with optional technical details (hidden by default) and custom buttons.</summary>
public partial class MessageWindow
{
    private object? _result;

    public MessageWindow()
    {
        InitializeComponent();
    }

    public static object? Show(string title, string message, MessageKind kind, string? details, params (string Text, object Result, bool IsPrimary, bool IsDefault, bool IsCancel)[] buttons)
    {
        var w = new MessageWindow();
        w.Heading.Text = title;
        w.Body.Text = message;
        w.Title = title;
        var (icon, fg, bg) = kind switch
        {
            MessageKind.Error => ("Icon.Error", "Brush.Danger", "Brush.DangerSoft"),
            MessageKind.Warning => ("Icon.Warning", "Brush.Warning", "Brush.WarningSoft"),
            MessageKind.Question => ("Icon.Question", "Brush.AccentForeground", "Brush.AccentSoft"),
            _ => ("Icon.Info", "Brush.AccentForeground", "Brush.AccentSoft"),
        };
        w.IconGlyph.Data = (Geometry)w.FindResource(icon);
        w.IconGlyph.SetResourceReference(Controls.Glyph.ForegroundProperty, fg);
        w.IconBadge.SetResourceReference(Border.BackgroundProperty, bg);
        if (!string.IsNullOrWhiteSpace(details))
        {
            w.DetailsArea.Visibility = Visibility.Visible;
            w.Details.Text = details;
        }

        foreach (var b in buttons)
        {
            var button = new Button
            {
                Content = b.Text,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = b.IsDefault,
                IsCancel = b.IsCancel,
            };
            if (b.IsPrimary)
            {
                button.Style = (Style)w.FindResource("Button.Primary");
            }

            var result = b.Result;
            button.Click += (_, _) =>
            {
                w._result = result;
                w.DialogResult = true;
            };
            System.Windows.Automation.AutomationProperties.SetName(button, b.Text);
            w.Buttons.Children.Add(button);
        }

        w.Loaded += (_, _) =>
        {
            var focus = w.Buttons.Children.OfType<Button>().FirstOrDefault(x => x.IsDefault) ?? w.Buttons.Children.OfType<Button>().LastOrDefault();
            focus?.Focus();
        };
        w.ShowDialog();
        return w._result;
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        var show = DetailsBox.Visibility != Visibility.Visible;
        DetailsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggle.Content = show ? "Hide technical details" : "Show technical details";
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Details.Text);
        }
        catch (Exception)
        {
        }
    }
}
