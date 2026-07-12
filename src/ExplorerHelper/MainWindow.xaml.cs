using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ExplorerHelper.Models;
using ExplorerHelper.ViewModels;

namespace ExplorerHelper;

// FluentWindow is fully qualified so the broad Wpf.Ui.Controls namespace doesn't collide
// with System.Windows types used below (MessageBox, Button, …).
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.SelectedFile):
                    Preview.Show(_vm.SelectedFile);
                    UpdateRenameBar(_vm.SelectedFile);
                    break;
                case nameof(MainViewModel.TodayDateFormat):
                case nameof(MainViewModel.CreatedDateFormat):
                    // Live-update the date button labels as the user edits formats in Settings.
                    RefreshDynamicButtons(_vm.SelectedFile);
                    break;
            }
        };

        TriageOverlay.CloseRequested += (_, _) =>
        {
            // Coming back from the overlay: restore the side preview and list focus.
            Preview.Show(_vm.SelectedFile);
            FileList.Focus();
        };

        UpdateSortIndicators();
        RefreshDynamicButtons(null); // seed the "today" button label before any file is selected
    }

    public void LoadFolder(string path)
    {
        _vm.LoadFolder(path);
        Title = $"Explorer Helper — {path}";
    }

    // --- Column sorting (issue #4) ---------------------------------------------------

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader { Tag: string key })
        {
            _vm.SortBy(key);
            UpdateSortIndicators();
        }
    }

    /// <summary>Shows a ▲/▼ arrow on the active column header and clears it from the others.</summary>
    private void UpdateSortIndicators()
    {
        var arrow = _vm.SortDescending ? " ▼" : " ▲";
        HdrName.Content = "Name" + (_vm.SortMode == "Name" ? arrow : string.Empty);
        HdrDate.Content = "Date modified" + (_vm.SortMode == "Date" ? arrow : string.Empty);
        HdrType.Content = "Type" + (_vm.SortMode == "Type" ? arrow : string.Empty);
        HdrSize.Content = "Size" + (_vm.SortMode == "Size" ? arrow : string.Empty);
    }

    /// <summary>Keeps the Name column filling the space the fixed columns leave behind.</summary>
    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var fixedWidth = IconColumn.ActualWidth + FlagColumn.ActualWidth + DateColumn.ActualWidth
            + TypeColumn.ActualWidth + SizeColumn.ActualWidth;
        var available = FileList.ActualWidth - fixedWidth - SystemParameters.VerticalScrollBarWidth - 12;
        if (available > 120)
            NameColumn.Width = available;
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.F2:
                RenameSelected();
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                if (_vm.UndoCommand.CanExecute(null))
                    _vm.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.K:
                FlagSelected(TriageFlag.Keep);
                e.Handled = true;
                break;
            case Key.X:
                FlagSelected(TriageFlag.Reject);
                e.Handled = true;
                break;
            case Key.U:
                FlagSelected(TriageFlag.None);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// List-mode triage: flags the selection without entering the deck. A single-item flag
    /// advances to the next row so a run of K/K/X/K stays on the keyboard, like Del does.
    /// </summary>
    private void FlagSelected(TriageFlag flag)
    {
        var selected = FileList.SelectedItems.Cast<FileEntry>().ToList();
        if (selected.Count == 0)
            return;
        foreach (var entry in selected)
            _vm.SetFlag(entry, flag);
        if (selected.Count == 1 && FileList.SelectedIndex < FileList.Items.Count - 1)
            FileList.SelectedIndex++;
    }

    private void Triage_Click(object sender, RoutedEventArgs e)
    {
        Preview.Clear(); // the deck card takes over the (single) live media handle
        TriageOverlay.Open(_vm);
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _vm.OpenSelectedCommand.Execute(null);
    }

    private void DeleteSelected()
    {
        var selected = FileList.SelectedItems.Cast<FileEntry>().ToList();
        if (selected.Count == 0)
            return;

        var index = FileList.SelectedIndex;

        // Release preview handles first. The media engine in particular keeps the
        // video file open, and it releases the OS handle only once its teardown has
        // been pumped through the dispatcher.
        Preview.Clear();

        // Defer the actual delete to the next message pump (Background priority) so
        // that teardown finishes and the handle is freed before SHFileOperation runs.
        // Deleting synchronously here would block the shell on our own open handle and
        // freeze the UI for seconds (issue #1).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _vm.Delete(selected);

            // Keep the keyboard triage flow going: select the next item.
            if (FileList.Items.Count > 0)
            {
                FileList.SelectedIndex = Math.Min(index, FileList.Items.Count - 1);
                FileList.Focus();
            }
        }));
    }

    // --- Quick rename (review-and-name flow) -----------------------------------------

    /// <summary>
    /// Reflects the selected file in the rename bar: enables it and shows the extension that
    /// will be preserved. The staged name is intentionally left untouched so it stays sticky
    /// across files — a run of similar clips is just Enter, Enter, Enter.
    /// </summary>
    private void UpdateRenameBar(FileEntry? entry)
    {
        if (entry is null)
        {
            QuickRenamePanel.IsEnabled = false;
            RenameExtLabel.Text = string.Empty;
            return;
        }

        QuickRenamePanel.IsEnabled = true;
        RenameExtLabel.Text = entry.IsDirectory
            ? "(folder)"
            : Path.GetExtension(entry.FullPath) is { Length: > 0 } ext ? ext : "(no extension)";
        RefreshDynamicButtons(entry);
    }

    // --- Quick-use buttons (issue #14) -----------------------------------------------

    /// <summary>
    /// Refreshes the two dynamic date buttons' labels: "today" tracks the current date, "created"
    /// shows the selected file's creation date. Both use the formats configured in Settings and
    /// fall back gracefully on a bad format string (never throws from a typo).
    /// </summary>
    private void RefreshDynamicButtons(FileEntry? entry)
    {
        TodayButton.Content = MainViewModel.FormatDate(DateTime.Now, _vm.TodayDateFormat);
        CreatedButton.Content = entry is null
            ? string.Empty
            : MainViewModel.FormatDate(entry.Created, _vm.CreatedDateFormat);
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e) =>
        AppendToRenameBox(MainViewModel.FormatDate(DateTime.Now, _vm.TodayDateFormat));

    private void CreatedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { } entry)
            AppendToRenameBox(MainViewModel.FormatDate(entry.Created, _vm.CreatedDateFormat));
    }

    private void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text })
            AppendToRenameBox(text);
    }

    /// <summary>
    /// Drops preset/date text into the rename box, building the name up piece by piece: appends
    /// with a single space when the box already has content, otherwise seeds it. Keeps focus and
    /// the caret at the end so the user can keep typing or tap another button.
    /// </summary>
    private void AppendToRenameBox(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var current = RenameBox.Text;
        RenameBox.Text = string.IsNullOrWhiteSpace(current)
            ? text
            : current.TrimEnd() + " " + text;
        RenameBox.Focus();
        RenameBox.CaretIndex = RenameBox.Text.Length;
    }

    private void AddQuickButton_Click(object sender, RoutedEventArgs e) => CommitNewButton();

    private void NewButtonBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitNewButton();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddButtonToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void CommitNewButton()
    {
        var text = NewButtonBox.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;
        _vm.AddQuickButton(text);
        NewButtonBox.Text = string.Empty;
        AddButtonToggle.IsChecked = false;
    }

    private void RemoveQuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text })
            _vm.RemoveQuickButton(text);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ApplyQuickRename();
                e.Handled = true;
                break;
            case Key.Escape:
                FileList.Focus(); // back to the list for Del / arrow-key triage
                e.Handled = true;
                break;
        }
    }

    private void PaletteChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string name })
        {
            // Stage the name; the user still presses Enter to apply (no accidental renames).
            RenameBox.Text = name;
            RenameBox.Focus();
            RenameBox.CaretIndex = name.Length;
        }
    }

    private void ApplyQuickRename()
    {
        if (_vm.SelectedFile is not { } entry)
            return;
        var stem = RenameBox.Text;
        if (string.IsNullOrWhiteSpace(stem))
            return;

        // Release preview handles first. A previewing video keeps the file open, so File.Move
        // would fail until the media engine's teardown has been pumped through the dispatcher —
        // the same handle problem the delete path solves (issue #1). Defer the rename to the
        // next Background pump so the handle is freed before we move the file.
        Preview.Clear();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var error = _vm.QuickRename(entry, stem);
            if (error is not null)
            {
                MessageBox.Show(this, error, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                Preview.Show(_vm.SelectedFile); // restore the preview we cleared
                RenameBox.Focus();
                return;
            }

            // Advance to the next file for the review flow. If we were already on the last one,
            // the selection doesn't change, so re-show its (now cleared) preview ourselves.
            if (!AdvanceSelection())
                Preview.Show(_vm.SelectedFile);

            // Keep focus in the box so a run of similar clips stays keyboard-only.
            RenameBox.SelectAll();
            RenameBox.Focus();
        }));
    }

    /// <summary>
    /// Selects the next item without stealing focus from the rename box. Returns false when the
    /// selection didn't move (empty list or already on the last item).
    /// </summary>
    private bool AdvanceSelection()
    {
        var index = FileList.SelectedIndex;
        if (index < 0 || FileList.Items.Count == 0)
            return false;

        var next = Math.Min(index + 1, FileList.Items.Count - 1);
        if (next == index)
            return false;

        FileList.SelectedIndex = next;
        if (FileList.SelectedItem is not null)
            FileList.ScrollIntoView(FileList.SelectedItem);
        return true;
    }

    private void RenameSelected()
    {
        if (_vm.SelectedFile is not { } entry)
            return;

        var dialog = new RenameDialog(entry.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var error = _vm.Rename(entry, dialog.NewName);
        if (error is not null)
            MessageBox.Show(this, error, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

}
