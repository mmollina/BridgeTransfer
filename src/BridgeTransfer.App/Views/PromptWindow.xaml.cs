using System.Windows;
using System.Windows.Automation;

namespace BridgeTransfer.App.Views;

/// <summary>Single text input (new folder, rename) or masked secret input (password, passphrase).</summary>
public partial class PromptWindow
{
    private Func<string, string?>? _validate;

    public PromptWindow()
    {
        InitializeComponent();
    }

    public static string? AskText(string title, string label, string initial, Func<string, string?> validate, string okText)
    {
        var w = new PromptWindow { Title = title };
        w.Heading.Text = title;
        w.LabelText.Text = label;
        w.Ok.Content = okText;
        w._validate = validate;
        w.Input.Text = initial;
        AutomationProperties.SetName(w.Input, label);
        w.Loaded += (_, _) =>
        {
            w.Input.Focus();
            var dot = initial.LastIndexOf('.');
            w.Input.Select(0, dot > 0 ? dot : initial.Length);
        };
        return w.ShowDialog() == true ? w.Input.Text.Trim() : null;
    }

    public static string? AskSecret(string title, string message, string label)
    {
        var w = new PromptWindow { Title = title };
        w.Heading.Text = title;
        w.LabelText.Text = message;
        w.Input.Visibility = Visibility.Collapsed;
        w.Secret.Visibility = Visibility.Visible;
        AutomationProperties.SetName(w.Secret, label);
        w.Ok.Content = "Continue";
        w.Loaded += (_, _) => w.Secret.Focus();
        var ok = w.ShowDialog() == true;
        var value = ok ? w.Secret.Password : null;
        w.Secret.Clear();
        return value;
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Problem.Visibility = Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_validate is not null)
        {
            var text = Input.Text.Trim();
            var problem = text.Length == 0 ? "Enter a name." : _validate(text);
            if (problem is not null)
            {
                Problem.Text = problem;
                Problem.Visibility = Visibility.Visible;
                Input.Focus();
                return;
            }
        }

        DialogResult = true;
    }
}
