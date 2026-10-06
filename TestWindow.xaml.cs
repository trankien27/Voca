using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Voca.Models;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voca;

/// <summary>
/// A test over any range of a plan: set up, answer, see the score. Results never change the review
/// schedule; the learner can choose to send the wrong words to reviews.
/// </summary>
public partial class TestWindow : Window
{
    private enum Stage { Setup, Question, Result }

    private static readonly Brush Good = Frozen(0x06, 0x76, 0x47), Bad = Frozen(0xB4, 0x23, 0x18);
    private static readonly Brush PickedWash = Frozen(0xEE, 0xEC, 0xFF), PickedLine = Frozen(0x6C, 0x5C, 0xE7);
    private static readonly Brush OptionWash = Frozen(0xFF, 0xFF, 0xFF), OptionLine = Frozen(0xE3, 0xE1, 0xF0);

    private readonly Store _store;
    private readonly bool _canListen;
    private readonly Action<string> _speak;
    private readonly AppActions _actions;
    /// <summary>The mistake list of this test; retry rounds add to it instead of making new lists.</summary>
    private Guid? _listId;
    private readonly Stopwatch _clock = new();
    private (int From, int To)? _requestedRange;
    private Stage _stage;
    private List<TestQuestion> _questions = [];
    private readonly List<TestAnswer> _answers = [];
    private readonly HashSet<TestQuestion> _hinted = [];
    private List<TestKind> _kinds = [];
    private string _scope = "";
    private int _index;
    /// <summary>The answer given to each question (null = not answered yet); graded only on submit.</summary>
    private string?[] _given = [];

    private sealed record ResultRow(string Mark, Brush MarkBrush, string Word, string Meaning, string Given, Visibility GivenVisibility, string Kind);
    private sealed record CountItem(int Count, string Label) { public override string ToString() => Label; }

    public TestWindow(Store store, Guid? planId, int? fromDay, int? toDay, AppActions actions)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
        _canListen = actions.CanListen;
        _speak = actions.Speak;
        if (fromDay is int f && toDay is int t) _requestedRange = (Math.Min(f, t), Math.Max(f, t));

        if (!_canListen)
        {
            KindListening.IsChecked = false;
            KindListening.IsEnabled = false;
            NoVoiceText.Visibility = Visibility.Visible;
        }
        CountBox.ItemsSource = TestBuilder.CountChoices.Select(c => new CountItem(c, c == 0 ? "Tất cả" : $"{c} câu")).ToList();
        CountBox.SelectedIndex = 1;

        var data = store.Data;
        var plans = CourseEngine.CoursePlans(data).Concat(data.Plans.Where(p => !data.Course.Contains(p.Id)))
            .Where(p => p.Words.Count > 0).ToList();
        PlanBox.ItemsSource = plans;
        PlanBox.SelectedItem = plans.FirstOrDefault(p => p.Id == planId) ?? CourseEngine.Current(data)?.Plan ?? plans.FirstOrDefault();
        ShowStage(Stage.Setup);
    }

    private Plan? SelectedPlan => PlanBox.SelectedItem as Plan;
    private int FromDay => FromDayBox.SelectedItem is int d ? d : 1;
    private int ToDay => ToDayBox.SelectedItem is int d ? d : 1;

    // ---------------------------------- setup ----------------------------------

    private void PlanBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPlan is not { } plan) return;
        var days = Enumerable.Range(1, Math.Max(1, plan.DayCount)).ToList();
        var (from, to) = _requestedRange ?? DefaultRange(plan);
        _requestedRange = null;
        FromDayBox.ItemsSource = days;
        ToDayBox.ItemsSource = days;
        FromDayBox.SelectedItem = Math.Clamp(from, 1, days.Count);
        ToDayBox.SelectedItem = Math.Clamp(to, 1, days.Count);
        UpdateSetup();
    }

    /// <summary>The days already reached: up to today's day for the plan being studied, otherwise the whole plan.</summary>
    private (int, int) DefaultRange(Plan plan)
    {
        var data = _store.Data;
        return CourseEngine.Current(data)?.Plan == plan ? (1, Math.Max(1, data.Position.Day)) : (1, plan.DayCount);
    }

    private void Range_Changed(object sender, RoutedEventArgs e) => UpdateSetup();

    private List<TestKind> ChosenKinds()
    {
        var kinds = new List<TestKind>();
        if (KindWordToMeaning.IsChecked == true) kinds.Add(TestKind.WordToMeaning);
        if (KindMeaningToWord.IsChecked == true) kinds.Add(TestKind.MeaningToWord);
        if (KindTyping.IsChecked == true) kinds.Add(TestKind.Typing);
        if (KindListening.IsChecked == true && _canListen) kinds.Add(TestKind.Listening);
        return kinds;
    }

    private void UpdateSetup()
    {
        if (!IsInitialized || PoolText is null || SelectedPlan is not { } plan || _stage != Stage.Setup) return;
        var pool = TestBuilder.Pool(plan, FromDay, ToDay);
        PoolText.Text = FromDay > ToDay ? "" : $"có {pool.Count} từ";
        var count = (CountBox.SelectedItem as CountItem)?.Count ?? 20;
        var questions = count == 0 ? pool.Count : Math.Min(count, pool.Count);
        SetupError.Text = FromDay > ToDay ? "“Từ ngày” phải nhỏ hơn hoặc bằng “đến ngày”."
            : pool.Count == 0 ? "Khoảng ngày này chưa có từ nào."
            : ChosenKinds().Count == 0 ? "Hãy chọn ít nhất một dạng câu hỏi."
            : "";
        NextButton.IsEnabled = SetupError.Text.Length == 0;
        FooterText.Text = NextButton.IsEnabled ? $"{questions} câu · {string.Join(", ", ChosenKinds().Select(TestBuilder.Label))}" : "";
    }

    private void StartFromSetup()
    {
        if (SelectedPlan is not { } plan) return;
        var count = (CountBox.SelectedItem as CountItem)?.Count ?? 20;
        var words = TestBuilder.Pick(TestBuilder.Pool(plan, FromDay, ToDay), count, Random.Shared);
        _listId = null;
        _scope = FromDay == ToDay ? $"{plan.Name} · ngày {FromDay}" : $"{plan.Name} · ngày {FromDay}–{ToDay}";
        Start(words, ChosenKinds());
    }

    private void Start(IReadOnlyList<Word> words, List<TestKind> kinds)
    {
        _kinds = kinds;
        _questions = TestBuilder.Build(_store.Data, words, kinds, Random.Shared);
        if (_questions.Count == 0) return;
        _answers.Clear();
        _hinted.Clear();
        _given = new string?[_questions.Count];
        _index = 0;
        _clock.Restart();
        ShowStage(Stage.Question);
    }

    // --------------------------------- question --------------------------------

    private TestQuestion Current => _questions[_index];

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        SetupPanel.Visibility = stage == Stage.Setup ? Visibility.Visible : Visibility.Collapsed;
        QuestionPanel.Visibility = stage == Stage.Question ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = stage == Stage.Result ? Visibility.Visible : Visibility.Collapsed;
        RetryWrongButton.Visibility = NewTestButton.Visibility = BackButton.Visibility = Visibility.Collapsed;
        HeaderRight.Text = stage == Stage.Setup ? "" : _scope;
        switch (stage)
        {
            case Stage.Setup:
                HeaderTitle.Text = "Tạo bài kiểm tra";
                NextButton.Content = "Bắt đầu";
                UpdateSetup();
                break;
            case Stage.Question:
                HeaderTitle.Text = "Bài kiểm tra";
                RenderQuestion();
                break;
            case Stage.Result:
                HeaderTitle.Text = "Kết quả";
                RenderResult();
                break;
        }
    }

    private void RenderQuestion()
    {
        var q = Current;
        var word = q.Word;
        QuestionLabel.Text = $"Câu {_index + 1} / {_questions.Count}  ·  {TestBuilder.Label(q.Kind)}";
        (QuestionPrompt.Text, QuestionSub.Text) = q.Kind switch
        {
            TestKind.WordToMeaning => (word.Text, Join(word.Phonetic, word.PartOfSpeech)),
            TestKind.Listening => ("Nghe và chọn từ đúng", "Bấm “Nghe lại” hoặc phím Space để nghe lần nữa"),
            _ => (word.Meaning, word.PartOfSpeech.Length > 0 ? $"({word.PartOfSpeech})" : "")
        };
        ListenButton.Visibility = q.Kind == TestKind.Listening ? Visibility.Visible : Visibility.Collapsed;

        OptionsGrid.Children.Clear();
        OptionsGrid.Visibility = q.Kind == TestKind.Typing ? Visibility.Collapsed : Visibility.Visible;
        TypingPanel.Visibility = q.Kind == TestKind.Typing ? Visibility.Visible : Visibility.Collapsed;
        var number = 1;
        foreach (var option in q.Options)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Option"),
                Tag = option,
                Content = new TextBlock { Text = $"{number++}.  {option}", TextWrapping = TextWrapping.Wrap }
            };
            button.Click += (_, _) => Choose(option);
            OptionsGrid.Children.Add(button);
        }
        MarkChosen();
        if (q.Kind == TestKind.Typing)
        {
            TypingBox.Text = _given[_index] ?? "";
            TypingBox.CaretIndex = TypingBox.Text.Length;
            HintText.Text = _hinted.Contains(q) ? TestBuilder.Hint(word.Text) : "";
            Dispatcher.BeginInvoke(() => TypingBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        if (q.Kind == TestKind.Listening) _speak(word.Text);

        BackButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = true;
        NextButton.Content = _index == _questions.Count - 1 ? "Nộp bài" : "Câu tiếp";
        UpdateProgress();
    }

    private static string Join(params string[] parts) => string.Join("  ", parts.Where(p => p.Length > 0));

    private void UpdateProgress()
    {
        var answered = _given.Count(g => !string.IsNullOrWhiteSpace(g));
        var keys = Current.Kind == TestKind.Typing ? "Enter để sang câu tiếp" : "phím 1–4 để chọn, ← → để chuyển câu";
        FooterText.Text = $"Đã trả lời {answered} / {_questions.Count} · {keys}";
    }

    /// <summary>Highlights the option chosen for the current question (no right/wrong until submit).</summary>
    private void MarkChosen()
    {
        foreach (var button in OptionsGrid.Children.OfType<Button>())
        {
            var chosen = (string)button.Tag == _given[_index];
            button.Background = chosen ? PickedWash : OptionWash;
            button.BorderBrush = chosen ? PickedLine : OptionLine;
            button.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Records the chosen option and moves on after a short pause; the last question waits for "Nộp bài".</summary>
    private void Choose(string option)
    {
        if (_stage != Stage.Question) return;
        _given[_index] = option;
        MarkChosen();
        UpdateProgress();
        if (_index == _questions.Count - 1) return;
        var at = _index;
        var pause = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        pause.Tick += (_, _) =>
        {
            pause.Stop();
            // Skip if the learner already moved (Back/Next) during the pause.
            if (_stage == Stage.Question && _index == at) GoTo(at + 1);
        };
        pause.Start();
    }

    private void SaveTyping()
    {
        if (_stage == Stage.Question && Current.Kind == TestKind.Typing)
            _given[_index] = TypingBox.Text.Trim().Length == 0 ? null : TypingBox.Text.Trim();
    }

    private void GoTo(int index)
    {
        SaveTyping();
        _index = Math.Clamp(index, 0, _questions.Count - 1);
        RenderQuestion();
    }

    /// <summary>Grades every question at once; unanswered questions count as wrong.</summary>
    private void Submit()
    {
        SaveTyping();
        var blank = _given.Count(string.IsNullOrWhiteSpace);
        if (blank > 0 && System.Windows.MessageBox.Show(this, $"Còn {blank} câu chưa trả lời, sẽ tính là sai. Nộp bài?", "Nộp bài",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _answers.Clear();
        for (var i = 0; i < _questions.Count; i++)
        {
            var q = _questions[i];
            var given = _given[i]?.Trim() ?? "";
            var correct = given.Length > 0 && (q.Kind == TestKind.Typing
                ? TestBuilder.IsTypedCorrectly(given, q.Correct)
                : string.Equals(given, q.Correct, StringComparison.Ordinal));
            _answers.Add(new TestAnswer(q, given, correct));
        }
        ShowStage(Stage.Result);
    }

    private void Hint_Click(object sender, RoutedEventArgs e)
    {
        _hinted.Add(Current);
        HintText.Text = TestBuilder.Hint(Current.Word.Text);
        TypingBox.Focus();
    }

    private void Listen_Click(object sender, RoutedEventArgs e) => _speak(Current.Word.Text);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Question && _index > 0) GoTo(_index - 1);
    }

    // ---------------------------------- result ---------------------------------

    private void RenderResult()
    {
        _clock.Stop();
        var right = _answers.Count(a => a.Correct);
        var total = _answers.Count;
        var percent = total == 0 ? 0 : (int)Math.Round(100.0 * right / total);
        ResultScore.Text = $"{right} / {total}";
        var minutes = (int)_clock.Elapsed.TotalMinutes;
        var time = minutes > 0 ? $"{minutes} phút {_clock.Elapsed.Seconds} giây" : $"{_clock.Elapsed.Seconds} giây";
        ResultNote.Text = $"{percent}% · {time} · " + percent switch
        {
            100 => "Hoàn hảo!",
            >= 80 => "Rất tốt, chỉ còn vài từ cần xem lại.",
            >= 50 => "Khá ổn — làm lại các câu sai để nhớ chắc hơn.",
            _ => "Nên học lại khoảng ngày này rồi kiểm tra lần nữa."
        };
        ResultKinds.Text = string.Join("   ·   ", _answers.GroupBy(a => a.Question.Kind).OrderBy(g => g.Key)
            .Select(g => $"{TestBuilder.Label(g.Key)}: {g.Count(a => a.Correct)}/{g.Count()}"));
        ResultList.ItemsSource = _answers.OrderBy(a => a.Correct).Select(a =>
        {
            var w = a.Question.Word;
            var kind = TestBuilder.Label(a.Question.Kind) + (_hinted.Contains(a.Question) ? " · có gợi ý" : "");
            var given = a.Given.Length == 0 ? "(bỏ trống)" : a.Given;
            return new ResultRow(a.Correct ? "✓" : "✗", a.Correct ? Good : Bad, w.Text, "  —  " + w.Meaning,
                $"Bạn trả lời: {given}", a.Correct ? Visibility.Collapsed : Visibility.Visible, kind);
        }).ToList();

        var wrong = right < total;
        RetryWrongButton.Visibility = wrong ? Visibility.Visible : Visibility.Collapsed;
        NewTestButton.Visibility = Visibility.Visible;
        NextButton.IsEnabled = true;
        NextButton.Content = "Đóng";
        FooterText.Text = wrong ? "Kết quả không làm thay đổi lịch ôn tập." : "";

        // Wrong words go to this test's mistake list (the review schedule stays as it is).
        var wrongWords = WrongWords();
        if (wrongWords.Count > 0)
        {
            var now = DateTime.Now;
            _store.Update(d => _listId = MistakeDays.Record(d, wrongWords, now, $"Bài kiểm tra {now:HH:mm} · {_scope.Replace(" · câu sai", "")}", _listId));
        }
        MistakeOffer.Visibility = wrongWords.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OfferText.Text = $"Đã lưu {wrongWords.Count} từ sai vào danh sách từ sai. Học lại ngay, hoặc mở danh sách để để dành học sau (có thể hẹn sang ngày mai).";
    }

    private void OfferPractice_Click(object sender, RoutedEventArgs e) =>
        _actions.Practice(WrongWords(), $"Học lại từ sai · {_scope.Replace(" · câu sai", "")}", SessionMode.Practice);

    private void OfferOpenList_Click(object sender, RoutedEventArgs e) => _actions.OpenMistakes();

    private List<Word> WrongWords() => _answers.Where(a => !a.Correct).Select(a => a.Question.Word).Distinct().ToList();

    private void RetryWrong_Click(object sender, RoutedEventArgs e)
    {
        var words = WrongWords();
        if (words.Count == 0) return;
        if (!_scope.EndsWith(" · câu sai")) _scope += " · câu sai";
        Start(words, _kinds);
    }

    private void NewTest_Click(object sender, RoutedEventArgs e) => ShowStage(Stage.Setup);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Setup:
                StartFromSetup();
                break;
            case Stage.Question when _index < _questions.Count - 1:
                GoTo(_index + 1);
                break;
            case Stage.Question:
                Submit();
                break;
            case Stage.Result:
                Close();
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_stage != Stage.Question) return;
        var q = Current;
        if (e.Key == Key.Enter)
        {
            Next_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (q.Kind == TestKind.Typing) return;
        switch (e.Key)
        {
            case Key.Left:
                Back_Click(sender, e);
                e.Handled = true;
                return;
            case Key.Right:
                Next_Click(sender, e);
                e.Handled = true;
                return;
            case Key.Space when q.Kind == TestKind.Listening:
                _speak(q.Word.Text);
                e.Handled = true;
                return;
        }
        var pick = e.Key switch
        {
            >= Key.D1 and <= Key.D4 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad4 => e.Key - Key.NumPad1,
            _ => -1
        };
        if (pick >= 0 && pick < q.Options.Count)
        {
            Choose(q.Options[pick]);
            e.Handled = true;
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
