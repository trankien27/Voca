using System.Windows;
using Voca.Models;
using Voca.Services;

namespace Voca;

/// <summary>Shown once after the last day of a plan: what was learned and which words need more work.</summary>
public partial class WeekSummaryWindow : Window
{
    private sealed record Row(string Word, string Meaning, string Count);

    public WeekSummaryWindow(AppData data, Guid finishedPlanId)
    {
        InitializeComponent();
        var plan = data.Plans.FirstOrDefault(p => p.Id == finishedPlanId);
        var words = plan?.Words ?? [];
        TitleText.Text = plan?.Name ?? "Lộ trình vừa học";
        LearnedText.Text = words.Count(w => w.Review is not null).ToString();
        FirstTryText.Text = words.Count(w => w.Review is not null && w.ForgotCount == 0).ToString();
        ReviewText.Text = words.Count(w => w.ForgotCount > 0).ToString();

        var forgotten = words.Where(w => w.ForgotCount > 0).OrderByDescending(w => w.ForgotCount).Take(5)
            .Select(w => new Row(w.Text, $"  ·  {w.Meaning}", $"sai {w.ForgotCount} lần")).ToList();
        ForgottenList.ItemsSource = forgotten;
        NoMistakesText.Visibility = forgotten.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (CourseEngine.Current(data) is var (next, _) && !data.Position.Finished)
        {
            NextText.Text = $"Tiếp theo: {next.Name}. Từ của tuần vừa học vẫn tiếp tục được ôn khi đến hạn.";
            ContinueButton.Content = $"Bắt đầu {next.Name}";
        }
        else
        {
            NextText.Text = "Bạn đã học hết khóa học. Mở Thư viện để tạo chủ đề mới; các từ cũ vẫn được ôn khi đến hạn.";
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
}
