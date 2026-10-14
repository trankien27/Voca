using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voca;

/// <summary>
/// The "Xem phim" tab: the films of the film folder (most recently watched first) and the episodes of the one chosen,
/// each with how far it was watched and whether it has subtitles; an episode plays in <see cref="VideoPlayerWindow"/>
/// from where it was left off. "Xem tiếp" opens the last video at that second, or the next episode. Making subtitles
/// is the "Phụ đề video" tab's job; this tab only watches.
/// </summary>
public partial class FilmsView : System.Windows.Controls.UserControl
{
    private static readonly Brush HasInk = Frozen(0x06, 0x76, 0x47), MissingInk = Frozen(0x98, 0xA2, 0xB3);

    private readonly Store _store;
    private bool _loading;

    private sealed record FilmRow(string Name, string Status, string Summary, List<EpisodeRow> Episodes);

    private sealed record EpisodeRow(PhoneVideo Video)
    {
        public string Label => Video.Label.Length > 0 ? Video.Label : Path.GetFileNameWithoutExtension(Video.Path);
        public string WatchText => Video.Progress is not { } p ? "" : p.Finished ? "đã xem ✓" : $"đang xem {VideoPlayerWindow.Clock(TimeSpan.FromSeconds(p.Seconds))}";
        public double Watched => Video.Progress is { LengthSeconds: > 0 } p ? Math.Clamp(p.Finished ? 1 : p.Seconds / p.LengthSeconds, 0, 1) : 0;
        public string SubtitleText => HasSubtitles ? "có phụ đề" : Matroska.IsMatroska(Video.Path) ? "phụ đề trong mkv" : "chưa có phụ đề";
        public Brush SubtitleInk => HasSubtitles ? HasInk : MissingInk;
        public bool HasSubtitles => Video.Srt is not null;
    }

    public FilmsView(Store store)
    {
        InitializeComponent();
        _store = store;
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Lists the films again (episodes, progress and subtitles may have changed), keeping the film and episode chosen.</summary>
    private void Refresh()
    {
        _loading = true;
        var chosenFilm = (FilmsList.SelectedItem as FilmRow)?.Name;
        var chosenEpisode = (EpisodesList.SelectedItem as EpisodeRow)?.Video.Path;
        var films = PhoneVideos.List(_store.Data, WatchProgress.Load(_store.Folder))
            .Where(v => v.Film is not null).GroupBy(v => v.Group).Select(g => g.ToList())
            .Select(episodes => (Episodes: episodes, Last: episodes.Where(v => v.Progress is not null).MaxBy(v => v.Progress!.WatchedAt)))
            .OrderByDescending(f => f.Last?.Progress!.WatchedAt ?? DateTime.MinValue)
            .Select(f => new FilmRow(f.Episodes[0].Film!, FilmStatus(f.Episodes, f.Last), FilmSummary(f.Episodes), f.Episodes.Select(v => new EpisodeRow(v)).ToList()))
            .ToList();
        FilmsList.ItemsSource = films;
        var folder = _store.Data.Settings.FilmFolder;
        EmptyText.Text = folder.Length == 0 ? "Chưa chọn thư mục phim: bấm “Thư mục phim…” (ví dụ D:\\Film, mỗi thư mục con là một phim)."
            : films.Count == 0 ? $"{folder} chưa có phim nào (mỗi phim là một thư mục con)." : "";
        EmptyText.Visibility = films.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
        FilmsList.SelectedItem = films.FirstOrDefault(f => f.Name == chosenFilm) ?? films.FirstOrDefault();
        if (chosenEpisode is not null && EpisodesList.ItemsSource is List<EpisodeRow> rows && rows.FirstOrDefault(r => r.Video.Path == chosenEpisode) is { } again)
            EpisodesList.SelectedItem = again;
        if (films.Count == 0) ShowFilm(null);
        ShowContinue();
    }

    private static string FilmSummary(List<PhoneVideo> episodes)
    {
        var watched = episodes.Count(v => v.Progress?.Finished == true);
        var subtitled = episodes.Count(v => v.Srt is not null);
        return episodes.Count == 1
            ? subtitled == 1 ? "có phụ đề" : "chưa có phụ đề"
            : $"{episodes.Count} tập · {subtitled} có phụ đề" + (watched > 0 ? $" · đã xem {watched}" : "");
    }

    /// <summary>"Đang xem E04 · 12:30", "Tiếp theo: E05", "Đã xem hết", or "Chưa xem".</summary>
    private static string FilmStatus(List<PhoneVideo> episodes, PhoneVideo? last)
    {
        if (last?.Progress is not { } progress) return "Chưa xem";
        var single = episodes.Count == 1;
        if (!progress.Finished) return single ? $"Đang xem · {VideoPlayerWindow.Clock(TimeSpan.FromSeconds(progress.Seconds))}"
            : $"Đang xem {last.Label} · {VideoPlayerWindow.Clock(TimeSpan.FromSeconds(progress.Seconds))}";
        var at = episodes.IndexOf(last);
        return at + 1 < episodes.Count ? $"Tiếp theo: {episodes[at + 1].Label}" : single ? "Đã xem" : "Đã xem hết";
    }

    private void FilmsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loading) ShowFilm(FilmsList.SelectedItem as FilmRow);
    }

    /// <summary>A film's episodes, the one to watch next chosen: the one left half-watched, else the one after the last watched.</summary>
    private void ShowFilm(FilmRow? film)
    {
        EpisodesList.ItemsSource = film?.Episodes;
        FilmTitle.Text = film is null ? "" : $"{film.Name}  ·  {film.Summary}";
        if (film is { Episodes.Count: > 0 })
        {
            var last = film.Episodes.Where(r => r.Video.Progress is not null).MaxBy(r => r.Video.Progress!.WatchedAt);
            var at = last is null ? 0 : film.Episodes.IndexOf(last) + (last.Video.Progress!.Finished ? 1 : 0);
            EpisodesList.SelectedIndex = Math.Clamp(at, 0, film.Episodes.Count - 1);
            EpisodesList.ScrollIntoView(EpisodesList.SelectedItem);
        }
        UpdateButtons();
    }

    private EpisodeRow? Episode => EpisodesList.SelectedItem as EpisodeRow;

    private void EpisodesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var episode = Episode;
        PlayButton.IsEnabled = FromStartButton.IsEnabled = OpenFolderButton.IsEnabled = episode is not null;
        ReadButton.IsEnabled = episode?.HasSubtitles == true;
        PlayButton.Content = episode?.Video.Progress is { Finished: false, Seconds: > 5 } p
            ? $"Xem tiếp từ {VideoPlayerWindow.Clock(TimeSpan.FromSeconds(p.Seconds))}" : "Xem tập này";
        if (episode is not null)
            Status(episode.HasSubtitles ? $"{episode.Label}: bấm “{PlayButton.Content}” hoặc bấm đúp để xem."
                : $"{episode.Label} chưa có file phụ đề .srt" + (Matroska.IsMatroska(episode.Video.Path) ? " — khi xem sẽ hiện phụ đề tiếng Anh có sẵn trong video (nếu có)." : ".")
                  + " Tạo phụ đề ở tab Phụ đề video.");
    }

    // ---------------------------------- watching ----------------------------------

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is { } episode) Play(episode.Video.Path, WatchProgress.ResumeAt(WatchProgress.Load(_store.Folder), episode.Video.Path));
    }

    private void FromStart_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is { } episode) Play(episode.Video.Path, TimeSpan.Zero);
    }

    private void EpisodesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ListItemFor(source) is not null) Play_Click(sender, e);
    }

    /// <summary>The list item a double click landed on, so a click on the scroll bar does not start a video.</summary>
    private static System.Windows.Controls.ListBoxItem? ListItemFor(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.ListBoxItem item) return item;
        return null;
    }

    private void EpisodesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Play_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>Plays a video with its .srt (none: the player shows the English subtitles inside an .mkv, if any).</summary>
    private void Play(string video, TimeSpan start)
    {
        List<SubtitleLine> lines = [];
        var srt = Path.ChangeExtension(video, ".srt");
        if (File.Exists(srt))
            try { lines = Subtitles.ReadSrt(File.ReadAllText(srt)); }
            catch (IOException ex) { Status($"Không đọc được phụ đề: {ex.Message}"); }
        var player = new VideoPlayerWindow(_store, video, lines, Subtitles.NewWords(_store.Data, lines), start);
        player.Closed += (_, _) => Refresh(); // the player saved where it stopped
        player.Show();
    }

    /// <summary>"Xem tiếp": the last video at the second it was left off, or the next episode once it was finished.</summary>
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (WatchProgress.Continue(WatchProgress.Load(_store.Folder)) is not { } next) { ShowContinue(); return; }
        Play(next.Video, next.Start);
    }

    private void ShowContinue()
    {
        var next = WatchProgress.Continue(WatchProgress.Load(_store.Folder));
        ContinueButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        if (next is null) return;
        var name = Path.GetFileNameWithoutExtension(next.Video);
        ContinueText.Text = next.NextEpisode ? $"Xem tập tiếp: {name}" : $"Xem tiếp: {name} · {VideoPlayerWindow.Clock(next.Start)}";
        ContinueButton.ToolTip = next.NextEpisode
            ? $"Tập trước đã xem hết. Mở tập tiếp theo trong thư mục:\n{next.Video}"
            : $"Mở lại video xem gần nhất, đúng chỗ đã dừng ({VideoPlayerWindow.Clock(next.Start)}):\n{next.Video}";
    }

    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is not { HasSubtitles: true } episode) return;
        try
        {
            var lines = Subtitles.ReadSrt(File.ReadAllText(episode.Video.Srt!));
            new SubtitleReaderWindow(_store, Path.GetFileNameWithoutExtension(episode.Video.Path), lines, Subtitles.NewWords(_store.Data, lines)).Show();
        }
        catch (IOException ex) { Status($"Không đọc được phụ đề: {ex.Message}"); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Episode is not { } episode) return;
        try { Process.Start("explorer.exe", $"/select,\"{episode.Video.Path}\""); }
        catch (Exception ex) { Status($"Không mở được thư mục: {ex.Message}"); }
    }

    private void FilmFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VideoView.PickFilmFolder(Window.GetWindow(this), _store) is null) return;
        FilmsList.SelectedItem = null;
        Refresh();
    }

    private void Status(string text) => StatusText.Text = text;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
