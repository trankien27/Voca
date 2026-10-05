using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voca.Models;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Voca;

public partial class StatsWindow : Window
{
    private static readonly Brush EmptyBar = Frozen(0xEC, 0xEB, 0xF5);
    private static readonly Brush ActiveBar = Frozen(0x6C, 0x5C, 0xE7);
    private static readonly Brush TodayBar = Frozen(0xE8, 0x59, 0x0C);
    private static readonly Brush MutedText = Frozen(0x66, 0x70, 0x85);
    private readonly Store _store;

    public StatsWindow(Store store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }

    public void Refresh()
    {
        var data = _store.Data;
        var today = DateTime.Today;
        var stats = StatsService.Compute(data, today);

        SummaryText.Text = $"{stats.TotalWords} từ trong thư viện · {stats.Unstudied} từ chưa học · hôm nay đã trả lời {stats.AnsweredToday} câu";
        StreakValue.Text = stats.CurrentStreak.ToString();
        BestStreakText.Text = $"Kỷ lục: {stats.BestStreak} ngày";
        MasteredValue.Text = stats.Mastered.ToString();
        LearningValue.Text = stats.Learning.ToString();
        DueValue.Text = stats.DueToday.ToString();

        var max = Math.Max(1, stats.LastDays.Max(d => d.Count));
        ActivityBars.Items.Clear();
        foreach (var (date, count) in stats.LastDays)
        {
            ActivityBars.Items.Add(new Border
            {
                Height = count == 0 ? 4 : 8 + 78.0 * count / max,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(3),
                Background = count == 0 ? EmptyBar : date == today ? TodayBar : ActiveBar,
                ToolTip = $"{date:dd/MM}: {count} câu trả lời"
            });
        }
        ActivityStart.Text = stats.LastDays[0].Date.ToString("dd/MM");

        ForgottenList.Children.Clear();
        ReviewPlanButton.IsEnabled = stats.OftenForgotten.Count > 0;
        if (stats.OftenForgotten.Count == 0)
        {
            ForgottenList.Children.Add(new TextBlock
            {
                Text = "Chưa có từ nào bị trả lời sai trong phiên học.",
                Foreground = MutedText, TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var (word, plan) in stats.OftenForgotten)
            ForgottenList.Children.Add(ForgottenRow(word, plan));
    }

    private void ReviewPlan_Click(object sender, RoutedEventArgs e)
    {
        Plan? plan = null;
        _store.Update(d => plan = CourseEngine.CreateReviewPlan(d, 20, 10, DateTime.Today));
        ReviewPlanText.Text = plan is null
            ? "Chưa có từ hay quên để tạo tuần ôn."
            : $"Đã tạo “{plan.Name}” ({plan.Words.Count} từ, {plan.DayCount} ngày), học ngay sau tuần hiện tại.";
    }

    private static UIElement ForgottenRow(Word word, Plan plan)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var count = new TextBlock
        {
            Text = $"sai {word.ForgotCount} lần",
            Foreground = Frozen(0xB4, 0x23, 0x18), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(count);
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
        text.Inlines.Add(new System.Windows.Documents.Run(word.Text) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(word.Meaning))
            text.Inlines.Add(new System.Windows.Documents.Run($"  —  {word.Meaning}") { Foreground = MutedText });
        text.Inlines.Add(new System.Windows.Documents.Run($"  · {plan.Name}") { Foreground = MutedText, FontSize = 11 });
        row.Children.Add(text);
        return row;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
