using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voca;

/// <summary>
/// A video's subtitles as a text to read: lines joined into paragraphs (<see cref="Subtitles.Paragraphs"/>),
/// with the same marks as the player — new words (click: save to "Từ của tôi"), words being learned (hover:
/// meaning), saved words. Marks and times can be turned off to read plain text.
/// </summary>
public partial class SubtitleReaderWindow : Window
{
    private static readonly Brush NewWash = Frozen(0xFF, 0xF1, 0xC2), NewInk = Frozen(0x8A, 0x53, 0x00);
    private static readonly Brush LearningWash = Frozen(0xEE, 0xEB, 0xFF), LearningInk = Frozen(0x4B, 0x3C, 0xC4);
    private static readonly Brush WaitingWash = Frozen(0xE7, 0xF7, 0xEE), WaitingInk = Frozen(0x06, 0x76, 0x47);
    private static readonly Brush TimeInk = Frozen(0x98, 0xA2, 0xB3), TextInk = Frozen(0x17, 0x20, 0x33);

    private readonly Store _store;
    private readonly IReadOnlyList<SubtitleLine> _lines;
    private readonly IReadOnlyList<VideoWord> _newWords;
    /// <summary>The runs of words that had a mark when the text was laid out; restyled in place when it changes.</summary>
    private readonly List<Run> _marked = [];
    private readonly Dictionary<Run, string> _lineOf = []; // subtitle line of each marked word, its context when saved
    private WordMarker _marker;
    private int _paragraphs, _words;

    public SubtitleReaderWindow(Store store, string name, IReadOnlyList<SubtitleLine> lines, IReadOnlyList<VideoWord> newWords)
    {
        InitializeComponent();
        _store = store;
        _lines = lines.OrderBy(l => l.Start).ToList();
        _newWords = newWords;
        _marker = new WordMarker(store.Data, newWords);
        Title = $"Voca · Đọc · {name}";
        TitleText.Text = name;
        _store.Changed += OnStoreChanged;
        Render();
    }

    private void Window_Closed(object? sender, EventArgs e) => _store.Changed -= OnStoreChanged;

    // A word saved or learned elsewhere (popup, library, the player) changes its mark here too.
    private void OnStoreChanged() => Dispatcher.Invoke(() =>
    {
        _marker = new WordMarker(_store.Data, _newWords);
        Restyle();
    });

    private void Options_Click(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        _marked.Clear();
        _lineOf.Clear();
        var marks = MarkBox.IsChecked == true;
        Legend.Visibility = marks ? Visibility.Visible : Visibility.Collapsed;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Georgia"), FontSize = 19, LineHeight = 33, Foreground = TextInk,
            PagePadding = new Thickness(40, 28, 40, 28), MaxPageWidth = 860, TextAlignment = TextAlignment.Left
        };
        var groups = Subtitles.Paragraphs(_lines);
        foreach (var group in groups)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 20) };
            if (TimeBox.IsChecked == true)
                paragraph.Inlines.Add(new Run(VideoPlayerWindow.Clock(group[0].Start) + "   ")
                    { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = TimeInk });
            for (var i = 0; i < group.Count; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new Run(" "));
                if (!marks) { paragraph.Inlines.Add(new Run(group[i].Text)); continue; }
                foreach (var part in Subtitles.Mark(group[i].Text, _marker)) paragraph.Inlines.Add(Piece(part, group[i].Text));
            }
            document.Blocks.Add(paragraph);
        }
        Reader.Document = document;
        _paragraphs = groups.Count;
        _words = _lines.Sum(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        ShowSummary();
    }

    private Run Piece(SubtitlePart part, string line)
    {
        var run = new Run(part.Text);
        if (part.Mark == WordMark.None) return run;
        run.MouseLeftButtonDown += Word_MouseDown;
        _marked.Add(run);
        _lineOf[run] = line;
        Paint(run, part);
        return run;
    }

    private static void Paint(Run run, SubtitlePart part)
    {
        run.Tag = part;
        (run.Background, run.Foreground, run.FontWeight, run.Cursor, run.ToolTip) = part.Mark switch
        {
            WordMark.New => (NewWash, NewInk, FontWeights.SemiBold, System.Windows.Input.Cursors.Hand,
                (object)"Từ mới — bấm để lưu vào “Từ của tôi”"),
            WordMark.Learning => (LearningWash, LearningInk, FontWeights.SemiBold, null,
                $"{part.Word!.Text} {part.Word.Phonetic} — {part.Word.Meaning}".Trim()),
            WordMark.Waiting => (WaitingWash, WaitingInk, FontWeights.SemiBold, null, "Đã lưu trong “Từ của tôi”, chờ điền nghĩa"),
            _ => ((Brush?)null, (Brush)TextInk, FontWeights.Normal, (System.Windows.Input.Cursor?)null, (object?)null),
        };
    }

    private void Restyle()
    {
        foreach (var run in _marked)
        {
            var (mark, word) = _marker.Of(run.Text);
            Paint(run, new SubtitlePart(run.Text, mark, word));
        }
        ShowSummary();
    }

    /// <summary>A new word clicked goes to "Từ của tôi"; the click does not start a text selection.</summary>
    private void Word_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Run { Tag: SubtitlePart { Mark: WordMark.New } part }) return;
        e.Handled = true;
        StatusText.Text = VideoPlayerWindow.SaveNewWord(_store, part.Text, _lineOf.GetValueOrDefault((Run)sender)); // the store's Changed restyles the text
    }

    private void ShowSummary()
    {
        var text = $"{_paragraphs} đoạn · {_lines.Count} câu · khoảng {_words} từ · đọc chừng {Math.Max(1, (int)Math.Round(_words / 150.0))} phút";
        if (MarkBox.IsChecked == true)
        {
            int Count(WordMark mark) => _marked.Where(r => r.Tag is SubtitlePart p && p.Mark == mark)
                .Select(r => r.Text.ToLowerInvariant()).Distinct().Count();
            text += $" · {Count(WordMark.New)} từ mới · {Count(WordMark.Learning)} từ đang học";
        }
        SummaryText.Text = text;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
