using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>What happened when a copied word was added to "Từ của tôi".</summary>
public sealed record QuickAddResult(string? Text, bool Added, Plan? ExistingPlan, Word? ExistingWord, bool AlreadyWaiting)
{
    public bool Invalid => Text is null;
}

/// <summary>
/// Result of importing the AI's answer for the waiting words. <paramref name="StillWaiting"/> are the waiting
/// words the answer left out or had no dictionary meaning for, each with the reason ("x — AI chưa trả lời").
/// </summary>
public sealed record MyWordsImport(List<Word> Added, List<string> SkippedExisting, int Day, IReadOnlyList<string> Errors,
    IReadOnlyList<string> StillWaiting);

/// <summary>
/// "Từ của tôi": select a word anywhere and press Ctrl+Alt+V. If the library already has it the learner
/// is told where; otherwise they write its meaning (and phonetics, example) and it is saved to the plan
/// "Từ của tôi" (outside the course, one day per date) and enters today's reviews. Words can also wait in
/// <see cref="AppData.Inbox"/> for an AI prompt that fills in all of them at once.
/// </summary>
public static partial class QuickAdd
{
    public const string PlanName = "Từ của tôi";
    public const int MaxLength = 60;
    public const int MaxWords = 6;

    /// <summary>
    /// Cleans copied text into a word or phrase: trims spaces, quotes and end punctuation, lower-cases it
    /// (ALL-CAPS acronyms stay). Null when it does not look like a word or short phrase.
    /// </summary>
    public static string? Normalize(string? copied)
    {
        if (string.IsNullOrWhiteSpace(copied)) return null;
        var text = Whitespace().Replace(copied.Trim(), " ");
        text = text.Trim(' ', '"', '\'', '“', '”', '‘', '’', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '«', '»', '…', '-', '–', '—');
        if (text.Length == 0 || text.Length > MaxLength || text.Split(' ').Length > MaxWords) return null;
        // Digits mean a version, date, code or price ("v2.8.0", "2026"), not a word to learn.
        if (!text.Any(char.IsLetter) || text.Any(c => char.IsDigit(c) || char.IsControl(c) || c is '|' or '/' or '\\' or '<' or '>' or '@' or '=' or '_')) return null;
        var acronym = text.Length > 1 && text.Where(char.IsLetter).All(char.IsUpper);
        return acronym ? text : text.ToLowerInvariant().Replace('’', '\'');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>What two texts must share to be the same word: cleaned like <see cref="Normalize"/>, any case.</summary>
    internal static string Key(string text) => (Normalize(text) ?? text.Trim()).ToLowerInvariant();

    /// <summary>The plan and word in the library with the same text (any plan, learned or not).</summary>
    public static (Plan Plan, Word Word)? FindInLibrary(AppData data, string text)
    {
        var key = Key(text);
        foreach (var plan in data.Plans)
            foreach (var word in plan.Words)
                if (Key(word.Text) == key) return (plan, word);
        return null;
    }

    public static bool IsWaiting(AppData data, string text) => data.Inbox.Any(i => Key(i.Text) == Key(text));

    /// <summary>
    /// Adds the copied text unless it is not a word, is already in the library, or already waits. The subtitle
    /// line it was clicked in (<paramref name="line"/>) is kept as its context; a word already waiting without one gets it.
    /// </summary>
    public static QuickAddResult Add(AppData data, string? copied, DateTime now, string? line = null)
    {
        var text = Normalize(copied);
        if (text is null) return new QuickAddResult(null, false, null, null, false);
        if (FindInLibrary(data, text) is var (plan, word)) return new QuickAddResult(text, false, plan, word, false);
        var context = ContextOf(line, text);
        if (data.Inbox.FirstOrDefault(i => Key(i.Text) == Key(text)) is { } waiting)
        {
            if (waiting.Context.Length == 0) waiting.Context = context;
            return new QuickAddResult(text, false, null, null, true);
        }
        data.Inbox.Add(new InboxWord { Text = text, AddedAt = now, Context = context });
        return new QuickAddResult(text, true, null, null, false);
    }

    /// <summary>What to tell the learner after a word heard in a video was added (<see cref="Add"/>).</summary>
    public static string Describe(QuickAddResult result, string text) => result switch
    {
        { Added: true } => $"Đã lưu “{result.Text}” vào Từ của tôi. Mở Thư viện → Từ của tôi để nhờ AI điền nghĩa.",
        { AlreadyWaiting: true } => $"“{result.Text}” đã nằm trong Từ của tôi.",
        { ExistingPlan: { } plan } => $"“{result.Text}” đã có trong {plan.Name}.",
        _ => $"Không lưu được “{text}”."
    };

    public static void Remove(AppData data, string text) => data.Inbox.RemoveAll(i => Key(i.Text) == Key(text));

    public static string DayTitleFor(DateTime now) => $"Từ thêm {now:dd/MM}";

    /// <summary>
    /// Saves a word written in the Ctrl+Alt+V window to "Từ của tôi" (today's day, created when needed) and
    /// puts it in today's reviews. Null when the text is not a word or the library already has it.
    /// </summary>
    public static Word? SaveWord(AppData data, string text, string phonetic, string partOfSpeech, string meaning, string example, DateTime now,
        string exampleMeaning = "")
    {
        var clean = Normalize(text);
        if (clean is null || meaning.Trim().Length == 0 || FindInLibrary(data, clean) is not null) return null;
        var plan = MyPlan(data, now);
        var title = DayTitleFor(now);
        var day = plan.DayTitles.FirstOrDefault(t => t.Value == title).Key;
        if (day == 0)
        {
            day = plan.DayCount + 1;
            plan.DayTitles[day] = title;
        }
        var word = new Word
        {
            Day = day, Text = clean, Phonetic = PlanFormat.NormalizePhonetic(phonetic), PartOfSpeech = partOfSpeech.Trim(),
            Meaning = meaning.Trim(), Example = example.Trim(), ExampleMeaning = exampleMeaning.Trim()
        };
        StartReviewing(word, now);
        plan.Words.Add(word);
        Remove(data, clean);
        return word;
    }

    /// <summary>A word saved by hand starts in today's reviews (taskbar and session) without waiting for a course day.</summary>
    private static void StartReviewing(Word word, DateTime now)
    {
        word.IntroducedOn ??= now.Date;
        word.Review ??= new ReviewState { Due = now.Date };
    }

    private static Plan MyPlan(AppData data, DateTime now)
    {
        var plan = data.Plans.FirstOrDefault(p => p.Name == PlanName);
        if (plan is not null) return plan;
        plan = new Plan { Name = PlanName, Level = "Tự chọn", Description = "Từ bạn tự thêm bằng Ctrl+Alt+V.", CreatedAt = now };
        data.Plans.Add(plan);
        return plan;
    }
}
