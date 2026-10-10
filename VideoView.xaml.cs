using System.Diagnostics;
using System.IO;
using System.Windows;
using Voca.Services;

namespace Voca;

/// <summary>
/// The "Phụ đề video" tab: recognises the speech of a video into an .srt saved next to it (or opens an .srt),
/// then optionally turns the words not in the library into a new plan via the usual AI-chat prompt.
/// Switching tabs leaves a recognition running; closing the library window cancels it.
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
            Filter = "Video, âm thanh hoặc phụ đề|*.mp4;*.m4v;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.mp3;*.m4a;*.wav;*.srt|Tất cả (*.*)|*.*"
        };
        if (dialog.ShowDialog(_host) != true) return;
        _mediaPath = dialog.FileName;
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
        if (_mediaPath is null || ModelBox.SelectedItem is not SpeechModel model) return;
        if (!Transcriber.IsDownloaded(_store.Folder, model))
        {
            var ok = System.Windows.MessageBox.Show(_host!,
                $"Cần tải model nhận dạng giọng nói “{model.FileName}” (khoảng {model.SizeMb} MB, từ Hugging Face) một lần duy nhất. Tải ngay?",
                "Voca · Tải model", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
        }

        _work = new CancellationTokenSource();
        var cancel = _work.Token;
        var started = DateTime.Now;
        SetLines([]);
        SetBusy(true);
        try
        {
            if (!Transcriber.IsDownloaded(_store.Folder, model))
            {
                StageText.Text = $"Đang tải model {model.FileName}…";
                await Transcriber.DownloadAsync(_store.Folder, model,
                    new Progress<double>(p => { Progress.Value = p * 100; StageText.Text = $"Đang tải model {model.FileName}… {p:P0}"; }), cancel);
            }
            Progress.Value = 0;
            var found = new List<SubtitleLine>();
            var lines = await Transcriber.TranscribeAsync(_store.Folder, model, _mediaPath,
                new Progress<string>(text => StageText.Text = text),
                new Progress<int>(p => { Progress.Value = p; StageText.Text = $"Đang nhận dạng giọng nói… {p}%"; }),
                new Progress<SubtitleLine>(line => { found.Add(line); SetLines(found, scroll: true); }),
                cancel);
            SetLines(lines);
            var minutes = (DateTime.Now - started).TotalMinutes;
            StageText.Text = $"Xong {lines.Count} dòng trong {(minutes < 1 ? $"{(DateTime.Now - started).TotalSeconds:0} giây" : $"{minutes:0.#} phút")}.";
            if (lines.Count == 0) { Status("Không nhận ra lời nói tiếng Anh nào trong video."); return; }
            var target = SrtPathFor(_mediaPath);
            if (File.Exists(target)) Save_Click(sender, e);
            else SaveTo(target);
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
        PickButton.IsEnabled = ModelBox.IsEnabled = !busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var busy = _work is not null;
        var isMedia = _mediaPath is not null && !_mediaPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        RunButton.IsEnabled = isMedia && !busy;
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
}
