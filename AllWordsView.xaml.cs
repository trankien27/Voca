using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    // Rebuilding the table costs a few hundred ms, so typing waits for a pause before searching.
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Window? _host;
    private bool _stale = true;
    private DateTime _shownOn;
    private bool _ready;

    // The table loads a batch of rows at a time and the next one as the list is scrolled near its end.
    private const int BatchSize = 50;
    private List<WordRow> _rows = [];
    private ObservableCollection<WordRow> _shown = [];
    private string? _sortMember;
    private ListSortDirection _sortDirection;

    private sealed record PlanChoice(Guid? Id, string Name);

    public sealed record WordRow(WordEntry Entry, string Text, string Phonetic, string Pos, string Meaning, string Where,
        string WhereTip, string Status, Brush Wash, Brush Ink, int StatusOrder, string Score, int Forgot, string Next, DateTime NextDate);

    public AllWordsView(Store store, AppActions actions, Action<Plan, Word> openInEditor)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
        _openInEditor = openInEditor;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Render(firstPage: true); };
        _ready = true;
    }

    // Loaded fires again on every return to the tab. Listen to the store from the first show until the
    // window closes, and rebuild the table on return only if the data (or the day) changed meanwhile.
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_host is null && Window.GetWindow(this) is { } host)
        {
            _host = host;
            _store.Changed += OnStoreChanged;
            host.Closed += (_, _) => { _store.Changed -= OnStoreChanged; _searchDelay.Stop(); };
        }
        if (_stale || _shownOn != DateTime.Today) Refresh();
    }

    private void OnStoreChanged() => Dispatcher.Invoke(() =>
    {
        if (IsLoaded) Refresh();
        else _stale = true;
    });

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _searchDelay.Stop();
        if (sender == SortBox) _sortMember = null; // the chosen order replaces a column header sort
        Render(firstPage: true);
    }

    private void Search_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private IEnumerable<WordRow> SelectedRows => WordsGrid.SelectedItems.OfType<WordRow>();

    private void Refresh()
    {
        _stale = false;
        _shownOn = DateTime.Today;
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
        var duplicates = AllWords.DuplicateKeys(data).Count;
        SummaryText.Text = $"{all.Count} từ · đã gặp {seen} · đã thuộc / nhớ lâu {known} · đến hạn ôn {due}"
            + (duplicates > 0 ? $" · {duplicates} từ bị trùng" : "");
        Render(firstPage: false);
    }

    /// <summary>Applies search, filters and order; a new search starts at the top, a data change keeps what was loaded.</summary>
    private void Render(bool firstPage)
    {
        var data = _store.Data;
        var today = DateTime.Today;
        var keep = SelectedRows.Select(r => r.Entry.Word.Id).ToHashSet();
        var entries = AllWords.Find(data, today, SearchBox.Text,
            ChosenTag<WordFilter>(StatusBox), (PlanFilterBox.SelectedItem as PlanChoice)?.Id, ChosenTag<WordSort>(SortBox));
        _rows = SortedByColumn(entries.Select(e => Row(e, today)).ToList());
        Show(firstPage ? BatchSize : Math.Max(BatchSize, _shown.Count), keep, toTop: firstPage);
    }

    private void Show(int count, HashSet<Guid> keep, bool toTop)
    {
        _shown = new ObservableCollection<WordRow>(_rows.Take(count));
        WordsGrid.ItemsSource = _shown;
        // A new ItemsSource clears the header arrows; the order is still the one picked.
        foreach (var column in WordsGrid.Columns)
            column.SortDirection = _sortMember is not null && column.SortMemberPath == _sortMember ? _sortDirection : null;
        foreach (var row in _shown.Where(r => keep.Contains(r.Entry.Word.Id))) WordsGrid.SelectedItems.Add(row);
        if (toTop && _shown.Count > 0) WordsGrid.ScrollIntoView(_shown[0]);
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WordsGrid.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowCount();
    }

    // Near the end of the loaded rows (or when they do not fill the table yet), load the next batch.
    private void WordsGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_shown.Count >= _rows.Count || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 5) return;
        foreach (var row in _rows.Skip(_shown.Count).Take(BatchSize)) _shown.Add(row);
    }

    // A click on a column header sorts every matching word, not just the page on screen.
    private void WordsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        _sortDirection = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortMember = e.Column.SortMemberPath;
        var keep = SelectedRows.Select(r => r.Entry.Word.Id).ToHashSet();
        _rows = SortedByColumn(_rows);
        Show(BatchSize, keep, toTop: true);
    }

    private List<WordRow> SortedByColumn(List<WordRow> rows)
    {
        if (_sortMember is null || typeof(WordRow).GetProperty(_sortMember) is not { } property) return rows;
        var comparer = Comparer<object?>.Create((a, b) => a is string x && b is string y
            ? string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase)
            : Comparer<object?>.Default.Compare(a, b));
        return (_sortDirection == ListSortDirection.Ascending
            ? rows.OrderBy(property.GetValue, comparer)
            : rows.OrderByDescending(property.GetValue, comparer)).ToList();
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
        var picked = WordsGrid.SelectedItems.Count;
        StatusText.Text = picked > 1 ? $"Đang hiện {_rows.Count} từ · đã chọn {picked}" : $"Đang hiện {_rows.Count} từ";
    }

    /// <summary>Lists every copy of the words written more than once, side by side, or says the library has none.</summary>
    private void CheckDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var duplicates = AllWords.DuplicateKeys(_store.Data).Count;
        if (duplicates == 0)
        {
            StatusText.Text = $"Không có từ nào bị trùng trong {_store.Data.Plans.Sum(p => p.Words.Count)} từ của thư viện.";
            return;
        }
        _ready = false;
        SearchBox.Text = "";
        StatusBox.SelectedItem = DuplicateItem;
        PlanFilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        _ready = true;
        _searchDelay.Stop();
        _sortMember = null;
        Render(firstPage: true);
        StatusText.Text = $"{duplicates} từ bị trùng ({_rows.Count} dòng), các bản nằm cạnh nhau. Nhấp đúp một dòng để mở trong Sửa lộ trình và xóa bản thừa.";
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
