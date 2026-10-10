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
/// The daily session: flip through today's new words, then a multiple-choice quiz over the new words
/// and due reviews. The quiz result drives spaced repetition (wrong answers return tomorrow); on the result
/// screen words can be ticked "Đã thuộc" (applied when the window closes).
/// </summary>
public partial class SessionWindow : Window
{
    private enum Stage { Learn, Quiz, Result }

    private static readonly Brush Good = Frozen(0x06, 0x76, 0x47), GoodWash = Frozen(0xE7, 0xF7, 0xEE);
    private static readonly Brush Bad = Frozen(0xB4, 0x23, 0x18), BadWash = Frozen(0xFD, 0xEC, 0xEA);
    private static readonly Brush StageOn = Frozen(0xEE, 0xEC, 0xFF), StageInk = Frozen(0x4B, 0x3C, 0xC4), MutedInk = Frozen(0x66, 0x70, 0x85);

    private readonly List<Word> _learn;
    private readonly List<QuizQuestion> _quiz;
    private readonly IReadOnlyCollection<Word> _newWords;
    private readonly Action<IReadOnlyList<(Word Word, bool Correct)>, IReadOnlyCollection<Word>> _onFinished;
    private readonly Action<string> _speak;
    private readonly List<(Word Word, bool Correct)> _answers = [];
    private Stage _stage;
    private int _index;
    private bool _flipped;
    private readonly SessionMode _mode;
    /// <summary>Marks the words ticked "Đã thuộc" on the result screen.</summary>
    private readonly Action<IReadOnlyList<Word>> _onLearned;
    private List<ResultRow> _results = [];
    private string? _picked;

    private sealed class ResultRow(Word source, bool correct, string mark, Brush markBrush, string meaning, string next)
    {
        public Word Source { get; } = source;
        public bool Correct { get; } = correct;
        public string Mark { get; } = mark;
        public Brush MarkBrush { get; } = markBrush;
        public string Word => Source.Text;
        public string Meaning { get; } = meaning;
        public string Next { get; } = next;
        public bool Learned { get; set; }
        public Visibility LearnedVisibility => Source.Learned ? Visibility.Collapsed : Visibility.Visible;
    }

    public SessionWindow(AppData data, IReadOnlyList<Word> newWords, IReadOnlyList<Word> reviews, string courseText,
        Action<IReadOnlyList<(Word Word, bool Correct)>, IReadOnlyCollection<Word>> onFinished, Action<string> speak,
        Action<IReadOnlyList<Word>> onLearned, SessionMode mode = SessionMode.Course)
    {
        InitializeComponent();
        _mode = mode;
        _onLearned = onLearned;
        Closed += (_, _) => ApplyLearned();
        if (mode is SessionMode.MistakeDay or SessionMode.Practice) StageLearnText.Text = "Xem lại từ sai";
        _newWords = newWords.ToList();
        _learn = newWords.ToList();
        _quiz = CourseEngine.BuildQuiz(data, [.. newWords, .. reviews], Random.Shared, data.Settings.QuizTyping);
        _onFinished = onFinished;
        _speak = speak;
        CourseText.Text = courseText;
        if (_quiz.Count == 0)
        {
            ShowEmpty();
            return;
        }
        Show(_learn.Count > 0 ? Stage.Learn : Stage.Quiz);
    }

    private void ShowEmpty()
    {
        _stage = Stage.Result;
        SetStageHeader(Stage.Result);
        LearnPanel.Visibility = QuizPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultScore.Text = "✓";
        ResultNote.Text = "Hôm nay không có từ mới hay từ cần ôn. Hẹn bạn ngày mai!";
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Content = "Đóng";
        FooterText.Text = "";
    }

    private void Show(Stage stage)
    {
        _stage = stage;
        _index = 0;
        SetStageHeader(stage);
        LearnPanel.Visibility = stage == Stage.Learn ? Visibility.Visible : Visibility.Collapsed;
        QuizPanel.Visibility = stage == Stage.Quiz ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = stage == Stage.Result ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void SetStageHeader(Stage stage)
    {
        void Mark(Border border, TextBlock text, Stage s)
        {
            border.Background = s == stage ? StageOn : System.Windows.Media.Brushes.Transparent;
            text.Foreground = s == stage ? StageInk : s < stage ? Good : MutedInk;
            text.FontWeight = s == stage ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Mark(StageLearn, StageLearnText, Stage.Learn);
        Mark(StageQuiz, StageQuizText, Stage.Quiz);
        Mark(StageResult, StageResultText, Stage.Result);
        StageLearn.Visibility = _learn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        switch (_stage)
        {
            case Stage.Learn: RenderCard(); break;
            case Stage.Quiz: RenderQuestion(); break;
            case Stage.Result: RenderResult(); break;
        }
    }

    private void RenderCard()
    {
        var word = _learn[_index];
        CardWord.Text = word.Text;
        CardPhonetic.Text = word.Phonetic;
        CardPos.Text = word.PartOfSpeech;
        CardMeaning.Text = _flipped ? word.Meaning : "";
        CardExample.Text = _flipped && word.Example.Length > 0 ? $"“{word.Example}”" : "";
        CardMeaning.Visibility = CardExample.Visibility = _flipped ? Visibility.Visible : Visibility.Collapsed;
        CardHint.Visibility = _flipped ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = true;
        NextButton.Content = _index == _learn.Count - 1 ? "Bắt đầu kiểm tra" : "Tiếp";
        FooterText.Text = $"{(_mode is SessionMode.Course or SessionMode.NewWords ? "Từ mới" : "Từ sai")} {_index + 1} / {_learn.Count}";
    }

    private void RenderQuestion()
    {
        var q = _quiz[_index];
        QuizQuestionLabel.Text = q.Kind switch
        {
            QuizKind.ChooseWord => "Từ nào có nghĩa là:",
            QuizKind.ChooseMeaning => "Nghĩa của từ này là gì?",
            QuizKind.Cloze => "Chọn từ điền vào chỗ trống:",
            _ => "Gõ từ tiếng Anh có nghĩa là:"
        };
        QuizPrompt.Text = q.Prompt;
        QuizPrompt.FontFamily = new System.Windows.Media.FontFamily(q.Kind == QuizKind.ChooseMeaning ? "Georgia" : "Segoe UI");
        QuizPrompt.FontSize = q.Kind == QuizKind.Cloze ? 21 : 26;
        QuizPrompt.FontWeight = q.Kind == QuizKind.Cloze ? FontWeights.SemiBold : FontWeights.Bold;
        TypePanel.Visibility = q.Kind == QuizKind.Type ? Visibility.Visible : Visibility.Collapsed;
        if (q.Kind == QuizKind.Type)
        {
            TypeBox.IsReadOnly = _picked is not null;
            TypeCheckButton.IsEnabled = _picked is null;
            if (_picked is null)
            {
                TypeBox.Text = "";
                Dispatcher.BeginInvoke(() => TypeBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        QuizOptions.Children.Clear();
        for (var i = 0; i < q.Options.Count; i++)
        {
            var option = q.Options[i];
            var button = new Button { Content = new TextBlock { Text = $"{i + 1}.  {option}", TextWrapping = TextWrapping.Wrap }, Tag = option, Style = (Style)FindResource("Option") };
            if (_picked is not null)
            {
                button.IsHitTestVisible = false;
                if (option == q.Correct) { button.Background = GoodWash; button.BorderBrush = Good; button.Foreground = Good; button.FontWeight = FontWeights.Bold; }
                else if (option == _picked) { button.Background = BadWash; button.BorderBrush = Bad; button.Foreground = Bad; }
            }
            button.Click += (_, _) => Pick((string)button.Tag);
            QuizOptions.Children.Add(button);
        }
        if (_picked is null)
        {
            QuizFeedback.Text = "";
        }
        else if (IsRight(q, _picked))
        {
            QuizFeedback.Foreground = Good;
            QuizFeedback.Text = q.Kind == QuizKind.ChooseMeaning ? "Chính xác." : $"Chính xác. {q.Word.Text} {q.Word.Phonetic}";
        }
        else
        {
            QuizFeedback.Foreground = Bad;
            QuizFeedback.Text = $"Chưa đúng. {q.Word.Text} = {q.Word.Meaning}. Từ này sẽ quay lại vào ngày mai.";
        }
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = _picked is not null;
        NextButton.Content = _index == _quiz.Count - 1 ? "Xem kết quả" : "Câu tiếp";
        FooterText.Text = $"Câu {_index + 1} / {_quiz.Count} · đúng {_answers.Count(a => a.Correct)}";
    }

    private static bool IsRight(QuizQuestion q, string given) =>
        q.Kind == QuizKind.Type ? TestBuilder.IsTypedCorrectly(given, q.Correct) : given == q.Correct;

    private void Pick(string option)
    {
        if (_picked is not null) return;
        _picked = option;
        var q = _quiz[_index];
        _answers.Add((q.Word, IsRight(q, option)));
        if (q.AskWord) _speak(q.Word.Text);
        RenderQuestion();
    }

    private void TypeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (TypeBox.Text.Trim().Length == 0) { TypeBox.Focus(); return; }
        Pick(TypeBox.Text.Trim());
    }

    private void RenderResult()
    {
        var correct = _answers.Count(a => a.Correct);
        ResultScore.Text = $"{correct}/{_answers.Count}";
        var today = DateTime.Today;
        ResultNote.Text = _mode switch
        {
            SessionMode.MistakeDay => correct == _answers.Count
                ? "Đúng hết! Các từ này đã ra khỏi danh sách từ sai. Mai học tiếp lộ trình."
                : "Từ trả lời đúng đã ra khỏi danh sách từ sai; từ còn sai ở lại danh sách và quay lại vào ngày mai.",
            SessionMode.NewWords => correct == _answers.Count
                ? "Đúng hết! Các từ này đã vào lịch ôn tập."
                : "Các từ đã vào lịch ôn tập; từ làm sai sẽ quay lại vào ngày mai và nằm trong danh sách từ sai.",
            SessionMode.Practice => correct == _answers.Count
                ? "Đúng hết! Các từ này đã ra khỏi danh sách từ sai."
                : "Từ trả lời đúng đã ra khỏi danh sách từ sai; từ còn sai vẫn ở lại để học lại sau.",
            _ => correct == _answers.Count
                ? "Tuyệt vời, đúng hết! Lịch ôn tập đã được giãn ra."
                : "Từ làm sai sẽ quay lại vào ngày mai và xuất hiện trước trên taskbar hôm nay."
        };
        _results = _answers.Select(a =>
        {
            var due = a.Word.Review?.Due.Date ?? today.AddDays(1);
            var days = (due - today).Days;
            var next = days <= 1 ? "ôn lại ngày mai" : $"ôn lại sau {days} ngày";
            return new ResultRow(a.Word, a.Correct, a.Correct ? "✓" : "✗", a.Correct ? Good : Bad, $"  ·  {a.Word.Meaning}", next);
        }).ToList();
        ResultList.ItemsSource = _results;
        LearnedPanel.Visibility = _results.Any(r => !r.Source.Learned) ? Visibility.Visible : Visibility.Collapsed;
        TickCorrectButton.Visibility = _results.Any(r => r.Correct) ? Visibility.Visible : Visibility.Collapsed;
        LearnedHint.Text = _mode == SessionMode.Course
            ? "Tích “Đã thuộc” cho từ bạn đã nhớ chắc. Thuộc hết từ của ngày thì lộ trình sang ngày tiếp."
            : "Tích “Đã thuộc” cho từ bạn đã nhớ chắc — từ đó sẽ không hiện nữa.";
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = true;
        NextButton.Content = "Xong";
        FooterText.Text = "Đã lưu kết quả.";
    }

    private void TickCorrect_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _results.Where(r => r.Correct)) row.Learned = true;
        ResultList.Items.Refresh();
    }

    /// <summary>Saves the "Đã thuộc" ticks once, when the window closes (so a tick can still be cleared before).</summary>
    private void ApplyLearned()
    {
        var learned = _results.Where(r => r.Learned && !r.Source.Learned).Select(r => r.Source).Distinct().ToList();
        _results = [];
        if (learned.Count > 0) _onLearned(learned);
    }

    private void Next_Click(object sender, RoutedEventArgs e) => GoNext();

    private void GoNext()
    {
        switch (_stage)
        {
            case Stage.Learn when _index < _learn.Count - 1:
                _index++; _flipped = false; Render(); break;
            case Stage.Learn:
                _flipped = false; Show(Stage.Quiz); break;
            case Stage.Quiz when _picked is null:
                break;
            case Stage.Quiz when _index < _quiz.Count - 1:
                _index++; _picked = null; Render(); break;
            case Stage.Quiz:
                // Save before showing the result so the "next review" dates are the real ones.
                _onFinished(_answers, _newWords);
                Show(Stage.Result);
                break;
            default:
                Close(); break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_stage != Stage.Learn || _index == 0) return;
        _index--; _flipped = false; Render();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e) { _flipped = !_flipped; RenderCard(); }

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Learn) _speak(_learn[_index].Text);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_stage == Stage.Learn && e.Key == Key.Space) { _flipped = !_flipped; RenderCard(); e.Handled = true; }
        else if (_stage == Stage.Learn && e.Key == Key.Left) { Back_Click(this, e); e.Handled = true; }
        else if (_stage == Stage.Learn && e.Key == Key.Right) { GoNext(); e.Handled = true; }
        else if (_stage == Stage.Quiz && e.Key == Key.Enter && _picked is null && _quiz[_index].Kind == QuizKind.Type)
        {
            TypeCheck_Click(this, e);
            e.Handled = true;
        }
        else if (_stage == Stage.Quiz && e.Key is >= Key.D1 and <= Key.D4 && _picked is null && _quiz[_index].Kind != QuizKind.Type)
        {
            var i = e.Key - Key.D1;
            if (i < _quiz[_index].Options.Count) Pick(_quiz[_index].Options[i]);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && NextButton.IsEnabled) { GoNext(); e.Handled = true; }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
