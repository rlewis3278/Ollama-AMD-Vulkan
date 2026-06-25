using System.Windows;

namespace OllamaToolkit.App;

public partial class ToolkitConfirmDialog : Window
{
    public ToolkitConfirmDialog(string message, string title = "Confirm")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    public static bool ShowAccept(Window owner, string message, string title = "Confirm")
    {
        var dialog = new ToolkitConfirmDialog(message, title) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void AcceptBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void DenyBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}