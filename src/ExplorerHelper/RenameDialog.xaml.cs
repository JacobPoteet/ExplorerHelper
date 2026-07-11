using System.IO;
using System.Windows;

namespace ExplorerHelper;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text.Trim();

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.Focus();

        // Pre-select the name without the extension, like Explorer does.
        var stem = Path.GetFileNameWithoutExtension(currentName);
        NameBox.Select(0, stem.Length);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewName))
            return;
        DialogResult = true;
    }
}
