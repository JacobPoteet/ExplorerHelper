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

public partial class MainWindow : Window
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
                ShowPreview(_vm.SelectedFile);
        };

        // WebView2 must not write its cache next to the exe (read-only when installed).
        PreviewWeb.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerHelper", "WebView2"),
        };
    }

    public void LoadFolder(string path)
    {
        _vm.LoadFolder(path);
        Title = $"Explorer Helper — {path}";
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
                PreviewMedia.Visibility = Visibility.Visible;
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
