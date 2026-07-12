using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ExplorerHelper.Models;
using ExplorerHelper.ViewModels;

namespace ExplorerHelper.Controls;

/// <summary>
/// Dating-app style triage: one file at a time as a swipeable card (drag, arrow keys, or the
/// big ✗/✓ buttons), plus a review screen showing both piles. Nothing touches the disk while
/// swiping — flags accumulate on the entries and the commit happens in one shot at the end
/// (via <see cref="MainViewModel.CommitTriage"/>).
/// </summary>
public partial class TriageView : UserControl
{
    private MainViewModel _vm = null!;
    private List<FileEntry> _deck = [];
    private int _index;

    /// <summary>Indexes of cards already acted on, for Backspace-rewind.</summary>
    private readonly Stack<int> _history = new();

    private bool _dragging;
    private bool _animating;
    private Point _dragStart;

    /// <summary>Raised when the overlay should close (Exit, Esc, or after a commit).</summary>
    public event EventHandler? CloseRequested;

    public TriageView()
    {
        InitializeComponent();

        // Swiping the card owns the mouse here, so a scrub slider can't coexist (issue #19).
        CardPreview.EnableVideoTimeline = false;
    }

    /// <summary>
    /// Opens the overlay over the given view-model. The deck is a snapshot of the current
    /// (filtered, sorted) file list — folders never get cards. Starts at the first unmarked
    /// file so a resumed session picks up where it left off.
    /// </summary>
    public void Open(MainViewModel vm)
    {
        _vm = vm;
        _deck = vm.Files.Where(f => !f.IsDirectory).ToList();
        _history.Clear();
        _index = _deck.FindIndex(f => f.Flag == TriageFlag.None);

        Visibility = Visibility.Visible;
        ShowDeckState();
        Focus();
    }

    /// <summary>
    /// User-initiated exit (Exit button or Esc). Marks live only for the session, so warn
    /// before throwing them away — then discard so the "progress will be lost" message is true.
    /// </summary>
    private void RequestExit()
    {
        if (_vm.KeepCount + _vm.RejectCount > 0)
        {
            var result = MessageBox.Show(
                Window.GetWindow(this)!,
                "Exit triage and discard your marks?\n\nYour keep and reject decisions haven't been committed yet — they won't be saved.",
                "Discard triage progress?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
                return;
            _vm.ClearAllFlags();
        }
        Close();
    }

    private void Close()
    {
        CardPreview.Clear();
        Visibility = Visibility.Collapsed;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private FileEntry? Current => _index >= 0 && _index < _deck.Count ? _deck[_index] : null;

    // --- Deck state -------------------------------------------------------------------

    /// <summary>Shows the deck, landing on the current card or the done panel.</summary>
    private void ShowDeckState()
    {
        ReviewPanel.Visibility = Visibility.Collapsed;
        DeckPanel.Visibility = Visibility.Visible;

        // Past the end (or fresh open with everything already marked): jump to the first
        // unmarked card — review may have sent items back to the deck — else stay done.
        if (Current is null)
            _index = _deck.FindIndex(f => f.Flag == TriageFlag.None);

        if (Current is null)
            ShowDone();
        else
            ShowCard();
    }

    private void ShowCard()
    {
        DonePanel.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        DeckButtons.Visibility = Visibility.Visible;

        ResetCardTransforms();

        var entry = Current!;
        ProgressText.Text = $"File {_index + 1} of {_deck.Count}";
        CardName.Text = entry.Name;
        CardDetail.Text = $"{entry.Extension} · {entry.SizeDisplay} · modified {entry.ModifiedDisplay}";
        CardPreview.Show(entry);

        var next = _index + 1 < _deck.Count ? _deck[_index + 1] : null;
        NextCardHint.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        NextCardThumb.Source = next?.Thumbnail;
    }

    private void ShowDone()
    {
        CardPreview.Clear();
        Card.Visibility = Visibility.Collapsed;
        NextCardHint.Visibility = Visibility.Collapsed;
        DeckButtons.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;

        ProgressText.Text = _deck.Count == 0 ? "No files to triage" : $"{_deck.Count} files reviewed";
        DoneSummary.Text = _deck.Count == 0
            ? "This folder has no files matching the current filters."
            : $"✓ {_vm.KeepCount} keep · ✗ {_vm.RejectCount} reject · {_vm.UnmarkedFileCount} unmarked";
    }

    private void Decide(TriageFlag flag)
    {
        if (_animating || Current is not { } entry)
            return;

        if (flag == TriageFlag.None)
        {
            Advance(); // skip: no mark, just move on
            return;
        }

        // Fly the card off screen, then mark and advance.
        _animating = true;
        var direction = flag == TriageFlag.Keep ? 1 : -1;
        var stamp = flag == TriageFlag.Keep ? KeepStamp : RejectStamp;
        stamp.Opacity = 1;

        var distance = Math.Max(ActualWidth, 800);
        var duration = TimeSpan.FromMilliseconds(180);
        var slide = new DoubleAnimation(direction * distance, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        slide.Completed += (_, _) =>
        {
            _animating = false;
            _vm.SetFlag(entry, flag);
            Advance();
        };
        CardRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(direction * 24, duration));
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    private void Advance()
    {
        _history.Push(_index);
        _index++;
        if (Current is null)
            ShowDone();
        else
            ShowCard();
    }

    private void Rewind()
    {
        if (_animating || _history.Count == 0)
            return;
        _index = _history.Pop();
        _vm.SetFlag(_deck[_index], TriageFlag.None); // un-decide the card we're returning to
        ShowCard();
    }

    /// <summary>Clears any running/leftover animations and puts the card back at rest.</summary>
    private void ResetCardTransforms()
    {
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        CardRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        Card.BeginAnimation(OpacityProperty, null);
        CardTranslate.X = 0;
        CardTranslate.Y = 0;
        CardRotate.Angle = 0;
        Card.Opacity = 1;
        KeepStamp.Opacity = 0;
        RejectStamp.Opacity = 0;
    }

    private void OpenCurrent()
    {
        if (Current is { } entry)
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
    }

    // --- Drag-to-swipe ----------------------------------------------------------------

    private double SwipeThreshold => Math.Max(Card.ActualWidth / 4, 80);

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_animating || Current is null)
            return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        Card.CaptureMouse();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;

        CardTranslate.X = dx;
        CardTranslate.Y = dy * 0.2; // a little vertical give, like the real thing
        CardRotate.Angle = dx / 20;

        var strength = Math.Min(Math.Abs(dx) / SwipeThreshold, 1.0);
        KeepStamp.Opacity = dx > 0 ? strength : 0;
        RejectStamp.Opacity = dx < 0 ? strength : 0;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        Card.ReleaseMouseCapture();

        var dx = CardTranslate.X;
        if (dx > SwipeThreshold)
            Decide(TriageFlag.Keep);
        else if (dx < -SwipeThreshold)
            Decide(TriageFlag.Reject);
        else
            SpringBack();
    }

    private void SpringBack()
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(0, duration) { EasingFunction = ease });
        CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, duration) { EasingFunction = ease });
        CardRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation(0, duration) { EasingFunction = ease });
        KeepStamp.Opacity = 0;
        RejectStamp.Opacity = 0;
    }

    // --- Keyboard ---------------------------------------------------------------------

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Arrow keys would otherwise move focus between the buttons — triage owns them.
        var inDeck = DeckPanel.Visibility == Visibility.Visible;
        switch (e.Key)
        {
            case Key.Escape:
                RequestExit();
                e.Handled = true;
                break;
            case Key.Left when inDeck:
                Decide(TriageFlag.Reject);
                e.Handled = true;
                break;
            case Key.Right when inDeck:
                Decide(TriageFlag.Keep);
                e.Handled = true;
                break;
            case Key.Down when inDeck:
                Decide(TriageFlag.None);
                e.Handled = true;
                break;
            case Key.Back when inDeck:
            case Key.Z when inDeck && (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                Rewind();
                e.Handled = true;
                break;
            case Key.Enter when inDeck:
                OpenCurrent();
                e.Handled = true;
                break;
        }
    }

    // --- Buttons ----------------------------------------------------------------------

    private void Keep_Click(object sender, RoutedEventArgs e) => Decide(TriageFlag.Keep);
    private void Reject_Click(object sender, RoutedEventArgs e) => Decide(TriageFlag.Reject);
    private void Skip_Click(object sender, RoutedEventArgs e) => Decide(TriageFlag.None);
    private void Rewind_Click(object sender, RoutedEventArgs e) => Rewind();
    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();

    // --- Review state -----------------------------------------------------------------

    private void Review_Click(object sender, RoutedEventArgs e) => ShowReviewState();

    private void BackToDeck_Click(object sender, RoutedEventArgs e) => ShowDeckState();

    private void ShowReviewState()
    {
        CardPreview.Clear(); // a playing video must not keep running under the review screen
        DeckPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Visible;
        RefreshReviewState();
    }

    private void RefreshReviewState()
    {
        UnmarkedStrip.Visibility = _vm.UnmarkedFileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnmarkedText.Text = $"{_vm.UnmarkedFileCount} file(s) not decided yet — they stay untouched unless you keep going.";
        CommitButton.IsEnabled = _vm.KeepCount + _vm.RejectCount > 0;
        CommitButton.Content = $"Commit  (✗ {_vm.RejectCount} recycle · ✓ {_vm.KeepCount} keep)…";
    }

    private static FileEntry? EntryOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FileEntry;

    private void SwapPile_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            _vm.SetFlag(entry, entry.Flag == TriageFlag.Keep ? TriageFlag.Reject : TriageFlag.Keep);
            RefreshReviewState();
        }
    }

    private void UnflagItem_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            _vm.SetFlag(entry, TriageFlag.None);
            RefreshReviewState();
        }
    }

    private void PileItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && EntryOf(sender) is { } entry)
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
    }

    // --- Commit -----------------------------------------------------------------------

    private void Commit_Click(object sender, RoutedEventArgs e)
    {
        var keepBytes = _vm.KeepPile.Sum(f => f.SizeBytes);
        var rejectBytes = _vm.RejectPile.Sum(f => f.SizeBytes);
        var dialog = new CommitDialog(_vm.RejectCount, rejectBytes, _vm.KeepCount, keepBytes)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
            return;
        var destination = dialog.KeepDestination;

        // Release every preview handle, then let the dispatcher pump the media teardown
        // before files start moving — same discipline as delete/rename (issue #1).
        CardPreview.Clear();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            var error = _vm.CommitTriage(destination);
            if (error is not null)
                MessageBox.Show(Window.GetWindow(this)!, error, "Commit finished with errors",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }));
    }
}
