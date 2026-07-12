using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using ExplorerHelper.Models;
using ExplorerHelper.Services;
using ExplorerHelper.ViewModels;

namespace ExplorerHelper;

// FluentWindow is fully qualified so the broad Wpf.Ui.Controls namespace doesn't collide
// with System.Windows types used below (MessageBox, Button, …).
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".m4v"];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma"];

    private readonly MainViewModel _vm = new();
    private bool _webViewReady;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedFile))
            {
                ShowPreview(_vm.SelectedFile);
                UpdateRenameBar(_vm.SelectedFile);
            }
        };

        // WebView2 must not write its cache next to the exe (read-only when installed).
        PreviewWeb.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerHelper", "WebView2"),
        };

        UpdateSortIndicators();
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
        var fixedWidth = IconColumn.ActualWidth + DateColumn.ActualWidth
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
        }
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
        ClearPreview();

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
        ClearPreview();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var error = _vm.QuickRename(entry, stem);
            if (error is not null)
            {
                MessageBox.Show(this, error, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowPreview(_vm.SelectedFile); // restore the preview we cleared
                RenameBox.Focus();
                return;
            }

            // Advance to the next file for the review flow. If we were already on the last one,
            // the selection doesn't change, so re-show its (now cleared) preview ourselves.
            if (!AdvanceSelection())
                ShowPreview(_vm.SelectedFile);

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

    private void ShowPreview(FileEntry? entry)
    {
        ClearPreview();
        if (entry is null)
        {
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        var ext = Path.GetExtension(entry.FullPath).ToLowerInvariant();

        try
        {
            if (!entry.IsDirectory && ImageExtensions.Contains(ext))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(entry.FullPath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
                bitmap.EndInit();
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                return;
            }

            if (!entry.IsDirectory && (VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext)))
            {
                PreviewMedia.Source = new Uri(entry.FullPath);
                // Keep the media element live so playback runs; for audio it renders no picture,
                // so overlay a speaker on top (it sits later in the Grid, so it wins z-order) to
                // make clear a file is selected and playing (issue #8).
                PreviewMedia.Visibility = Visibility.Visible;
                if (AudioExtensions.Contains(ext))
                {
                    PreviewAudioName.Text = entry.Name;
                    PreviewAudioDetail.Text = $"{entry.Extension} · {entry.SizeDisplay} · playing";
                    PreviewAudio.Visibility = Visibility.Visible;
                }
                PreviewMedia.Play();
                return;
            }

            if (!entry.IsDirectory && ext == ".pdf")
            {
                ShowPdf(entry.FullPath);
                return;
            }
        }
        catch
        {
            // fall through to the generic info panel
        }

        // Everything else: big shell thumbnail + details
        PreviewInfoThumb.Source = ShellThumbnailService.GetThumbnail(entry.FullPath, 256);
        PreviewInfoName.Text = entry.Name;
        PreviewInfoDetail.Text = entry.IsDirectory
            ? $"Folder · modified {entry.ModifiedDisplay}"
            : $"{entry.Extension} · {entry.SizeDisplay} · modified {entry.ModifiedDisplay}";
        PreviewInfo.Visibility = Visibility.Visible;
    }

    private async void ShowPdf(string path)
    {
        try
        {
            if (!_webViewReady)
            {
                await PreviewWeb.EnsureCoreWebView2Async();
                _webViewReady = true;
            }
            PreviewWeb.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
            PreviewWeb.Visibility = Visibility.Visible;
        }
        catch
        {
            // WebView2 runtime missing — show the generic panel instead.
            PreviewInfoName.Text = Path.GetFileName(path);
            PreviewInfoDetail.Text = "PDF preview requires the WebView2 runtime.";
            PreviewInfo.Visibility = Visibility.Visible;
        }
    }

    private void ClearPreview()
    {
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewMedia.Stop();
        PreviewMedia.Close(); // releases the media session and the underlying file handle
        PreviewMedia.Source = null;
        PreviewMedia.Visibility = Visibility.Collapsed;
        PreviewAudio.Visibility = Visibility.Collapsed;
        PreviewWeb.Visibility = Visibility.Collapsed;
        if (_webViewReady)
            PreviewWeb.CoreWebView2.Navigate("about:blank"); // release the file handle
        PreviewInfo.Visibility = Visibility.Collapsed;
    }

    private void PreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        PreviewMedia.Position = TimeSpan.Zero;
        PreviewMedia.Play();
    }
}
