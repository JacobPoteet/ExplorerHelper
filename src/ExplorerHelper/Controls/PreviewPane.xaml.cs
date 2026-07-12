using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using ExplorerHelper.Models;
using ExplorerHelper.Services;

namespace ExplorerHelper.Controls;

/// <summary>
/// The file preview surface: images render natively, video/audio play via MediaElement,
/// PDFs via WebView2, everything else gets a large shell thumbnail + details. Extracted
/// from MainWindow so both the main split view and the triage card can host one.
/// <see cref="Clear"/> releases every file handle the preview holds — callers must clear
/// (and pump the dispatcher) before deleting, renaming, or moving the previewed file (issue #1).
/// </summary>
public partial class PreviewPane : UserControl
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".m4v"];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma"];

    private bool _webViewReady;

    // Video scrubbing state (issue #19). The timer syncs the slider to playback; _syncingSlider
    // suppresses the seek that our own slider update would otherwise trigger, and _isScrubbing
    // pauses the sync while the user drags the thumb so the two don't fight.
    private readonly DispatcherTimer _timelineTimer;
    private bool _isVideo;
    private bool _isPlaying;
    private bool _isScrubbing;
    private bool _syncingSlider;

    public PreviewPane()
    {
        InitializeComponent();

        // WebView2 must not write its cache next to the exe (read-only when installed).
        PreviewWeb.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerHelper", "WebView2"),
        };

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timelineTimer.Tick += TimelineTimer_Tick;

        // Thumb drag events only surface as bubbling routed events on the Slider.
        TimelineSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(TimelineSlider_DragStarted));
        TimelineSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(TimelineSlider_DragCompleted));
    }

    /// <summary>Text shown when no file is selected (e.g. "Select a file to preview it").</summary>
    public string PlaceholderText
    {
        get => PreviewPlaceholder.Text;
        set => PreviewPlaceholder.Text = value;
    }

    /// <summary>
    /// Whether video previews get the scrub timeline (issue #19). Turned off on the triage card,
    /// whose swipe gesture would fight the slider thumb for the mouse.
    /// </summary>
    public bool EnableVideoTimeline { get; set; } = true;

    public void Show(FileEntry? entry)
    {
        Clear();
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
                else
                {
                    // Video gets a scrub timeline (issue #19). Duration fills in on MediaOpened.
                    _isVideo = true;
                    if (EnableVideoTimeline)
                        ShowTimeline();
                }
                PreviewMedia.Play();
                _isPlaying = true;
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

    /// <summary>Hides every preview surface and releases the file handles they hold.</summary>
    public void Clear()
    {
        HideTimeline();
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

    // --- Video scrubbing timeline (issue #19) -----------------------------------------

    private void ShowTimeline()
    {
        _isScrubbing = false;
        TimelineSlider.Maximum = 1; // real length arrives on MediaOpened
        SetSliderValue(0);
        PositionText.Text = FormatTime(TimeSpan.Zero);
        DurationText.Text = FormatTime(TimeSpan.Zero);
        PlayPauseButton.Content = "⏸";
        VideoControls.Visibility = Visibility.Visible;
        _timelineTimer.Start();
    }

    private void HideTimeline()
    {
        _timelineTimer.Stop();
        VideoControls.Visibility = Visibility.Collapsed;
        _isVideo = false;
        _isPlaying = false;
        _isScrubbing = false;
    }

    private void PreviewMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_isVideo || !PreviewMedia.NaturalDuration.HasTimeSpan)
            return;
        var duration = PreviewMedia.NaturalDuration.TimeSpan;
        TimelineSlider.Maximum = Math.Max(duration.TotalSeconds, 0.1);
        DurationText.Text = FormatTime(duration);
    }

    private void TimelineTimer_Tick(object? sender, EventArgs e)
    {
        // While the user drags, leave the slider alone so we don't yank the thumb back.
        if (_isScrubbing || !PreviewMedia.NaturalDuration.HasTimeSpan)
            return;
        SetSliderValue(PreviewMedia.Position.TotalSeconds);
        PositionText.Text = FormatTime(PreviewMedia.Position);
    }

    /// <summary>Moves the slider without it being read as a user seek.</summary>
    private void SetSliderValue(double seconds)
    {
        _syncingSlider = true;
        TimelineSlider.Value = seconds;
        _syncingSlider = false;
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ignore the timer's own sync; seek only on genuine user input (drag or track click).
        if (_syncingSlider || !_isVideo)
            return;
        var position = TimeSpan.FromSeconds(e.NewValue);
        PreviewMedia.Position = position; // ScrubbingEnabled makes this update the frame live
        PositionText.Text = FormatTime(position);
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        PreviewMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
        _isScrubbing = false;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isVideo)
            return;
        if (_isPlaying)
        {
            PreviewMedia.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
        }
        else
        {
            PreviewMedia.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "⏸";
        }
    }

    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";
}
