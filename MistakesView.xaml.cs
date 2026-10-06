using System.Windows;
using System.Windows.Controls;
using Voca.Models;
using Voca.Services;

namespace Voca;

/// <summary>
/// The "Từ sai" tab of the library: mistake lists (one per test, one per day of sessions, plus "all").
/// Pick one to study it now, keep it for tomorrow as a mistake day, or delete it.
/// </summary>
public partial class MistakesView : System.Windows.Controls.UserControl
{
    private readonly Store _store;
    private readonly AppActions _actions;

    /// <summary>A list row; <see cref="Id"/> is null for "all wrong words".</summary>
    private sealed record ListRow(Guid? Id, string Title, string Meta, List<Word> Words);
    private sealed record WordRow(string Text, string Phonetic, string Pos, string Meaning, string Times);

    public MistakesView(Store store, AppActions actions)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
    }

    // Listen for changes only while the tab is shown (switching tabs unloads it).
    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e) => _store.Changed -= OnStoreChanged;

    private void OnStoreChanged() => Dispatcher.Invoke(Refresh);

    private ListRow? Selected => ListsBox.SelectedItem as ListRow;

    private void Refresh()
    {
        var data = _store.Data;
        var keep = Selected?.Id;
        var hadSelection = Selected is not null;
        var lists = MistakeDays.Lists(data);
        var all = MistakeDays.Waiting(data).Select(x => x.Word).ToList();
        var rows = new List<ListRow>();
        if (lists.Count > 1) rows.Add(new ListRow(null, "Tất cả từ sai", $"{all.Count} từ · sai nhiều nhất trước", all));
        rows.AddRange(lists.Select(x => new ListRow(x.List.Id, x.List.Title, $"{x.Words.Count} từ · {x.List.CreatedAt:HH:mm dd/MM/yyyy}", x.Words)));

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ListsBox.ItemsSource = rows;
        ListsBox.SelectedItem = rows.FirstOrDefault(r => hadSelection && r.Id == keep)
                                ?? rows.FirstOrDefault(r => r.Id is not null) ?? rows.FirstOrDefault();
        RenderSelected();
        RenderDayBanner();
    }

    private void ListsBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelected();

    private void RenderSelected()
    {
        if (Selected is not { } row)
        {
            SelectedTitle.Text = SelectedMeta.Text = "";
            WordsList.ItemsSource = null;
            return;
        }
        var data = _store.Data;
        SelectedTitle.Text = row.Title;
        SelectedMeta.Text = row.Meta;
        WordsList.ItemsSource = row.Words.Select(w =>
            new WordRow(w.Text, w.Phonetic, w.PartOfSpeech, w.Meaning, $"sai {MistakeDays.TimesWrong(data, w)} lần")).ToList();
        DeleteButton.Visibility = row.Id is null ? Visibility.Collapsed : Visibility.Visible;
        var many = row.Words.Count > MistakeDays.MaxWords;
        TomorrowButton.ToolTip = "Ngày mai thành ngày học từ sai: thay cho bài của lộ trình, hôm sau học tiếp" +
                                 (many ? $" (tối đa {MistakeDays.MaxWords} từ)" : "");
    }

    private void RenderDayBanner()
    {
        var data = _store.Data;
        var today = DateTime.Today;
        var day = MistakeDays.Upcoming(data, today) ?? (MistakeDays.Active(data, today) is { Done: false } active ? active : null);
        DayBanner.Visibility = day is null ? Visibility.Collapsed : Visibility.Visible;
        if (day is null) return;
        var when = day.Date.Date <= today ? "hôm nay" : day.Date.Date == today.AddDays(1) ? "ngày mai" : $"ngày {day.Date:dd/MM}";
        DayBannerText.Text = $"📌 Đã hẹn ngày học từ sai vào {when} ({MistakeDays.WordsOf(data, day).Count} từ). Ngày đó thay cho bài của lộ trình, hôm sau học tiếp.";
    }

    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        _actions.Practice(row.Words, $"Học lại từ sai · {row.Title}", SessionMode.Practice);
    }

    private void Tomorrow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var count = 0;
        _store.Update(d => count = MistakeDays.Schedule(d, DateTime.Today.AddDays(1), DateTime.Today, row.Words.Select(w => w.Id)));
        StatusText.Text = $"Đã hẹn ngày mai học {count} từ sai. Muốn học sớm hơn thì bấm “▶ Học ngay”.";
    }

    private void CancelDay_Click(object sender, RoutedEventArgs e)
    {
        _store.Update(MistakeDays.Cancel);
        StatusText.Text = "Đã hủy ngày học từ sai. Các từ vẫn còn trong danh sách.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Id: Guid id } row) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this)!, $"Xóa danh sách “{row.Title}” ({row.Words.Count} từ)?\nCác từ này không còn được tính là từ sai (trừ khi có trong danh sách khác).",
                "Xóa danh sách từ sai", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _store.Update(d => MistakeDays.DeleteList(d, id));
        StatusText.Text = $"Đã xóa “{row.Title}”.";
    }
}
