using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voca.Models;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Voca;

/// <summary>
/// The "Tất cả từ" tab of the library: every word of every plan in one table, with its status,
/// right/wrong counts and next review. Search, filter and sort; speak, practise or open a word.
/// </summary>
public partial class AllWordsView : System.Windows.Controls.UserControl
{
    private static readonly Brush KnownWash = Frozen(0xE7, 0xF7, 0xEE), KnownInk = Frozen(0x06, 0x76, 0x47);
    private static readonly Brush LongWash = Frozen(0xD1, 0xFA, 0xDF), LongInk = Frozen(0x05, 0x60, 0x3A);
    private static readonly Brush DueWash = Frozen(0xFF, 0xF6, 0xED), DueInk = Frozen(0xB5, 0x47, 0x08);
    private static readonly Brush LearningWash = Frozen(0xEE, 0xEC, 0xFF), LearningInk = Frozen(0x4B, 0x3C, 0xC4);
    private static readonly Brush NewWash = Frozen(0xF2, 0xF4, 0xF7), NewInk = Frozen(0x66, 0x70, 0x85);

    private readonly Store _store;
    private readonly AppActions _actions;
    private readonly Action<Plan, Word> _openInEditor;
    private bool _ready;

    private sealed record PlanChoice(Guid? Id, string Name);

    public sealed record WordRow(WordEntry Entry, string Text, string Phonetic, string Pos, string Meaning, string Where,
        string WhereTip, string Status, Brush Wash, Brush Ink, int StatusOrder, string Score, int Forgot, string Next, DateTime NextDate);

    public AllWordsView(Store store, AppActions actions, Action<Plan, Word> openInEditor)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
        _openInEditor = openInEditor;
        _ready = true;
    }

    // Listen for changes only while the tab is shown (switching tabs unloads it).
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e) => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged() => Dispatcher.Invoke(Refresh);

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Render();
    }

    private IEnumerable<WordRow> SelectedRows => WordsGrid.SelectedItems.OfType<WordRow>();

    private void Refresh()
    {
        var data = _store.Data;
        var keep = (PlanFilterBox.SelectedItem as PlanChoice)?.Id;
        var choices = new List<PlanChoice> { new(null, "Mọi lộ trình") };
        choices.AddRange(AllWords.PlansInOrder(data).Select(p => new PlanChoice(p.Id, p.Name)));
        _ready = false;
        PlanFilterBox.ItemsSource = choices;
        PlanFilterBox.SelectedItem = choices.FirstOrDefault(c => c.Id == keep) ?? choices[0];
        _ready = true;

        var all = AllWords.Find(data, DateTime.Today);
        var seen = all.Count(e => e.Status != WordStatus.New || e.Word.IntroducedOn is not null);
        var known = all.Count(e => e.Status is WordStatus.Learned or WordStatus.Mastered);
        var due = all.Count(e => e.Status == WordStatus.Due);
        SummaryText.Text = $"{all.Count} từ · đã gặp {seen} · đã thuộc / nhớ lâu {known} · đến hạn ôn {due}";
        Render();
    }

    private void Render()
    {
        var data = _store.Data;
        var today = DateTime.Today;
        var keep = SelectedRows.Select(r => r.Entry.Word.Id).ToHashSet();
        var entries = AllWords.Find(data, today, SearchBox.Text,
            ChosenTag<WordFilter>(StatusBox), (PlanFilterBox.SelectedItem as PlanChoice)?.Id, ChosenTag<WordSort>(SortBox));
        var rows = entries.Select(e => Row(e, today)).ToList();
        WordsGrid.ItemsSource = rows;
        foreach (var row in rows.Where(r => keep.Contains(r.Entry.Word.Id))) WordsGrid.SelectedItems.Add(row);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WordsGrid.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowCount();
    }

    private static T ChosenTag<T>(System.Windows.Controls.ComboBox box) where T : struct, Enum =>
        (box.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse<T>(tag, out var value) ? value : default;

    private static WordRow Row(WordEntry entry, DateTime today)
    {
        var (word, plan) = (entry.Word, entry.Plan);
        var (status, wash, ink) = entry.Status switch
        {
            WordStatus.Learned => ("Đã thuộc", KnownWash, KnownInk),
            WordStatus.Mastered => ("Nhớ lâu", LongWash, LongInk),
            WordStatus.Due => ("Đến hạn ôn", DueWash, DueInk),
            WordStatus.Learning => ("Đang học", LearningWash, LearningInk),
            _ => ("Chưa học", NewWash, NewInk),
        };
        var title = plan.DayTitle(word.Day);
        var where = $"Ngày {word.Day} · {plan.Name}";
        var whereTip = $"{plan.Name}\nNgày {word.Day}{(title.Length > 0 ? " · " + title : "")}";
        var answered = word.Review is not null || word.CorrectCount > 0 || word.ForgotCount > 0;
        var score = answered ? $"{word.CorrectCount} / {word.ForgotCount}" : "";
        var due = word.Review?.Due.Date;
        var next = word.Learned || due is null ? "" : due <= today ? "hôm nay" : due == today.AddDays(1) ? "ngày mai" : $"{due:dd/MM/yyyy}";
        return new WordRow(entry, word.Text, word.Phonetic, word.PartOfSpeech, word.Meaning, where,
            whereTip, status, wash, ink, (int)entry.Status, score, word.ForgotCount, next, word.Learned ? DateTime.MaxValue : due ?? DateTime.MaxValue);
    }

    private void WordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCount();

    private void ShowCount()
    {
        var shown = (WordsGrid.ItemsSource as List<WordRow>)?.Count ?? 0;
        var picked = WordsGrid.SelectedItems.Count;
        StatusText.Text = picked > 1 ? $"Đang hiện {shown} từ · đã chọn {picked}" : $"Đang hiện {shown} từ";
    }

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (WordsGrid.SelectedItem is not WordRow row) { StatusText.Text = "Hãy chọn một từ."; return; }
        _actions.Speak(row.Text);
    }

    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        var words = SelectedRows.Select(r => r.Entry.Word).Distinct().ToList();
        if (words.Count == 0) { StatusText.Text = "Hãy chọn các từ muốn học (giữ Ctrl hoặc Shift để chọn nhiều)."; return; }
        _actions.Practice(words, $"Học lại {words.Count} từ · Tất cả từ", SessionMode.Practice);
    }

    private void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (WordsGrid.SelectedItem is not WordRow row) { StatusText.Text = "Hãy chọn một từ."; return; }
        _openInEditor(row.Entry.Plan, row.Entry.Word);
    }

    private void WordsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only a double click on a row, not on the header or scroll bar.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(WordsGrid, source) is DataGridRow { Item: WordRow row })
            _openInEditor(row.Entry.Plan, row.Entry.Word);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
