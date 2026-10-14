using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Voca.Models;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Voca;

/// <summary>
/// The "Phụ đề video" tab: recognises the speech of a video into an .srt saved next to it (or opens an .srt),
/// then optionally turns the words not in the library into a new plan via the usual AI-chat prompt — one word
/// set per video, saved as soon as its prompt is copied so other videos do not offer the same words.
/// Several videos picked at once are subtitled one after another (a queue). Switching tabs leaves a
/// recognition running; closing the library window cancels it.
/// </summary>
public partial class VideoView : System.Windows.Controls.UserControl
{
    private readonly Store _store;
    private string? _mediaPath;
    /// <summary>The video (or audio) to play with the subtitles: the file picked, or the one next to a picked .srt.</summary>
    private string? _videoPath;
    /// <summary>What a word set is tied to: the video, or the .srt when no video sits next to it.</summary>
    private string? Source => _videoPath ?? _mediaPath;
    private List<SubtitleLine> _lines = [];
    private List<WordRow> _words = [];
    private CancellationTokenSource? _work;
    private Window? _host;
    private readonly ObservableCollection<QueueItem> _queue = [];

    private static readonly Brush MutedInk = Frozen(0x66, 0x70, 0x85), WorkInk = Frozen(0x6C, 0x5C, 0xE7),
        DoneInk = Frozen(0x06, 0x76, 0x47), ErrorInk = Frozen(0xB4, 0x23, 0x18);

    /// <summary>A video of the queue and what happened to it.</summary>
    private sealed class QueueItem(string path, string? label = null) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        /// <summary>The file name, or for an episode of a film its short name and how far it was watched.</summary>
        public string Name => label ?? System.IO.Path.GetFileName(Path);
        public bool HasSubtitles => File.Exists(SrtPathFor(Path));
        public string State { get; private set; } = "";
        public Brush StateInk { get; private set; } = MutedInk;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(string state, Brush ink)
        {
            State = state;
            StateInk = ink;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateInk)));
        }
    }

    private sealed record LineRow(string Time, string Text);

    /// <summary>A film of the film folder, as listed in the "Phim" box.</summary>
    private sealed record FilmRow(string Name, string Text, List<PhoneVideo> Episodes);

    private bool _filmsLoading;

    private sealed class WordRow(VideoWord word)
    {
        public VideoWord Word { get; } = word;
        public string Text => Word.Text;
        public string Example => Word.Example;
        public string CountText => Word.Count > 1 ? $"{Word.Count}×" : "";
        public bool Picked { get; set; }
    }

    public VideoView(Store store)
    {
        InitializeComponent();
        _store = store;
        ModelBox.ItemsSource = Transcriber.Models;
        // A model already downloaded first, so nothing has to be fetched again.
        ModelBox.SelectedItem = Transcriber.Models.FirstOrDefault(m => Transcriber.IsDownloaded(store.Folder, m)) ?? Transcriber.Models[0];
        QueueList.ItemsSource = _queue;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        ShowContinue(); // also lists the films
        // Loaded fires again on every return to the tab; hook the window once.
        if (_host is not null) return;
        _host = Window.GetWindow(this);
        if (_host is not null) _host.Closing += (_, _) => _work?.Cancel();
    }

    // ---------------------------------- films ----------------------------------


    /// <summary>Fills the "Phim" box from the film folder (keeping the film chosen); says how to set it up when empty.</summary>
    private void LoadFilms()
    {
        _filmsLoading = true;
        var chosen = (FilmBox.SelectedItem as FilmRow)?.Name;
        var films = PhoneVideos.List(_store.Data, WatchProgress.Load(_store.Folder))
            .Where(v => v.Film is not null).GroupBy(v => v.Group).Select(g => g.ToList())
            .Select(episodes => new FilmRow(episodes[0].Film!, $"{episodes[0].Film}  ·  {FilmSummary(episodes)}", episodes)).ToList();
        if (films.Count == 0)
        {
            var folder = _store.Data.Settings.FilmFolder;
            FilmBox.ItemsSource = new[] { new FilmRow("", folder.Length == 0 ? "Chưa chọn thư mục phim — bấm “Thư mục phim…”" : $"{folder} chưa có phim nào (mỗi phim là một thư mục con)", []) };
            FilmBox.SelectedIndex = 0;
            FilmBox.IsEnabled = false;
        }
        else
        {
            FilmBox.ItemsSource = films;
            FilmBox.SelectedItem = films.FirstOrDefault(f => f.Name == chosen);
            FilmBox.IsEnabled = _work is null;
        }
        _filmsLoading = false;
        ShowFilmHint();
    }

    private void ShowFilmHint() => FilmHint.Visibility = FilmBox.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;

    private static string FilmSummary(List<PhoneVideo> episodes)
    {
        var watched = episodes.Count(v => v.Progress?.Finished == true);
        var subtitled = episodes.Count(v => v.Srt is not null);
        return episodes.Count == 1
            ? subtitled == 1 ? "có phụ đề" : "chưa có phụ đề"
            : $"{episodes.Count} tập · {subtitled} có phụ đề" + (watched > 0 ? $" · đã xem {watched}" : "");
    }

    /// <summary>An episode's line in the list: "E04 · đang xem 12:30", "E01 · đã xem ✓".</summary>
    private static string EpisodeText(PhoneVideo episode) =>
        episode.Label + (episode.Progress is not { } p ? ""
            : p.Finished ? "  ·  đã xem ✓"
            : $"  ·  đang xem {VideoPlayerWindow.Clock(TimeSpan.FromSeconds(p.Seconds))}");

    /// <summary>A film chosen: its episodes fill the list (subtitled ones marked); the one to watch next is opened.</summary>
    private void FilmBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowFilmHint();
        if (_filmsLoading || _work is not null || FilmBox.SelectedItem is not FilmRow { Episodes.Count: > 0 } film) return;
        _queue.Clear();
        foreach (var episode in film.Episodes) _queue.Add(new QueueItem(episode.Path, EpisodeText(episode)));
        RedoBox.IsChecked = false;
        ShowQueueStates();
        QueuePanel.Visibility = Visibility.Visible;
        // Where to pick up: the episode left half-watched, else the one after the last watched, else the first.
        var last = film.Episodes.Where(v => v.Progress is not null).MaxBy(v => v.Progress!.WatchedAt);
        var at = last is null ? 0 : film.Episodes.IndexOf(last) + (last.Progress!.Finished ? 1 : 0);
        QueueList.SelectedIndex = Math.Clamp(at, 0, _queue.Count - 1);
        QueueList.ScrollIntoView(QueueList.SelectedItem);
        var missing = _queue.Count(i => !i.HasSubtitles);
        Status($"{film.Name}: {_queue.Count} tập" + (missing > 0 ? $", {missing} tập chưa có phụ đề — bấm “Tạo phụ đề” để tạo lần lượt" : ", tập nào cũng có phụ đề")
               + ". Bấm một tập để xem phụ đề, rồi “▶ Xem video có phụ đề”.");
        UpdateButtons();
    }

    /// <summary>
    /// "Tạo phụ đề cả thư mục": every episode of every film without an .srt goes into the list (the film being
    /// watched first) and is recognised one after another, as when several videos are picked.
    /// </summary>
    private void ScanFilms_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        var history = WatchProgress.Load(_store.Folder);
        var films = PhoneVideos.List(_store.Data, history).Where(v => v.Film is not null).GroupBy(v => v.Group).ToList();
        if (films.Count == 0)
        {
            Status(_store.Data.Settings.FilmFolder.Length == 0 ? "Chưa chọn thư mục phim: bấm “Thư mục phim…”." : "Thư mục phim chưa có phim nào (mỗi phim là một thư mục con).");
            return;
        }
        var missing = films
            .OrderByDescending(g => g.Max(v => v.Progress?.WatchedAt ?? DateTime.MinValue))
            .SelectMany(g => g).Where(v => v.Srt is null).ToList();
        if (missing.Count == 0)
        {
            Status($"Đã quét {films.Count} phim: tập nào cũng có phụ đề.");
            return;
        }
        _filmsLoading = true;
        FilmBox.SelectedIndex = -1;
        _filmsLoading = false;
        _queue.Clear();
        foreach (var episode in missing)
            _queue.Add(new QueueItem(episode.Path, episode.Film == episode.Label ? episode.Film : $"{episode.Film} · {episode.Label}"));
        RedoBox.IsChecked = false;
        ShowQueueStates();
        QueuePanel.Visibility = Visibility.Visible;
        Status($"Đã quét {films.Count} phim: {missing.Count} tập chưa có phụ đề. Đang tạo lần lượt từng tập — bấm “Hủy” để dừng, lần sau bấm lại sẽ làm tiếp các tập còn thiếu.");
        Run_Click(sender, e);
    }

    /// <summary>
    /// "Tách phụ đề từ mkv": the English subtitles inside the .mkv files are written as their .srt, one after another,
    /// with each one's state in the list — the episodes listed (a film, or videos picked), or with nothing listed every
    /// episode of the film folder. Videos with an .srt are skipped unless "Tạo lại" is ticked; a video without English
    /// text subtitles inside is marked and skipped (Tạo phụ đề recognises its speech instead). No model is needed.
    /// </summary>
    private async void ExtractMkv_Click(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        if (_queue.Count == 0)
        {
            var episodes = PhoneVideos.List(_store.Data, WatchProgress.Load(_store.Folder)).Where(v => v.Film is not null).ToList();
            if (episodes.Count == 0)
            {
                Status(_store.Data.Settings.FilmFolder.Length == 0
                    ? "Chọn một phim hoặc video trước, hoặc chọn thư mục phim (“Thư mục phim…”) để tách cả thư mục."
                    : "Thư mục phim chưa có phim nào (mỗi phim là một thư mục con).");
                return;
            }
            foreach (var episode in episodes)
                _queue.Add(new QueueItem(episode.Path, episode.Film == episode.Label ? episode.Film : $"{episode.Film} · {episode.Label}"));
            ShowQueueStates();
            QueuePanel.Visibility = Visibility.Visible;
        }
        var redo = RedoBox.IsChecked == true;
        var todo = _queue.Where(i => Matroska.IsMatroska(i.Path) && (redo || !i.HasSubtitles)).ToList();
        if (todo.Count == 0)
        {
            Status(_queue.Any(i => Matroska.IsMatroska(i.Path))
                ? "Mọi file mkv trong danh sách đều đã có phụ đề .srt. Tích “Tạo lại cả video đã có phụ đề” để tách lại."
                : "Danh sách không có file mkv nào.");
            return;
        }

        _work = new CancellationTokenSource();
        var cancel = _work.Token;
        SetBusy(true);
        var started = DateTime.Now;
        int done = 0, none = 0, failed = 0;
        try
        {
            for (var n = 0; n < todo.Count; n++)
            {
                var item = todo[n];
                QueueList.SelectedItem = item;
                QueueList.ScrollIntoView(item);
                Progress.Value = n * 100.0 / todo.Count;
                StageText.Text = $"Đang tách phụ đề {n + 1}/{todo.Count}: {item.Name}";
                item.Set("Đang tách…", WorkInk);
                try
                {
                    var track = await Task.Run(() => EmbeddedEnglish(item.Path), cancel);
                    if (track is null)
                    {
                        item.Set("Không có phụ đề tiếng Anh trong video", MutedInk);
                        none++;
                        continue;
                    }
                    var lines = await ReadEmbeddedAsync(item.Path, track, cancel);
                    if (lines.Count == 0)
                    {
                        item.Set("Phụ đề trong video không có lời thoại", MutedInk);
                        none++;
                        continue;
                    }
                    File.WriteAllText(SrtPathFor(item.Path), Subtitles.ToSrt(lines), new System.Text.UTF8Encoding(false));
                    item.Set($"Xong · {lines.Count} dòng", DoneInk);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    item.Set("Lỗi: " + ex.Message, ErrorInk);
                    failed++;
                }
            }
            Progress.Value = 100;
            StageText.Text = $"Xong trong {Took(started)}.";
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Đã hủy.";
        }
        finally
        {
            _work.Dispose();
            _work = null;
            SetBusy(false);
        }
        Status($"Đã tách phụ đề cho {done}/{todo.Count} video"
               + (none > 0 ? $", {none} video không có phụ đề tiếng Anh bên trong (dùng “Tạo phụ đề” để nhận dạng giọng nói)" : "")
               + (failed > 0 ? $", {failed} video lỗi (xem dòng màu đỏ)" : "")
               + ". Các file .srt nằm cạnh từng video; bấm một tập để xem phụ đề hoặc phát video.");
    }

    private void FilmFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFilmFolder(_host, _store) is null) return;
        LoadFilms();
        if (FilmBox.IsEnabled && FilmBox.Items.Count > 0) FilmBox.SelectedIndex = 0;
    }

    /// <summary>Asks for the film folder (starting at the current one, or D:\Film) and saves it; null when cancelled.</summary>
    internal static string? PickFilmFolder(Window? owner, Store store)
    {
        var current = store.Data.Settings.FilmFolder;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục phim (mỗi thư mục con là một phim, tên thư mục là tên phim)",
            InitialDirectory = Directory.Exists(current) ? current : Directory.Exists(@"D:\Film") ? @"D:\Film" : ""
        };
        if (dialog.ShowDialog(owner) != true) return null;
        store.Update(d => d.Settings.FilmFolder = dialog.FolderName);
        return dialog.FolderName;
    }

    // ---------------------------------- subtitles ----------------------------------

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video, âm thanh hoặc phụ đề|*.mp4;*.m4v;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.mp3;*.m4a;*.wav;*.srt|Tất cả (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(_host) != true) return;
        var media = dialog.FileNames.Where(f => !f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToList();
        _queue.Clear();
        if (media.Count < 2)
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            OpenFile(media.FirstOrDefault() ?? dialog.FileName);
            return;
        }
        foreach (var file in media) _queue.Add(new QueueItem(file));
        RedoBox.IsChecked = false;
        ShowQueueStates();
        QueuePanel.Visibility = Visibility.Visible;
        QueueList.SelectedIndex = 0;
        var have = _queue.Count(i => i.HasSubtitles);
        Status($"{_queue.Count} video" + (have > 0 ? $", {have} video đã có phụ đề (sẽ bỏ qua)" : "")
               + ". Bấm “Tạo phụ đề” để tạo lần lượt từng video; bấm vào một video để xem phụ đề của nó.");
        UpdateButtons();
    }

    /// <summary>"Chờ" or "Đã có phụ đề" for each video not processed yet, depending on "Tạo lại".</summary>
    private void ShowQueueStates()
    {
        var redo = RedoBox.IsChecked == true;
        foreach (var item in _queue)
            if (item.HasSubtitles) item.Set(redo ? "Sẽ tạo lại" : "Đã có phụ đề", redo ? MutedInk : DoneInk);
            else item.Set("Chờ", MutedInk);
    }

    private void RedoBox_Click(object sender, RoutedEventArgs e)
    {
        ShowQueueStates();
        UpdateButtons();
    }

    private List<QueueItem> Pending => _queue.Where(i => RedoBox.IsChecked == true || !i.HasSubtitles).ToList();

    private void QueueList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // While the queue runs, the list shows progress; the video being recognised is on screen.
        if (_work is null && QueueList.SelectedItem is QueueItem item) OpenFile(item.Path);
    }

    /// <summary>Opens one file: a video (with its .srt when one sits next to it) or an .srt (with its video).</summary>
    private void OpenFile(string path)
    {
        _mediaPath = path;
        FileText.Text = Path.GetFileName(_mediaPath);
        PlanNameBox.Text = Path.GetFileNameWithoutExtension(_mediaPath);
        SetLines([]);
        var isSrt = _mediaPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        _videoPath = isSrt ? VideoNextTo(_mediaPath) : _mediaPath;
        var srt = isSrt ? _mediaPath : SrtPathFor(_mediaPath);
        if (File.Exists(srt))
        {
            try
            {
                SetLines(Subtitles.ReadSrt(File.ReadAllText(srt)));
                Status(_lines.Count == 0 ? "File .srt không có dòng phụ đề nào."
                    : isSrt && _videoPath is null ? $"Đã mở {_lines.Count} dòng phụ đề (không thấy video cùng tên bên cạnh để phát). Bấm “Tìm từ mới” để tạo bộ từ."
                    : isSrt ? $"Đã mở {_lines.Count} dòng phụ đề của {Path.GetFileName(_videoPath)}. Bấm “▶ Xem video có phụ đề” hoặc “Tìm từ mới”."
                    : $"Đã mở phụ đề có sẵn {Path.GetFileName(srt)} ({_lines.Count} dòng). Bấm “▶ Xem video có phụ đề”, hoặc “Tạo phụ đề” để tạo lại.");
            }
            catch (IOException ex) { Status($"Không đọc được file: {ex.Message}"); }
        }
        else
        {
            Status("Bấm “Tạo phụ đề” để Whisper nghe tiếng Anh của video và tạo phụ đề."
                   + (!isSrt && EmbeddedEnglish(_mediaPath) is not null ? " Video này cũng có sẵn phụ đề tiếng Anh bên trong: “Tách phụ đề từ mkv” lấy nó ngay." : ""));
        }
        ShowWordSets(loadPending: true);
        UpdateButtons();
    }

    /// <summary>The English text subtitles stored inside an .mkv, if any (for “Tách phụ đề từ mkv”).</summary>
    private static MkvTrack? EmbeddedEnglish(string path) =>
        Matroska.IsMatroska(path) ? Matroska.BestEnglishSubtitles(Matroska.ReadTracks(path)) : null;

    private static Task<List<SubtitleLine>> ReadEmbeddedAsync(string path, MkvTrack track, CancellationToken cancel = default) =>
        Task.Run(() => Matroska.ReadSubtitles(path, track.Number, cancel), cancel);

    /// <summary>
    /// Shows this video's word sets: one waiting for the AI (its words ticked, name and words per day restored
    /// when <paramref name="loadPending"/>) and the ones already imported.
    /// </summary>
    private void ShowWordSets(bool loadPending)
    {
        var parts = new List<string>();
        var pending = Source is null ? null : Subtitles.PendingSetFor(_store.Data, Source);
        if (pending is not null)
        {
            parts.Add($"📌 Bộ từ “{pending.Name}” của video này đang chờ AI ({pending.Words.Count} từ, lưu {pending.CreatedAt:dd/MM HH:mm}). " +
                      "Sao chép lại prompt nếu cần, rồi dán câu trả lời và bấm “Nhập vào khóa học”.");
            if (loadPending && _lines.Count > 0)
            {
                PlanNameBox.Text = pending.Name;
                PerDayBox.Text = pending.PerDay.ToString();
                ListWords(pending);
            }
        }
        if (Source is not null)
            foreach (var done in Subtitles.ImportedSetsFor(_store.Data, Source))
                parts.Add($"✓ Đã tạo bộ từ “{done.Name}” ({done.Words.Count} từ, {done.ImportedAt:dd/MM}) từ video này.");
        var others = _store.Data.VideoWordSets.Count(s => s.Pending && s != pending);
        if (others > 0) parts.Add($"Video khác có {others} bộ từ đang chờ AI — từ của chúng không được gợi ý lại ở đây.");
        SetStateText.Text = string.Join("\n", parts);
        SetPanel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DropSetButton.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DropSet_Click(object sender, RoutedEventArgs e)
    {
        if (Source is null || Subtitles.PendingSetFor(_store.Data, Source) is not { } set) return;
        _store.Update(d => Subtitles.DropPendingSet(d, set));
        foreach (var row in _words) row.Picked = false;
        WordsList.Items.Refresh();
        ShowWordSets(loadPending: false);
        UpdateButtons();
        Status($"Đã bỏ bộ từ đang chờ “{set.Name}”; {set.Words.Count} từ của nó lại được gợi ý cho mọi video.");
    }

    /// <summary>The words of this video not taken elsewhere; those of its own pending set come ticked.</summary>
    private void ListWords(VideoWordSet? pending)
    {
        var picked = pending?.Words.Select(w => w.Text.ToLowerInvariant()).ToHashSet() ?? [];
        _words = Subtitles.NewWords(_store.Data, _lines, pending)
            .Select(w => new WordRow(w) { Picked = picked.Contains(w.Text.ToLowerInvariant()) }).ToList();
        WordsList.ItemsSource = _words;
    }


    /// <summary>The video with the same name as a subtitle file, if one sits next to it.</summary>
    private static string? VideoNextTo(string srt) =>
        WatchProgress.MediaExtensions.Select(ext => Path.ChangeExtension(srt, ext)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Plays the video with the subtitles (from where it was left off last time); new words of the video and
    /// words being learned stand out.
    /// </summary>
    private void PlayVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null || _lines.Count == 0) return;
        Play(_videoPath, WatchProgress.ResumeAt(WatchProgress.Load(_store.Folder), _videoPath));
    }

    private void Play(string video, TimeSpan start)
    {
        var player = new VideoPlayerWindow(_store, video, _lines, Subtitles.NewWords(_store.Data, _lines), start);
        player.Closed += (_, _) => ShowContinue(); // the player saved where it stopped
        player.Show();
    }

    /// <summary>"Xem tiếp": the last video at the second it was left off, or the next episode once it was finished.</summary>
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (WatchProgress.Continue(WatchProgress.Load(_store.Folder)) is not { } next) { ShowContinue(); return; }
        _queue.Clear();
        QueuePanel.Visibility = Visibility.Collapsed;
        OpenFile(next.Video);
        Play(next.Video, next.Start); // plays without subtitles when the video has no .srt yet
    }

    private void ShowContinue()
    {
        if (IsLoaded) LoadFilms();
        var next = WatchProgress.Continue(WatchProgress.Load(_store.Folder));
        ContinueButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        if (next is null) return;
        var name = Path.GetFileNameWithoutExtension(next.Video);
        ContinueText.Text = next.NextEpisode ? $"Xem tập tiếp: {name}" : $"Xem tiếp: {name} · {VideoPlayerWindow.Clock(next.Start)}";
        ContinueButton.ToolTip = next.NextEpisode
            ? $"Tập trước đã xem hết. Mở tập tiếp theo trong thư mục:\n{next.Video}"
            : $"Mở lại video xem gần nhất, đúng chỗ đã dừng ({VideoPlayerWindow.Clock(next.Start)}):\n{next.Video}";
    }

    /// <summary>Opens the subtitles as a text to read; with none open yet, asks for an .srt first (no video needed).</summary>
    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Phụ đề SubRip (*.srt)|*.srt" };
            if (dialog.ShowDialog(_host) != true) return;
            _queue.Clear();
            QueuePanel.Visibility = Visibility.Collapsed;
            OpenFile(dialog.FileName);
            if (_lines.Count == 0) return;
        }
        new SubtitleReaderWindow(_store, Path.GetFileNameWithoutExtension(Source!), _lines, Subtitles.NewWords(_store.Data, _lines)).Show();
    }

    private void ModelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is not SpeechModel model || StageText is null) return;
        StageText.Text = Transcriber.IsDownloaded(_store.Folder, model) ? "" : $"Lần đầu dùng model này sẽ tải về khoảng {model.SizeMb} MB.";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ModelBox.SelectedItem is not SpeechModel model) return;
        var queue = _queue.Count > 0;
        if (!queue && _mediaPath is null) return;
        if (queue && Pending.Count == 0)
        {
            Status("Mọi video đều đã có phụ đề. Tích “Tạo lại cả video đã có phụ đề” nếu muốn tạo lại.");
            return;
        }
        if (!Transcriber.IsDownloaded(_store.Folder, model))
        {
            var ok = System.Windows.MessageBox.Show(_host!,
                $"Cần tải model nhận dạng giọng nói “{model.FileName}” (khoảng {model.SizeMb} MB, từ Hugging Face) một lần duy nhất. Tải ngay?",
                "Voca · Tải model", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
        }

        _work = new CancellationTokenSource();
        var cancel = _work.Token;
        SetBusy(true);
        try
        {
            if (!Transcriber.IsDownloaded(_store.Folder, model))
            {
                StageText.Text = $"Đang tải model {model.FileName}…";
                await Transcriber.DownloadAsync(_store.Folder, model,
                    new Progress<double>(p => { Progress.Value = p * 100; StageText.Text = $"Đang tải model {model.FileName}… {p:P0}"; }), cancel);
            }
            if (queue) await RunQueueAsync(model, cancel);
            else await RunOneAsync(model, _mediaPath!, cancel);
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Đã hủy.";
        }
        catch (Exception ex)
        {
            StageText.Text = "";
            Status($"Không tạo được phụ đề: {ex.Message}");
        }
        finally
        {
            _work.Dispose();
            _work = null;
            SetBusy(false);
        }
    }

    /// <summary>One video: recognised, then saved next to it (or where the learner chooses, if one exists).</summary>
    private async Task RunOneAsync(SpeechModel model, string path, CancellationToken cancel)
    {
        var started = DateTime.Now;
        var lines = await RecogniseAsync(model, path, "", null, cancel);
        StageText.Text = $"Xong {lines.Count} dòng trong {Took(started)}.";
        if (lines.Count == 0) { Status("Không nhận ra lời nói tiếng Anh nào trong video."); return; }
        var target = SrtPathFor(path);
        if (File.Exists(target)) Save_Click(this, new RoutedEventArgs());
        else SaveTo(target);
    }

    /// <summary>
    /// The queue: every video still to do, one after another (Whisper already uses every core). Each .srt is
    /// saved next to its video; a video that fails is marked and the queue goes on. Cancelling stops it all.
    /// </summary>
    private async Task RunQueueAsync(SpeechModel model, CancellationToken cancel)
    {
        var todo = Pending;
        var started = DateTime.Now;
        int done = 0, failed = 0;
        for (var n = 0; n < todo.Count; n++)
        {
            var item = todo[n];
            QueueList.SelectedItem = item;
            QueueList.ScrollIntoView(item);
            _mediaPath = _videoPath = item.Path;
            FileText.Text = item.Name;
            PlanNameBox.Text = Path.GetFileNameWithoutExtension(item.Path);
            item.Set("Đang tách âm thanh…", WorkInk);
            var itemStarted = DateTime.Now;
            try
            {
                var lines = await RecogniseAsync(model, item.Path, $"Video {n + 1}/{todo.Count} · ",
                    p => item.Set($"Đang nhận dạng… {p}%", WorkInk), cancel);
                if (lines.Count == 0)
                {
                    item.Set("Không nhận ra lời nói", ErrorInk);
                    failed++;
                    continue;
                }
                File.WriteAllText(SrtPathFor(item.Path), Subtitles.ToSrt(lines), new System.Text.UTF8Encoding(false));
                item.Set($"Xong · {lines.Count} dòng · {Took(itemStarted)}", DoneInk);
                done++;
            }
            catch (OperationCanceledException)
            {
                item.Set("Đã hủy", ErrorInk);
                Status($"Đã dừng hàng đợi: xong {done}/{todo.Count} video.");
                throw;
            }
            catch (Exception ex)
            {
                item.Set("Lỗi: " + ex.Message, ErrorInk);
                failed++;
            }
        }
        StageText.Text = $"Xong hàng đợi trong {Took(started)}.";
        Status($"Đã tạo phụ đề cho {done}/{todo.Count} video" + (failed > 0 ? $", {failed} video không tạo được (xem dòng màu đỏ)" : "")
               + ". Các file .srt nằm cạnh từng video; bấm vào một video để xem phụ đề, phát video hoặc tìm từ mới.");
    }

    /// <summary>Recognises one file, showing the lines as they come; <paramref name="prefix"/> says which video of the queue.</summary>
    private async Task<List<SubtitleLine>> RecogniseAsync(SpeechModel model, string path, string prefix, Action<int>? percent, CancellationToken cancel)
    {
        Progress.Value = 0;
        SetLines([]);
        var found = new List<SubtitleLine>();
        var lines = await Transcriber.TranscribeAsync(_store.Folder, model, path,
            new Progress<string>(text => StageText.Text = prefix + text),
            new Progress<int>(p => { Progress.Value = p; StageText.Text = $"{prefix}Đang nhận dạng giọng nói… {p}%"; percent?.Invoke(p); }),
            new Progress<SubtitleLine>(line => { found.Add(line); SetLines(found, scroll: true); }),
            cancel);
        SetLines(lines);
        return lines;
    }

    private static string Took(DateTime started)
    {
        var took = DateTime.Now - started;
        return took.TotalMinutes < 1 ? $"{took.TotalSeconds:0} giây" : $"{took.TotalMinutes:0.#} phút";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0 || _mediaPath is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Phụ đề SubRip (*.srt)|*.srt",
            InitialDirectory = Path.GetDirectoryName(_mediaPath),
            FileName = Path.GetFileName(SrtPathFor(_mediaPath))
        };
        if (dialog.ShowDialog(_host) == true) SaveTo(dialog.FileName);
    }

    private void SaveTo(string path)
    {
        try
        {
            File.WriteAllText(path, Subtitles.ToSrt(_lines), new System.Text.UTF8Encoding(false));
            Status($"Đã lưu phụ đề: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"Không lưu được phụ đề: {ex.Message}");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPath is null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_mediaPath}\""); }
        catch (Exception ex) { Status($"Không mở được thư mục: {ex.Message}"); }
    }

    /// <summary>Players load "video.srt" next to "video.mp4" by themselves.</summary>
    private static string SrtPathFor(string media) => Path.ChangeExtension(media, ".srt");

    private void SetLines(List<SubtitleLine> lines, bool scroll = false)
    {
        _lines = lines;
        var rows = lines.Select(l => new LineRow(Subtitles.SrtTime(l.Start)[..8], l.Text)).ToList();
        LinesList.ItemsSource = rows;
        if (scroll && rows.Count > 0) LinesList.ScrollIntoView(rows[^1]);
        if (!scroll)
        {
            _words = [];
            WordsList.ItemsSource = null;
        }
        UpdateButtons();
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PickButton.IsEnabled = ModelBox.IsEnabled = RedoBox.IsEnabled = !busy;
        if (!busy) LoadFilms(); else FilmBox.IsEnabled = false;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = _work is not null;
        var isMedia = _mediaPath is not null && !_mediaPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        RunButton.IsEnabled = !busy && (_queue.Count > 0 || isMedia);
        ScanFilmsButton.IsEnabled = ExtractButton.IsEnabled = !busy;
        PlayVideoButton.IsEnabled = _videoPath is not null && !busy && _lines.Count > 0;
        ReadButton.IsEnabled = !busy;
        SaveButton.IsEnabled = isMedia && !busy && _lines.Count > 0;
        OpenFolderButton.IsEnabled = _mediaPath is not null;
        FindWordsButton.IsEnabled = !busy && _lines.Count > 0;
        SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = _words.Count > 0;
        CopyPromptButton.IsEnabled = _words.Any(w => w.Picked) && PlanNameBox.Text.Trim().Length > 0;
    }

    // ---------------------------------- optional plan ----------------------------------

    private void FindWords_Click(object sender, RoutedEventArgs e)
    {
        ListWords(Source is null ? null : Subtitles.PendingSetFor(_store.Data, Source));
        UpdateButtons();
        Status(_words.Count > 0
            ? $"{_words.Count} từ mới, từ xuất hiện nhiều nhất ở trên. Tích các từ muốn học rồi sao chép prompt (bộ từ được lưu riêng cho video này)."
            : "Mọi từ đáng học trong video đều đã có trong thư viện, Từ của tôi hoặc bộ từ của video khác.");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Pick(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => Pick(false);

    private void Pick(bool picked)
    {
        foreach (var row in _words) row.Picked = picked;
        WordsList.Items.Refresh();
        UpdateButtons();
        Status(PickedText());
    }

    private void WordBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateButtons();
        Status(PickedText());
    }

    private string PickedText()
    {
        var picked = _words.Count(w => w.Picked);
        return picked == 0 ? "Chưa chọn từ nào." : $"Đã chọn {picked} từ · {Days(picked)} ngày học.";
    }

    private void PlanInput_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (CopyPromptButton is not null) UpdateButtons();
    }

    private int PerDay => int.TryParse(PerDayBox.Text, out var n) ? Math.Clamp(n, 5, PlanFormat.MaxWordsPerDay) : 20;
    private int Days(int words) => Math.Max(1, (words + PerDay - 1) / PerDay);
    private List<VideoWord> PickedWords => _words.Where(w => w.Picked).Select(w => w.Word).ToList();

    /// <summary>
    /// Saves the ticked words as this video's word set (so no other video offers them again), then copies the
    /// prompt for them. Words another video or the library took in the meantime are dropped and named.
    /// </summary>
    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickedWords;
        if (picked.Count == 0 || Source is null) return;
        VideoWordSet? set = null;
        List<string> taken = [];
        _store.Update(d => (set, taken) = Subtitles.SaveWordSet(d, Source, PlanNameBox.Text, picked, PerDay, DateTime.Now));
        if (taken.Count > 0)
        {
            _words.RemoveAll(w => taken.Contains(w.Text));
            WordsList.Items.Refresh();
        }
        ShowWordSets(loadPending: false);
        UpdateButtons();
        var takenNote = taken.Count > 0 ? $" Bỏ {taken.Count} từ đã có ở nơi khác: {string.Join(", ", taken.Take(8))}." : "";
        if (set is null)
        {
            Status("Không còn từ nào để tạo bộ từ." + takenNote);
            return;
        }
        var prompt = Subtitles.BuildPrompt(set.Name, Subtitles.WordsOf(set), set.PerDay);
        try
        {
            System.Windows.Clipboard.SetText(prompt);
            Status($"Đã lưu bộ từ của video này ({set.Words.Count} từ) và sao chép prompt. Dán vào AI chat, rồi dán câu trả lời vào ô bên phải.{takenNote}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            AnswerBox.Text = prompt;
            AnswerBox.Focus();
            AnswerBox.SelectAll();
            Status("Không sao chép tự động được (clipboard đang bận). Prompt đã được đặt và bôi đen trong ô trả lời — nhấn Ctrl+C rồi xóa đi trước khi dán câu trả lời.");
        }
    }

    private void OpenAi_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Status($"Không mở được trình duyệt: {ex.Message}"); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var name = PlanNameBox.Text.Trim();
        if (name.Length == 0) { Status("Nhập tên bộ từ."); PlanNameBox.Focus(); return; }
        var set = Source is null ? null : Subtitles.PendingSetFor(_store.Data, Source);
        var (read, skipped) = Subtitles.ReadPlan(_store.Data, AnswerBox.Text, name, Math.Max(1, set?.Words.Count ?? PickedWords.Count), PerDay, set);
        if (!read.CanImport)
        {
            Status("Chưa nhập được: " + string.Join(" ", read.Errors));
            return;
        }
        var plan = read.Plan!;
        if (_store.Data.Plans.Any(p => string.Equals(p.Name.Trim(), plan.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            plan.Name = $"{plan.Name} ({DateTime.Now:dd/MM HH:mm})";
        var picked = PickedWords;
        _store.Update(d =>
        {
            d.Plans.Add(plan);
            CourseEngine.AddToCourse(d, plan.Id);
            // The set records which video the plan came from (also when the prompt was not copied from here).
            if (set is null && Source is not null && picked.Count > 0) set = Subtitles.SaveWordSet(d, Source, plan.Name, picked, PerDay, DateTime.Now).Set;
            if (set is not null) Subtitles.MarkImported(set, plan, DateTime.Now);
        });
        AnswerBox.Clear();
        var added = new HashSet<string>(plan.Words.Select(w => w.Text.Trim().ToLowerInvariant()));
        _words.RemoveAll(w => added.Contains(w.Text) || w.Picked);
        WordsList.Items.Refresh();
        ShowWordSets(loadPending: false);
        UpdateButtons();
        var notes = string.Join(" ", read.Warnings.Take(2));
        Status($"Đã thêm “{plan.Name}” ({plan.DayCount} ngày, {plan.Words.Count} từ) vào cuối khóa học."
               + (skipped.Count > 0 ? $" Bỏ qua {skipped.Count} từ đã có: {string.Join(", ", skipped.Take(8))}." : "")
               + (notes.Length > 0 ? " " + notes : ""));
    }

    private void Status(string text) => StatusText.Text = text;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
