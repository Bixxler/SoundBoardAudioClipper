using System.Windows;
using System.Windows.Controls;

namespace Soundboard;

public partial class RenameWindow : Window
{
    public RenameWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
    }

    /// <summary>The new name, trimmed. Read it after ShowDialog returns true.</summary>
    public string NewName => NameBox.Text.Trim();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = NewName.Length > 0;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
