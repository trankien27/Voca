using System.Globalization;
using Voca.Models;

namespace Voca.Services;

/// <summary>Where a word stands, as shown in the "Tất cả từ" tab.</summary>
public enum WordStatus
{
    /// <summary>Never answered in a session or test.</summary>
    New,
    /// <summary>Answered at least once, next review still ahead.</summary>
    Learning,
    /// <summary>Its review is due today or earlier.</summary>
    Due,
    /// <summary>Remembered long enough that the next review is 21+ days away.</summary>
    Mastered,
    /// <summary>Marked "Đã thuộc" by the learner.</summary>
    Learned,
}

public enum WordFilter { All, Seen, New, Learning, Due, Known, Duplicate }

public enum WordSort { Course, Recent, Alphabet, MostForgotten }

public sealed record WordEntry(Word Word, Plan Plan, WordStatus Status);

/// <summary>Every word of every plan in one list, for the "Tất cả từ" tab: status, search, filter and order.</summary>
public static class AllWords
{
    public static WordStatus StatusOf(Word word, DateTime today) =>
        word.Learned ? WordStatus.Learned
        : word.Review is null ? WordStatus.New
        : ReviewScheduler.IsMastered(word) ? WordStatus.Mastered
        : ReviewScheduler.IsDueReview(word, today) ? WordStatus.Due
        : WordStatus.Learning;

    /// <summary>When the learner last met the word (answered it, marked it, or first studied it); null if never.</summary>
    public static DateTime? LastSeen(Word word) =>
        new[] { word.Review?.LastReviewed, word.LearnedOn, word.IntroducedOn }.Max();

    /// <summary>Course plans in study order first, then the plans outside the course.</summary>
    public static List<Plan> PlansInOrder(AppData data)
    {
        var course = CourseEngine.CoursePlans(data).ToList();
        return [.. course, .. data.Plans.Where(p => !course.Contains(p))];
    }

    /// <summary>
    /// Texts (as <see cref="QuickAdd.Key"/>) written more than once in the library, in any plans or days:
    /// "Look up", "look up." and "LOOK UP" are the same word.
    /// </summary>
    public static HashSet<string> DuplicateKeys(AppData data) =>
        data.Plans.SelectMany(p => p.Words).GroupBy(w => QuickAdd.Key(w.Text)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

    public static List<WordEntry> Find(AppData data, DateTime today, string query = "", WordFilter filter = WordFilter.All,
        Guid? planId = null, WordSort sort = WordSort.Course)
    {
        var plans = PlansInOrder(data);
        var duplicates = filter == WordFilter.Duplicate ? DuplicateKeys(data) : null;
        var entries = plans
            .Where(p => planId is null || p.Id == planId)
            .SelectMany(p => p.Words.OrderBy(w => w.Day).Select(w => new WordEntry(w, p, StatusOf(w, today))))
            .Where(e => (duplicates?.Contains(QuickAdd.Key(e.Word.Text)) ?? Matches(e, filter)) && Matches(e.Word, query.Trim()));
        // Copies of a word sit together (in course order), so they can be compared and the extra ones removed.
        if (duplicates is not null && sort == WordSort.Course)
            return entries.OrderBy(e => QuickAdd.Key(e.Word.Text), StringComparer.CurrentCultureIgnoreCase).ToList();
        return sort switch
        {
            WordSort.Recent => entries.OrderByDescending(e => LastSeen(e.Word) ?? DateTime.MinValue).ToList(),
            WordSort.Alphabet => entries.OrderBy(e => e.Word.Text, StringComparer.CurrentCultureIgnoreCase).ToList(),
            WordSort.MostForgotten => entries.OrderByDescending(e => e.Word.ForgotCount).ThenBy(e => e.Word.Text, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => entries.ToList(),
        };
    }

    private static bool Matches(WordEntry entry, WordFilter filter) => filter switch
    {
        WordFilter.Seen => entry.Status != WordStatus.New || entry.Word.IntroducedOn is not null,
        WordFilter.New => entry.Status == WordStatus.New,
        WordFilter.Learning => entry.Status is WordStatus.Learning or WordStatus.Due,
        WordFilter.Due => entry.Status == WordStatus.Due,
        WordFilter.Known => entry.Status is WordStatus.Learned or WordStatus.Mastered,
        _ => true,
    };

    /// <summary>The word or its meaning contains the query, ignoring case and Vietnamese accents ("truong" finds "trường").</summary>
    public static bool Matches(Word word, string query) =>
        query.Length == 0 || Contains(word.Text, query) || Contains(word.Meaning, query);

    private static bool Contains(string text, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(Fold(text), Fold(query), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    // "đ" has no decomposition, so accent-insensitive comparison alone would not match it to "d".
    private static string Fold(string text) => text.Replace('đ', 'd').Replace('Đ', 'D');
}
