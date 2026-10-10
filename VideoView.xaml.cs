using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Voca;

/// <summary>
/// The "Phụ đề video" tab: recognises the speech of a video into an .srt saved next to it (or opens an .srt),
/// then optionally turns the words not in the library into a new plan via the usual AI-chat prompt.
/// Several videos picked at once are subtitled one after another (a queue). Switching tabs leaves a
/// recognition running; closing the library window cancels it.
/// </summary>
public partial class VideoView : System.Windows.Controls.UserControl
{
    private readonly Store _store;
    private string? _mediaPath;
    /// <summary>The video (or audio) to play with the subtitles: the file picked, or the one next to a picked .srt.</summary>
    private string? _videoPath;
    private List<SubtitleLine> _lines = [];
    private List<WordRow> _words = [];
    private CancellationTokenSource? _work;
    private Window? _host;
    private readonly ObservableCollection<QueueItem> _queue = [];

    private static readonly Brush MutedInk = Frozen(0x66, 0x70, 0x85), WorkInk = Frozen(0x6C, 0x5C, 0xE7),
        DoneInk = Frozen(0x06, 0x76, 0x47), ErrorInk = Frozen(0xB4, 0x23, 0x18);

    /// <summary>A video of the queue and what happened to it.</summary>
    private sealed class QueueItem(string path) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
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
        ModelBox.SelectedIndex = 0;
        QueueList.ItemsSource = _queue;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again on every return to the tab; hook the window once.
        if (_host is not null) return;
        _host = Window.GetWindow(this);
        if (_host is not null) _host.Closing += (_, _) => _work?.Cancel();
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
            Status("Bấm “Tạo phụ đề”.");
        }
        UpdateButtons();
    }

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".mp3", ".m4a", ".wav"];

    /// <summary>The video with the same name as a subtitle file, if one sits next to it.</summary>
    private static string? VideoNextTo(string srt) =>
        VideoExtensions.Select(ext => Path.ChangeExtension(srt, ext)).FirstOrDefault(File.Exists);

    /// <summary>Plays the video with the subtitles; new words of the video and words being learned stand out.</summary>
    private void PlayVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null || _lines.Count == 0) return;
        var newWords = Subtitles.NewWords(_store.Data, _lines);
        new VideoPlayerWindow(_store, _videoPath, _lines, newWords).Show();
    }

    private void ModelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is not SpeechModel model || StageText is null) return;
        StageText.Text = Transcriber.IsDownloaded(_store.Folder, model) ? "" : $"Lần đầu dùng model này sẽ tải về khoảng {model.SizeMb} MB.";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ModelBox.SelectedItem is not SpeechModel model) return;
        var queue = _queue.Count > 1;
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
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = _work is not null;
        var isMedia = _mediaPath is not null && !_mediaPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        RunButton.IsEnabled = !busy && (_queue.Count > 1 || isMedia);
        PlayVideoButton.IsEnabled = _videoPath is not null && !busy && _lines.Count > 0;
        SaveButton.IsEnabled = isMedia && !busy && _lines.Count > 0;
        OpenFolderButton.IsEnabled = _mediaPath is not null;
        FindWordsButton.IsEnabled = !busy && _lines.Count > 0;
        SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = _words.Count > 0;
        CopyPromptButton.IsEnabled = _words.Any(w => w.Picked) && PlanNameBox.Text.Trim().Length > 0;
    }

    // ---------------------------------- optional plan ----------------------------------

    private void FindWords_Click(object sender, RoutedEventArgs e)
    {
        _words = Subtitles.NewWords(_store.Data, _lines).Select(w => new WordRow(w)).ToList();
        WordsList.ItemsSource = _words;
        UpdateButtons();
        Status(_words.Count > 0
            ? $"{_words.Count} từ chưa có trong thư viện, từ xuất hiện nhiều nhất ở trên. Tích các từ muốn học rồi sao chép prompt."
            : "Mọi từ đáng học trong video đều đã có trong thư viện.");
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

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        var words = PickedWords;
        if (words.Count == 0) return;
        var prompt = Subtitles.BuildPrompt(PlanNameBox.Text.Trim(), words, PerDay);
        try
        {
            System.Windows.Clipboard.SetText(prompt);
            Status($"Đã sao chép prompt cho {words.Count} từ. Dán vào AI chat, rồi dán câu trả lời vào ô bên phải.");
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
        var (read, skipped) = Subtitles.ReadPlan(_store.Data, AnswerBox.Text, name, Math.Max(1, PickedWords.Count), PerDay);
        if (!read.CanImport)
        {
            Status("Chưa nhập được: " + string.Join(" ", read.Errors));
            return;
        }
        var plan = read.Plan!;
        if (_store.Data.Plans.Any(p => string.Equals(p.Name.Trim(), plan.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            plan.Name = $"{plan.Name} ({DateTime.Now:dd/MM HH:mm})";
        _store.Update(d =>
        {
            d.Plans.Add(plan);
            CourseEngine.AddToCourse(d, plan.Id);
        });
        AnswerBox.Clear();
        var added = new HashSet<string>(plan.Words.Select(w => w.Text.Trim().ToLowerInvariant()));
        _words.RemoveAll(w => added.Contains(w.Text) || w.Picked);
        WordsList.Items.Refresh();
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
