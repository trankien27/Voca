using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Voca.Models;
using Voca.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voca;

/// <summary>
/// The "Từ của tôi" tab: words added with Ctrl+Alt+V (or typed here) wait for meanings; one prompt asks an
/// AI for all of them, and the pasted answer becomes a new day of the plan "Từ của tôi", ready to study.
/// </summary>
public partial class MyWordsView : System.Windows.Controls.UserControl
{
    private readonly Store _store;
    private readonly AppActions _actions;
    private List<Word> _justImported = [];

    private sealed record WaitingRow(string Text, string Added, string Context)
    {
        public Visibility ContextVisibility => Context.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string? ContextTip => Context.Length > 0 ? $"Câu gặp từ: {Context}" : null;
    }

    public MyWordsView(Store store, AppActions actions)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _store.Changed += OnStoreChanged;
        HotkeyWarning.Visibility = _actions.QuickAddHotkeyReady() ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e) => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged() => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        var inbox = _store.Data.Inbox.OrderByDescending(i => i.AddedAt).ToList();
        WaitingTitle.Text = $"Đang chờ điền nghĩa ({inbox.Count})";
        WaitingList.ItemsSource = inbox.Select(i => new WaitingRow(i.Text, i.AddedAt.ToString("HH:mm dd/MM"), i.Context)).ToList();
        CopyPromptButton.IsEnabled = inbox.Count > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddTyped();

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTyped();
        e.Handled = true;
    }

    private void AddTyped()
    {
        QuickAddResult? result = null;
        _store.Update(d => result = QuickAdd.Add(d, AddBox.Text, DateTime.Now));
        StatusText.Text = result switch
        {
            null or { Invalid: true } => "Hãy nhập một từ hoặc cụm từ ngắn (tối đa 6 từ).",
            { ExistingPlan: { } plan, ExistingWord: { } word } =>
                $"“{result.Text}” đã có trong {plan.Name} · Ngày {word.Day}{(word.Learned ? " (đã thuộc)" : "")}. Không thêm trùng.",
            { AlreadyWaiting: true } => $"“{result.Text}” đã có trong danh sách chờ.",
            _ => $"Đã thêm “{result.Text}”."
        };
        if (result is { Added: true }) AddBox.Clear();
        AddBox.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var picked = WaitingList.SelectedItems.OfType<WaitingRow>().Select(r => r.Text).ToList();
        if (picked.Count == 0) { StatusText.Text = "Hãy chọn từ cần xóa trong danh sách chờ."; return; }
        _store.Update(d => { foreach (var text in picked) QuickAdd.Remove(d, text); });
        StatusText.Text = $"Đã xóa {picked.Count} từ khỏi danh sách chờ.";
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        var words = _store.Data.Inbox.OrderBy(i => i.AddedAt).ToList();
        if (words.Count == 0) return;
        var prompt = QuickAdd.BuildPrompt(words);
        var count = Math.Min(words.Count, QuickAdd.MaxPromptWords);
        try
        {
            System.Windows.Clipboard.SetText(prompt);
            StatusText.Text = $"Đã sao chép prompt cho {count} từ" +
                              (words.Count > count ? $" (trong {words.Count} từ đang chờ — nhập xong bấm lại để làm tiếp phần còn lại)" : "") +
                              ". Dán vào AI (Ctrl+V), copy câu trả lời rồi bấm Dán kết quả.";
        }
        catch (Exception)
        {
            AnswerBox.Text = prompt;
            AnswerBox.SelectAll();
            AnswerBox.Focus();
            StatusText.Text = "Không sao chép được (clipboard đang bận). Prompt đã được đặt vào ô bên phải — Ctrl+C rồi xóa đi trước khi dán câu trả lời.";
        }
    }

    private void OpenAi_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Không mở được trình duyệt: {ex.Message}"; }
    }

    /// <summary>Puts the AI's answer from the clipboard in the box and imports it in one click.</summary>
    private void PasteAnswer_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
        catch (Exception) { StatusText.Text = "Không đọc được clipboard (đang bận). Bấm vào ô bên trên, Ctrl+V rồi bấm Nhập nội dung trong ô."; return; }
        if (text.Trim().Length == 0) { StatusText.Text = "Clipboard đang trống — hãy copy câu trả lời của AI trước."; return; }
        AnswerBox.Text = text;
        Import();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Import();

    private void Import()
    {
        if (AnswerBox.Text.Trim().Length == 0) { StatusText.Text = "Dán câu trả lời của AI vào ô trước."; return; }
        MyWordsImport? result = null;
        _store.Update(d => result = QuickAdd.Import(d, AnswerBox.Text, DateTime.Now));
        if (result!.Errors.Count > 0)
        {
            ImportResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            ImportResult.Text = "Chưa nhập được: " + string.Join(" ", result.Errors.Take(3)) + " Sửa trong ô rồi bấm Nhập nội dung trong ô.";
            return;
        }
        _justImported = result.Added;
        ImportResult.Foreground = (System.Windows.Media.Brush)FindResource("Ink");
        ImportResult.Text = (result.Added.Count > 0 ? $"✓ Đã thêm {result.Added.Count} từ vào “{QuickAdd.PlanName}” (ngày {result.Day})." : "Không có từ mới để thêm.") +
                            (result.SkippedExisting.Count > 0 ? $" Bỏ qua {result.SkippedExisting.Count} từ đã có trong thư viện: {string.Join(", ", result.SkippedExisting)}." : "") +
                            (result.StillWaiting.Count > 0 ? $"\nVẫn chờ {result.StillWaiting.Count} từ: {string.Join("; ", result.StillWaiting)}." : "");
        PracticeButton.Visibility = result.Added.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PracticeButton.Content = $"▶ Học ngay {result.Added.Count} từ";
        if (result.Added.Count > 0) AnswerBox.Clear();
    }

    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (_justImported.Count == 0) return;
        _actions.Practice(_justImported, $"{QuickAdd.PlanName} · {_justImported.Count} từ mới", SessionMode.NewWords);
    }
}
