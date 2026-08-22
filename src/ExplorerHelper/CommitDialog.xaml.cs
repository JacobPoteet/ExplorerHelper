using System.IO;
using System.Windows;
using ExplorerHelper.Models;
using ExplorerHelper.ViewModels;

namespace ExplorerHelper;

/// <summary>
/// Confirms a triage commit: shows what will be recycled and kept, with an optional
/// "move kept files to…" destination (e.g. pull the good shots off an SD card in the
/// same step). The last destination is remembered for the session.
/// </summary>
public partial class CommitDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Remembered across commits within this app run.</summary>
    private static string? _lastDestination;

    /// <summary>The folder keepers should move (or copy) to, or null to leave them in place.</summary>
    public string? KeepDestination { get; private set; }

    /// <summary>True to copy kept files to <see cref="KeepDestination"/> instead of moving them.</summary>
    public bool CopyKeepers { get; private set; }

    /// <summary>True to send rejected files to the Recycle Bin; false to leave them in place.</summary>
    public bool DeleteRejects { get; private set; }

    /// <summary>
    /// True to commit only the marks made in the folder currently open, leaving the rest pending
    /// (issue #43). Always false when every mark came from one folder.
    /// </summary>
    public bool CurrentFolderOnly { get; private set; }

    /// <summary>One row of the per-folder breakdown.</summary>
    private sealed record FolderRow(string Folder, string Display, string Counts);

    private readonly IReadOnlyList<MainViewModel.TriageFolderSummary> _byFolder;
    private readonly string _currentFolder;

    /// <summary>
    /// Totals are recomputed when the scope changes, so the reject and keep lines always describe
    /// what the Commit button is about to do rather than everything that happens to be marked.
    /// </summary>
    private readonly Func<bool, (int RejectCount, long RejectBytes, int KeepCount, long KeepBytes)> _totalsFor;

    public CommitDialog(
        IReadOnlyList<MainViewModel.TriageFolderSummary> byFolder,
        string currentFolder,
        Func<bool, (int RejectCount, long RejectBytes, int KeepCount, long KeepBytes)> totalsFor)
    {
        InitializeComponent();

        _byFolder = byFolder;
        _currentFolder = currentFolder;
        _totalsFor = totalsFor;

        if (byFolder.Count > 1)
        {
            var totalMarks = byFolder.Sum(f => f.KeepCount + f.RejectCount);
            var here = byFolder.FirstOrDefault(
                f => string.Equals(f.Folder, currentFolder, StringComparison.OrdinalIgnoreCase));
            var hereMarks = here is null ? 0 : here.KeepCount + here.RejectCount;

            ScopePanel.Visibility = Visibility.Visible;
            ScopeHeading.Text = $"Marks from {byFolder.Count} folders";
            ScopeAllRadio.Content = $"Commit everything ({totalMarks})";
            ScopeFolderRadio.Content = $"This folder only ({hereMarks})";
            ScopeFolderRadio.IsEnabled = hereMarks > 0;
            FolderBreakdown.ItemsSource = byFolder
                .Select(f => new FolderRow(f.Folder, f.Display, DescribeCounts(f)))
                .ToList();
        }

        ApplyTotals();
    }

    private static string DescribeCounts(MainViewModel.TriageFolderSummary summary)
    {
        var parts = new List<string>();
        if (summary.KeepCount > 0) parts.Add($"✓ {summary.KeepCount}");
        if (summary.RejectCount > 0) parts.Add($"✗ {summary.RejectCount}");
        return string.Join("  ", parts);
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while the dialog is still being constructed, before the cards exist.
        if (!IsInitialized || _totalsFor is null)
            return;
        ApplyTotals();
    }

    /// <summary>Rewrites the reject and keep cards for the scope currently selected.</summary>
    private void ApplyTotals()
    {
        var onlyHere = ScopeFolderRadio.IsChecked == true;
        var (rejectCount, rejectBytes, keepCount, keepBytes) = _totalsFor(onlyHere);

        RejectLine.Text = rejectCount == 0
            ? "✗ Nothing flagged reject."
            : $"✗ {rejectCount} file(s) flagged reject ({FileEntry.FormatSize(rejectBytes)})";
        DeletePanel.Visibility = rejectCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        KeepLine.Text = keepCount == 0
            ? "✓ Nothing flagged keep."
            : $"✓ Keep {keepCount} file(s) ({FileEntry.FormatSize(keepBytes)})";

        MovePanel.Visibility = keepCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(DestBox.Text))
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
        CurrentFolderOnly = ScopeFolderRadio.IsChecked == true;
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
