using System.IO;
using System.Windows;
using ExplorerHelper.Models;

namespace ExplorerHelper;

/// <summary>
/// Confirms a triage commit: shows what will be recycled and kept, with an optional
/// "move kept files to…" destination (e.g. pull the good shots off an SD card in the
/// same step). The last destination is remembered for the session.
/// </summary>
public partial class CommitDialog : Window
{
    /// <summary>Remembered across commits within this app run.</summary>
    private static string? _lastDestination;

    /// <summary>The folder keepers should move (or copy) to, or null to leave them in place.</summary>
    public string? KeepDestination { get; private set; }

    /// <summary>True to copy kept files to <see cref="KeepDestination"/> instead of moving them.</summary>
    public bool CopyKeepers { get; private set; }

    /// <summary>True to send rejected files to the Recycle Bin; false to leave them in place.</summary>
    public bool DeleteRejects { get; private set; }

    public CommitDialog(int rejectCount, long rejectBytes, int keepCount, long keepBytes)
    {
        InitializeComponent();

        RejectLine.Text = rejectCount == 0
            ? "✗ Nothing flagged reject."
            : $"✗ {rejectCount} file(s) flagged reject ({FileEntry.FormatSize(rejectBytes)})";
        DeletePanel.Visibility = rejectCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        KeepLine.Text = keepCount == 0
            ? "✓ Nothing flagged keep."
            : $"✓ Keep {keepCount} file(s) ({FileEntry.FormatSize(keepBytes)})";

        MovePanel.Visibility = keepCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DestBox.Text = _lastDestination ?? string.Empty;
    }

    private void MoveCheck_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = MoveCheck.IsChecked == true;
        DestBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        MoveRadio.IsEnabled = enabled;
        CopyRadio.IsEnabled = enabled;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Move kept files to…",
            InitialDirectory = Directory.Exists(DestBox.Text) ? DestBox.Text : string.Empty,
        };
        if (picker.ShowDialog(this) == true)
            DestBox.Text = picker.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DeleteRejects = DeletePanel.Visibility != Visibility.Visible || DeleteRejectsCheck.IsChecked == true;

        if (MoveCheck.IsChecked == true)
        {
            var dest = DestBox.Text.Trim();
            if (!Directory.Exists(dest))
            {
                MessageBox.Show(this, "Pick an existing folder to move or copy the kept files to.",
                    "Commit triage", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            KeepDestination = dest;
            CopyKeepers = CopyRadio.IsChecked == true;
            _lastDestination = dest;
        }
        DialogResult = true;
    }
}
