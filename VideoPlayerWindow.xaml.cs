using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
/// Windows' own player is used until the VLC player is downloaded (<see cref="VlcRuntime"/>); from then on VLC plays
/// every video, which also lets the learner switch the audio track of a film with several (English / Japanese).
/// The offer to download it appears when Windows cannot play a video or when the video has several audio tracks.
/// Subtitles stored inside an .mkv can be picked as the line shown, instead of the .srt.
/// The control bar lies over the bottom of the video and fades out, with the cursor, when the mouse rests while
/// the video plays; moving the mouse (or a key) brings it back, and it stays while paused.
/// </summary>
public partial class VideoPlayerWindow : Window
{
    private static readonly Brush NewInk = Frozen(0xFF, 0xC9, 0x4D), LearningInk = Frozen(0xB9, 0xAE, 0xFF), WaitingInk = Frozen(0x7E, 0xE2, 0xA8);

    private readonly Store _store;
    private readonly string _name;
    private readonly string _videoPath;
    /// <summary>The subtitles the video was opened with (its .srt), and their new words.</summary>
    private readonly IReadOnlyList<SubtitleLine> _givenLines;
    private readonly IReadOnlyList<VideoWord> _givenNewWords;
    private IReadOnlyList<SubtitleLine> _lines;
    private IReadOnlyList<VideoWord> _newWords;
    /// <summary>The tracks of an .mkv (empty for other files).</summary>
    private readonly IReadOnlyList<MkvTrack> _tracks;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    /// <summary>Saves the position while playing, so a crash or power cut loses a few seconds at most.</summary>
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    /// <summary>Hides the control bar once the mouse has rested this long.</summary>
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private WordMarker _marker;
    private SubtitleLine? _shown;
    private bool _playing, _seeking, _pausedByHover, _fullScreen, _finished, _listing, _audioListed, _closed, _controlsHidden;
    private System.Windows.Point _mouse;
    /// <summary>Where to jump once the video is open (where it was left off last time).</summary>
    private TimeSpan _start;
    private WindowState _stateBeforeFullScreen;
    /// <summary>The VLC player, once downloaded; null while Windows' player (<see cref="Media"/>) plays the video.</summary>
    private VlcPlayback? _vlc;
    private CancellationTokenSource? _install;
    private int _subtitleLoad;

    private sealed record SubtitleChoice(string Label, int Track); // Track 0: the subtitles the video was opened with

    public VideoPlayerWindow(Store store, string videoPath, IReadOnlyList<SubtitleLine> lines, IReadOnlyList<VideoWord> newWords,
        TimeSpan start = default)
    {
        InitializeComponent();
        _store = store;
        _lines = _givenLines = lines.OrderBy(l => l.Start).ToList();
        _newWords = _givenNewWords = newWords;
        _marker = new WordMarker(store.Data, newWords);
        _name = Path.GetFileNameWithoutExtension(videoPath);
        _videoPath = videoPath;
        _start = start;
        _tracks = Matroska.IsMatroska(videoPath) ? Matroska.ReadTracks(videoPath) : [];
        Title = $"Voca · {Path.GetFileName(videoPath)}";
        _tick.Tick += (_, _) => Update();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _saveTimer.Tick += (_, _) => SaveProgress();
        _idleTimer.Tick += (_, _) => HideControls();
        if (!VlcRuntime.IsInstalled(store.Folder) || !StartVlc()) Media.Source = new Uri(videoPath);
        ListSubtitles();
        // MediaElement only opens its source once it is part of a shown window.
        ContentRendered += (_, _) => Play();
    }

    // ---------------------------------- playback ----------------------------------

    private TimeSpan Position
    {
        get => _vlc?.Position ?? Media.Position;
        set { if (_vlc is not null) _vlc.Position = value; else Media.Position = value; }
    }

    private TimeSpan? Length => _vlc is not null ? _vlc.Length
        : Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : null;

    private void Play()
    {
        if (_vlc is not null) _vlc.Play();
        else Media.Play();
        _playing = true;
        _finished = false;
        _pausedByHover = false;
        PlayButton.Content = "⏸";
        _tick.Start();
        _saveTimer.Start();
        ShowControls();
    }

    private void Pause()
    {
        if (_vlc is not null) _vlc.Pause();
        else Media.Pause();
        _playing = false;
        _saveTimer.Stop();
        PlayButton.Content = "▶";
        Update();
        ShowControls();
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
        if (Length is not { } length) return; // never opened (or failed): nothing to resume
        WatchProgress.Record(_store.Folder, _videoPath, at ?? Position, length, DateTime.Now);
    }

    private void SeekTo(TimeSpan time)
    {
        var length = Length ?? TimeSpan.MaxValue;
        Position = time < TimeSpan.Zero ? TimeSpan.Zero : time > length ? length : time;
        Update();
        ShowControls();
    }

    private void Media_Opened(object sender, RoutedEventArgs e) => Opened();

    private void Opened()
    {
        Seek.Maximum = Length?.TotalSeconds ?? 1;
        if (_start > TimeSpan.Zero)
        {
            Position = _start;
            ShowToast($"Xem tiếp từ {Clock(_start)} · ← lùi 5 giây, kéo thanh thời gian để xem từ đầu");
            _start = TimeSpan.Zero;
        }
        ListAudio();
        Update();
    }

    private void Media_Ended(object sender, RoutedEventArgs e) => Ended();

    private void Ended()
    {
        Pause();
        if (Length is { } length) SaveProgress(length);
        _finished = true;
        Position = TimeSpan.Zero;
    }

    private void Media_Failed(object? sender, ExceptionRoutedEventArgs e) => Failed(e.ErrorException.Message);

    private void Failed(string message)
    {
        _tick.Stop();
        if (_vlc is null)
        {
            OfferVlc($"Windows không phát được video này ({message}).\n\nBộ phát VLC phát được hầu hết mọi video (mkv, video 10-bit, HEVC, âm thanh AC3…) " +
                     $"và đổi được tiếng khi phim có nhiều tiếng. Tải về một lần, khoảng {VlcRuntime.DownloadMb} MB.");
            return;
        }
        SubtitlePanel.Visibility = Visibility.Visible;
        SubtitleText.Inlines.Clear();
        SubtitleText.Text = message;
    }

    /// <summary>Moves the seek bar and time, and shows the line for the current position.</summary>
    private void Update()
    {
        var now = Position;
        var total = Length ?? TimeSpan.Zero;
        if (total > TimeSpan.Zero && Seek.Maximum != total.TotalSeconds) Seek.Maximum = total.TotalSeconds;
        if (!_seeking) Seek.Value = now.TotalSeconds;
        TimeText.Text = $"{Clock(now)} / {Clock(total)}";
        ShowLine(Subtitles.LineAt(_lines, now));
        if (_vlc is { IsOpen: true } && !_audioListed) ListAudio(); // libVLC may learn the tracks a moment after it starts
    }

    internal static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ---------------------------------- VLC ----------------------------------

    /// <summary>Plays the video with VLC from now on; false (Windows' player stays) when libVLC cannot be loaded.</summary>
    private bool StartVlc()
    {
        try
        {
            _vlc = new VlcPlayback(_store.Folder, Dispatcher, bitmap => Picture.Source = bitmap);
        }
        catch (Exception ex)
        {
            ShowToast($"Không mở được bộ phát VLC ({ex.Message}), dùng trình phát của Windows.");
            return false;
        }
        _vlc.Opened += Opened;
        _vlc.Ended += Ended;
        _vlc.Failed += Failed;
        _vlc.Open(_videoPath);
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var speed))
            _vlc.Speed = speed;
        Media.Visibility = Visibility.Collapsed;
        Picture.Visibility = Visibility.Visible;
        AudioOfferButton.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OfferVlc(string text)
    {
        VlcText.Text = text;
        VlcProgress.Visibility = Visibility.Collapsed;
        VlcInstallButton.IsEnabled = true;
        VlcCloseButton.Content = "Để sau";
        VlcPanel.Visibility = Visibility.Visible;
    }

    private void AudioOffer_Click(object sender, RoutedEventArgs e)
    {
        var audio = _tracks.Where(t => t.Kind == MkvTrackKind.Audio).Select(t => t.Label).ToList();
        OfferVlc($"Video này có {audio.Count} tiếng: {string.Join(", ", audio)}. Trình phát của Windows chỉ phát được một tiếng và không đổi được.\n\n" +
                 $"Bộ phát VLC đổi được tiếng ngay khi đang xem (và phát được hầu hết mọi video). Tải về một lần, khoảng {VlcRuntime.DownloadMb} MB.");
    }

    /// <summary>Downloads VLC, then carries on with it from where the video is.</summary>
    private async void VlcInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_install is not null) return;
        _install = new CancellationTokenSource();
        VlcInstallButton.IsEnabled = false;
        VlcCloseButton.Content = "Hủy";
        VlcProgress.Value = 0;
        VlcProgress.Visibility = Visibility.Visible;
        VlcText.Text = "Đang tải bộ phát VLC…";
        try
        {
            await VlcRuntime.InstallAsync(_store.Folder,
                new Progress<double>(p => { VlcProgress.Value = p; VlcText.Text = $"Đang tải bộ phát VLC… {p:P0}"; }), _install.Token);
        }
        catch (OperationCanceledException)
        {
            VlcPanel.Visibility = Visibility.Collapsed;
            return;
        }
        catch (Exception ex)
        {
            VlcText.Text = $"Không tải được bộ phát VLC: {ex.Message}";
            VlcProgress.Visibility = Visibility.Collapsed;
            VlcInstallButton.IsEnabled = true;
            VlcInstallButton.Content = "Thử lại";
            VlcCloseButton.Content = "Để sau";
            return;
        }
        finally
        {
            _install?.Dispose();
            _install = null;
        }
        if (_closed) return;
        VlcPanel.Visibility = Visibility.Collapsed;
        SwitchToVlc();
    }

    private void SwitchToVlc()
    {
        var at = Length is not null ? Position : _start;
        if (_playing) Pause();
        Media.Close();
        Media.Source = null;
        _start = TimeSpan.Zero;
        if (!StartVlc()) return;
        _vlc!.Position = at;
        SubtitlePanel.Visibility = Visibility.Collapsed;
        Play();
        ShowToast("Đã chuyển sang bộ phát VLC" + (_tracks.Count(t => t.Kind == MkvTrackKind.Audio) > 1 ? " · chọn tiếng ở ô bên phải thanh điều khiển (phím A)" : ""));
    }

    private void VlcClose_Click(object sender, RoutedEventArgs e)
    {
        if (_install is not null) _install.Cancel();
        else VlcPanel.Visibility = Visibility.Collapsed;
    }

    private void VlcPanel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true; // not a click on the video

    // ---------------------------------- audio tracks ----------------------------------

    /// <summary>Fills the audio box from VLC (shown when there is more than one track); with Windows' player, offers VLC instead.</summary>
    private void ListAudio()
    {
        if (_vlc is null)
        {
            AudioOfferButton.Visibility = _tracks.Count(t => t.Kind == MkvTrackKind.Audio) > 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var choices = _vlc.AudioTracks(_tracks);
        _audioListed = choices.Count > 0;
        _listing = true;
        AudioBox.ItemsSource = choices;
        AudioBox.SelectedItem = choices.FirstOrDefault(c => c.Id == _vlc.AudioTrack);
        _listing = false;
        AudioBox.Visibility = choices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AudioBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (_vlc is not null && _vlc.AudioTracks(_tracks).Count != AudioBox.Items.Count) ListAudio();
    }

    private void AudioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_listing || _vlc is null || AudioBox.SelectedItem is not AudioChoice choice || choice.Id == _vlc.AudioTrack) return;
        _vlc.AudioTrack = choice.Id;
        ShowToast($"Tiếng: {choice.Label}");
    }

    /// <summary>Key A: the next audio track.</summary>
    private void NextAudio()
    {
        if (_vlc is null) { if (AudioOfferButton.Visibility == Visibility.Visible) AudioOffer_Click(this, new RoutedEventArgs()); return; }
        if (AudioBox.Items.Count < 2) return;
        AudioBox.SelectedIndex = (AudioBox.SelectedIndex + 1) % AudioBox.Items.Count;
    }

    // ---------------------------------- subtitle tracks ----------------------------------

    /// <summary>
    /// The subtitles to choose from: the .srt the video was opened with and the text subtitles inside an .mkv. With
    /// no .srt, the English subtitles inside the video (dialogue rather than signs) are shown.
    /// </summary>
    private void ListSubtitles()
    {
        var choices = new List<SubtitleChoice>();
        if (_givenLines.Count > 0) choices.Add(new SubtitleChoice("Phụ đề .srt", 0));
        var inside = _tracks.Where(t => t.IsText).ToList();
        choices.AddRange(inside.Zip(MkvTrack.ShortLabels(inside), (t, label) => new SubtitleChoice(label, t.Number)));
        if (choices.Count == 0) return;
        var best = Matroska.BestEnglishSubtitles(_tracks)?.Number;
        var first = _givenLines.Count > 0 ? choices[0] : choices.FirstOrDefault(c => c.Track == best);
        _listing = true;
        SubtitleBox.ItemsSource = choices;
        SubtitleBox.SelectedItem = first;
        _listing = false;
        SubtitleBox.Visibility = choices.Count > 1 || first is null ? Visibility.Visible : Visibility.Collapsed;
        if (first is { Track: > 0 }) _ = LoadSubtitlesAsync(first);
    }

    private void SubtitleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_listing && SubtitleBox.SelectedItem is SubtitleChoice choice) _ = LoadSubtitlesAsync(choice);
    }

    private async Task LoadSubtitlesAsync(SubtitleChoice choice)
    {
        var load = ++_subtitleLoad;
        if (choice.Track == 0)
        {
            SetLines(_givenLines, _givenNewWords);
            return;
        }
        try
        {
            var lines = await Task.Run(() => Matroska.ReadSubtitles(_videoPath, choice.Track));
            if (load != _subtitleLoad || _closed) return; // another choice came after this one
            SetLines(lines, Subtitles.NewWords(_store.Data, lines));
            ShowToast(lines.Count == 0 ? "Track phụ đề này không có dòng chữ nào." : $"Phụ đề: {choice.Label} · {lines.Count} dòng");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (load == _subtitleLoad) ShowToast($"Không đọc được phụ đề này: {ex.Message}");
        }
    }

    private void SetLines(IReadOnlyList<SubtitleLine> lines, IReadOnlyList<VideoWord> newWords)
    {
        _lines = lines.OrderBy(l => l.Start).ToList();
        _newWords = newWords;
        _marker = new WordMarker(_store.Data, newWords);
        ShowLine(Subtitles.LineAt(_lines, Position), force: true);
    }

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

    // ---------------------------------- control bar ----------------------------------

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // WPF also raises MouseMove when the cursor or the layout changes under a still mouse.
        var at = e.GetPosition(this);
        if (at == _mouse) return;
        _mouse = at;
        ShowControls();
    }

    /// <summary>Shows the control bar and the cursor, and starts counting again until they hide.</summary>
    private void ShowControls()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
        if (!_controlsHidden) return;
        _controlsHidden = false;
        ControlBar.IsHitTestVisible = true;
        Cursor = null;
        FadeControls();
    }

    /// <summary>Hides the control bar and the cursor while the video plays, unless the learner is using the bar.</summary>
    private void HideControls()
    {
        _idleTimer.Stop();
        if (_controlsHidden || !_playing || _seeking || ControlBar.IsMouseOver || VlcPanel.Visibility == Visibility.Visible
            || SpeedBox.IsDropDownOpen || AudioBox.IsDropDownOpen || SubtitleBox.IsDropDownOpen) return;
        _controlsHidden = true;
        ControlBar.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.None;
        FadeControls();
    }

    private void FadeControls() => ControlBar.BeginAnimation(OpacityProperty,
        new DoubleAnimation(_controlsHidden ? 0 : 1, TimeSpan.FromMilliseconds(_controlsHidden ? 300 : 120)));

    /// <summary>
    /// The subtitle stays just above the control bar, shown or not: were it to move with the bar, it could slide under
    /// a still mouse and pause the video (hover) each time the bar hides.
    /// </summary>
    private void ControlBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SubtitlePanel.Margin = new Thickness(40, 0, 40, ControlBar.ActualHeight + 12);

    // ---------------------------------- controls ----------------------------------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Stage_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void Back_Click(object sender, RoutedEventArgs e) => SeekTo(Position - TimeSpan.FromSeconds(5));

    /// <summary>Back to the start of the line on screen, or of the one before when already there.</summary>
    private void PreviousLine_Click(object sender, RoutedEventArgs e)
    {
        var now = Position;
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
        if (SpeedBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var speed)) return;
        if (_vlc is not null) _vlc.Speed = speed;
        else Media.SpeedRatio = speed;
    }

    private void SubtitleToggle_Click(object sender, RoutedEventArgs e) => ShowLine(Subtitles.LineAt(_lines, Position), force: true);

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
        // The arrow keys and Space belong to an open drop-down list.
        if (AudioBox.IsDropDownOpen || SubtitleBox.IsDropDownOpen || SpeedBox.IsDropDownOpen) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(Position - TimeSpan.FromSeconds(5)); break;
            case Key.Right: SeekTo(Position + TimeSpan.FromSeconds(5)); break;
            case Key.F: SetFullScreen(!_fullScreen); break;
            case Key.Escape when _fullScreen: SetFullScreen(false); break;
            case Key.R: Read_Click(this, e); break;
            case Key.C: SubtitleToggle.IsChecked = SubtitleToggle.IsChecked != true; SubtitleToggle_Click(this, e); break;
            case Key.A: NextAudio(); break;
            default: return;
        }
        e.Handled = true;
        ShowControls();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        if (!_finished) SaveProgress();
        _install?.Cancel();
        _saveTimer.Stop();
        _tick.Stop();
        _toastTimer.Stop();
        _idleTimer.Stop();
        _vlc?.Dispose();
        Media.Close();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
