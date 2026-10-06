using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voca.Models;
using Voca.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voca;

/// <summary>
/// Opened by Ctrl+Alt+V next to the mouse: the selected word with boxes for phonetics, part of speech,
/// meaning and example. "Lưu" saves it to "Từ của tôi" (into today's reviews); "Để AI điền sau" keeps it
/// waiting for the AI prompt. A word the library already has is shown with where it is, not added again.
/// </summary>
public partial class QuickAddWindow : Window
{
    private readonly Store _store;
    private readonly Action<string> _speak;
    private readonly Action<string> _saved;

    public QuickAddWindow(Store store, Action<string> speak, Action<string> saved)
    {
        InitializeComponent();
        _store = store;
        _speak = speak;
        _saved = saved;
    }

    /// <summary>Shows a word (or an empty box) with an optional note, e.g. that nothing was selected.</summary>
    public void Load(string? word, string? note)
    {
        WordBox.Text = word ?? "";
        PhoneticBox.Text = MeaningBox.Text = ExampleBox.Text = PosBox.Text = "";
        NoteText.Text = note ?? "";
        NoteText.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateState();
        Dispatcher.BeginInvoke(() =>
        {
            var box = WordBox.Text.Length == 0 || ExistingPanel.Visibility == Visibility.Visible ? WordBox : MeaningBox;
            box.Focus();
            if (box == WordBox) WordBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Places the window next to a screen point (in WPF units), kept inside the work area.</summary>
    public void PlaceNear(System.Windows.Point point)
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : 420;
        Left = Math.Clamp(point.X + 12, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        Top = Math.Clamp(point.Y + 16, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8));
    }

    private void WordBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateState();

    /// <summary>Already in the library → show where and its meaning, nothing to save; else the boxes.</summary>
    private void UpdateState()
    {
        if (ExistingPanel is null) return;
        var data = _store.Data;
        var clean = QuickAdd.Normalize(WordBox.Text);
        var existing = clean is null ? null : QuickAdd.FindInLibrary(data, clean);
        if (existing is var (plan, word))
        {
            ExistingPanel.Visibility = Visibility.Visible;
            FieldsPanel.Visibility = Visibility.Collapsed;
            ExistingText.Text = $"Đã có trong {plan.Name} · Ngày {word.Day}{(word.Learned ? " · đã thuộc" : "")} — không thêm trùng.";
            ExistingDetail.Text = string.Join("  ", new[] { word.Text, word.Phonetic, word.PartOfSpeech }.Where(t => t.Length > 0)) +
                                  $"\n{word.Meaning}" + (word.Example.Length > 0 ? $"\n“{word.Example}”" : "");
            SaveButton.IsEnabled = LaterButton.IsEnabled = false;
            CloseButton.Content = "Đóng";
            return;
        }
        ExistingPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = LaterButton.IsEnabled = clean is not null;
        CloseButton.Content = "Hủy";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        var clean = QuickAdd.Normalize(WordBox.Text);
        if (clean is null) { ShowError("Hãy nhập một từ hoặc cụm từ ngắn (tối đa 6 từ, không có chữ số)."); return; }
        if (MeaningBox.Text.Trim().Length == 0)
        {
            ShowError("Hãy ghi nghĩa tiếng Việt (hoặc bấm “Để AI điền sau”).");
            MeaningBox.Focus();
            return;
        }
        Word? saved = null;
        _store.Update(d => saved = QuickAdd.SaveWord(d, clean, PhoneticBox.Text, PosBox.Text, MeaningBox.Text,
            Regex.Replace(ExampleBox.Text, @"\s+", " "), DateTime.Now));
        if (saved is null) { UpdateState(); return; }
        _saved($"Đã lưu “{saved.Text}” — {saved.Meaning}. Từ này sẽ hiện để ôn ngay hôm nay.");
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        QuickAddResult? result = null;
        _store.Update(d => result = QuickAdd.Add(d, WordBox.Text, DateTime.Now));
        switch (result)
        {
            case null or { Invalid: true }:
                ShowError("Hãy nhập một từ hoặc cụm từ ngắn (tối đa 6 từ, không có chữ số).");
                return;
            case { ExistingPlan: not null }:
                UpdateState();
                return;
            default:
                _saved(result.AlreadyWaiting
                    ? $"“{result.Text}” đã có trong danh sách chờ AI điền nghĩa."
                    : $"Đã đưa “{result.Text}” vào danh sách chờ ({_store.Data.Inbox.Count} từ). Mở tab Từ của tôi để nhờ AI điền nghĩa.");
                Close();
                return;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (WordBox.Text.Trim().Length > 0) _speak(WordBox.Text.Trim());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && SaveButton.IsEnabled && !PosBox.IsDropDownOpen) { Save(); e.Handled = true; }
    }
}
