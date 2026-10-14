using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voca;

/// <summary>
/// Plays a video with its subtitles on top. New words of the video are bold yellow (click: save to "Từ của
/// tôi"), words being learned bold violet (hover: meaning), saved words green. Hovering the subtitle pauses
/// so it can be read; "Đọc phụ đề" opens all of them as a text to read. Where the video stands is saved every
/// few seconds while it plays and on pause, close and end, so it resumes at that second next time (even after
/// a crash) — see <see cref="WatchProgress"/>.
/// </summary>
public partial class VideoPlayerWindow : Window
{
    private static readonly Brush NewInk = Frozen(0xFF, 0xC9, 0x4D), LearningInk = Frozen(0xB9, 0xAE, 0xFF), WaitingInk = Frozen(0x7E, 0xE2, 0xA8);

    private readonly Store _store;
    private readonly string _name;
    private readonly string _videoPath;
    private readonly IReadOnlyList<SubtitleLine> _lines;
    private readonly IReadOnlyList<VideoWord> _newWords;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    /// <summary>Saves the position while playing, so a crash or power cut loses a few seconds at most.</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private WordMarker _marker;
    private SubtitleLine? _shown;
    private bool _playing, _seeking, _pausedByHover, _fullScreen, _finished;
    /// <summary>Where to jump once the video is open (where it was left off last time).</summary>
    private TimeSpan _start;
    private WindowState _stateBeforeFullScreen;

    public VideoPlayerWindow(Store store, string videoPath, IReadOnlyList<SubtitleLine> lines, IReadOnlyList<VideoWord> newWords,
        TimeSpan start = default)
    {
        InitializeComponent();
        _store = store;
        _lines = lines.OrderBy(l => l.Start).ToList();
        _newWords = newWords;
        _marker = new WordMarker(store.Data, newWords);
        _name = Path.GetFileNameWithoutExtension(videoPath);
        _videoPath = videoPath;
        _start = start;
        Title = $"Voca · {Path.GetFileName(videoPath)}";
        _tick.Tick += (_, _) => Update();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _saveTimer.Tick += (_, _) => SaveProgress();
        Media.Source = new Uri(videoPath);
        // MediaElement only opens its source once it is part of a shown window.
        ContentRendered += (_, _) => Play();
    }

    // ---------------------------------- playback ----------------------------------

    private void Play()
    {
        Media.Play();
        _playing = true;
        _finished = false;
        _pausedByHover = false;
        PlayButton.Content = "⏸";
        _tick.Start();
        _saveTimer.Start();
    }

    private void Pause()
    {
        Media.Pause();
        _playing = false;
        _saveTimer.Stop();
        PlayButton.Content = "▶";
        Update();
    }

    private void TogglePlay()
    {
        if (!_playing) { Play(); return; }
        Pause();
        SaveProgress();
    }

    /// <summary>Remembers where the video stands, for the next time it is played and for "Xem tiếp".</summary>
    private void SaveProgress(TimeSpan? at = null)
    {
        if (!Media.NaturalDuration.HasTimeSpan) return; // never opened (or failed): nothing to resume
        WatchProgress.Record(_store.Folder, _videoPath, at ?? Media.Position, Media.NaturalDuration.TimeSpan, DateTime.Now);
    }

    private void SeekTo(TimeSpan time)
    {
        var length = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.MaxValue;
        Media.Position = time < TimeSpan.Zero ? TimeSpan.Zero : time > length ? length : time;
        Update();
    }

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        Seek.Maximum = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 1;
        if (_start > TimeSpan.Zero)
        {
            Media.Position = _start;
            ShowToast($"Xem tiếp từ {Clock(_start)} · ← lùi 5 giây, kéo thanh thời gian để xem từ đầu");
            _start = TimeSpan.Zero;
        }
        Update();
    }

    private void Media_Ended(object sender, RoutedEventArgs e)
    {
        Pause();
        if (Media.NaturalDuration.HasTimeSpan) SaveProgress(Media.NaturalDuration.TimeSpan);
        _finished = true;
        Media.Position = TimeSpan.Zero;
    }

    private void Media_Failed(object? sender, ExceptionRoutedEventArgs e)
    {
        _tick.Stop();
        SubtitlePanel.Visibility = Visibility.Visible;
        SubtitleText.Inlines.Clear();
        SubtitleText.Text = $"Không phát được video này: {e.ErrorException.Message}\nThử chuyển video sang mp4 (H.264).";
    }

    /// <summary>Moves the seek bar and time, and shows the line for the current position.</summary>
    private void Update()
    {
        var now = Media.Position;
        if (!_seeking) Seek.Value = now.TotalSeconds;
        var total = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Clock(now)} / {Clock(total)}";
        ShowLine(Subtitles.LineAt(_lines, now));
    }

    internal static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------------------------------- subtitle ----------------------------------

    private void ShowLine(SubtitleLine? line, bool force = false)
    {
        if (line == _shown && !force) return;
        _shown = line;
        if (line is null || SubtitleToggle.IsChecked != true)
        {
            // Keep the line while the learner reads it with the mouse on it.
            if (!_pausedByHover) SubtitlePanel.Visibility = Visibility.Collapsed;
            return;
        }
        SubtitleText.Inlines.Clear();
        foreach (var part in Subtitles.Mark(line.Text, _marker))
        {
            var run = new Run(part.Text) { Tag = part };
            switch (part.Mark)
            {
                case WordMark.New:
                    run.FontWeight = FontWeights.Bold; run.Foreground = NewInk; run.Cursor = System.Windows.Input.Cursors.Hand;
                    run.ToolTip = "Từ mới — bấm để lưu vào “Từ của tôi”";
                    break;
                case WordMark.Learning:
                    run.FontWeight = FontWeights.Bold; run.Foreground = LearningInk;
                    run.ToolTip = $"{part.Word!.Text} {part.Word.Phonetic} — {part.Word.Meaning}".Trim();
                    break;
                case WordMark.Waiting:
                    run.FontWeight = FontWeights.Bold; run.Foreground = WaitingInk;
                    run.ToolTip = "Đã lưu trong “Từ của tôi”, chờ điền nghĩa";
                    break;
            }
            SubtitleText.Inlines.Add(run);
        }
        SubtitlePanel.Visibility = Visibility.Visible;
    }

    /// <summary>A new word clicked in the subtitle goes to "Từ của tôi" (waiting for its meaning).</summary>
    private void Subtitle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.OriginalSource is not Run { Tag: SubtitlePart { Mark: WordMark.New } part }) return;
        ShowToast(SaveNewWord(_store, part.Text));
        _marker = new WordMarker(_store.Data, _newWords);
        ShowLine(_shown, force: true);
    }

    /// <summary>Saves a new word of a video to "Từ của tôi" (waiting for its meaning) and says what happened.</summary>
    internal static string SaveNewWord(Store store, string text)
    {
        QuickAddResult? result = null;
        store.Update(d => result = QuickAdd.Add(d, text, DateTime.Now));
        return QuickAdd.Describe(result!, text);
    }

    private void Subtitle_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_playing) return;
        Pause();
        _pausedByHover = true;
    }

    private void Subtitle_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pausedByHover) Play();
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SubtitleText.FontSize = Math.Clamp(ActualHeight / 24, 18, 44);

    // ---------------------------------- controls ----------------------------------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Stage_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void Back_Click(object sender, RoutedEventArgs e) => SeekTo(Media.Position - TimeSpan.FromSeconds(5));

    /// <summary>Back to the start of the line on screen, or of the one before when already there.</summary>
    private void PreviousLine_Click(object sender, RoutedEventArgs e)
    {
        var now = Media.Position;
        var start = _lines.LastOrDefault(l => l.Start < now - TimeSpan.FromSeconds(1))?.Start ?? TimeSpan.Zero;
        SeekTo(start);
        if (!_playing) Play();
    }

    /// <summary>Pauses and opens every line of the video as a text to read.</summary>
    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause();
        new SubtitleReaderWindow(_store, _name, _lines, _newWords).Show();
    }

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        SeekTo(TimeSpan.FromSeconds(Seek.Value));
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var speed))
            Media.SpeedRatio = speed;
    }

    private void SubtitleToggle_Click(object sender, RoutedEventArgs e) => ShowLine(Subtitles.LineAt(_lines, Media.Position), force: true);

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // re-maximizing after the style change covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(Media.Position - TimeSpan.FromSeconds(5)); break;
            case Key.Right: SeekTo(Media.Position + TimeSpan.FromSeconds(5)); break;
            case Key.F: SetFullScreen(!_fullScreen); break;
            case Key.Escape when _fullScreen: SetFullScreen(false); break;
            case Key.R: Read_Click(this, e); break;
            case Key.C: SubtitleToggle.IsChecked = SubtitleToggle.IsChecked != true; SubtitleToggle_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (!_finished) SaveProgress();
        _saveTimer.Stop();
        _tick.Stop();
        _toastTimer.Stop();
        Media.Close();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
