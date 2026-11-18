using System.Windows;

namespace Snit.Views;

public partial class TextPromptWindow : Window
{
    private TextPromptWindow(string question, string text)
    {
        InitializeComponent();
        Question.Text = question;
        Answer.Text = text;
        Loaded += (_, _) =>
        {
            Answer.Focus();
            Answer.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Asks for a bit of text. Returns null when you click Cancel.</summary>
    public static string? Ask(Window owner, string question, string text)
    {
        var window = new TextPromptWindow(question, text) { Owner = owner };
        return window.ShowDialog() == true ? window.Answer.Text.Trim() : null;
    }
}
