using Voca.Models;

namespace Voca.Services;

/// <summary>
/// Wrong answers are kept in <see cref="AppData.Mistakes"/>. The learner turns them into a mistake day,
/// today or tomorrow; that day replaces the course lesson (the course pauses and continues the day after).
/// A word leaves the list once it is answered right on a mistake day.
/// </summary>
public static class MistakeDays
{
    /// <summary>The popup suggests a mistake day from this many waiting words.</summary>
    public const int SuggestAt = 5;
    public const int MaxWords = 30;

    public static void Record(AppData data, IEnumerable<Word> wrong, DateTime now)
    {
        foreach (var word in wrong.Distinct())
        {
            var entry = data.Mistakes.FirstOrDefault(m => m.WordId == word.Id);
            if (entry is null) data.Mistakes.Add(entry = new MistakeEntry { WordId = word.Id });
            entry.Times++;
            entry.LastWrong = now;
            // A mistake day that has not been studied yet picks up new wrong words too.
            if (data.MistakeDay is { Done: false } day && !day.WordIds.Contains(word.Id) && day.WordIds.Count < MaxWords)
                day.WordIds.Add(word.Id);
        }
    }

    /// <summary>Waiting words, most often wrong first, then most recent; words deleted from plans are skipped.</summary>
    public static List<(Word Word, MistakeEntry Entry)> Waiting(AppData data)
    {
        var words = WordsById(data);
        return data.Mistakes
            .Where(m => words.ContainsKey(m.WordId))
            .OrderByDescending(m => m.Times).ThenByDescending(m => m.LastWrong)
            .Select(m => (words[m.WordId], m))
            .ToList();
    }

    /// <summary>Waiting words not yet in the scheduled (unfinished) mistake day.</summary>
    public static int NotScheduledCount(AppData data)
    {
        var scheduled = data.MistakeDay is { Done: false } day ? day.WordIds.ToHashSet() : [];
        return Waiting(data).Count(x => !scheduled.Contains(x.Word.Id));
    }

    /// <summary>The scheduled day that is still to come (date after today, not done).</summary>
    public static MistakeDay? Upcoming(AppData data, DateTime today) =>
        data.MistakeDay is { Done: false } day && day.Date.Date > today.Date ? day : null;

    /// <summary>
    /// The mistake day that is today's lesson: scheduled for today or a missed earlier date, or finished
    /// today (its words stay on the taskbar until tomorrow).
    /// </summary>
    public static MistakeDay? Active(AppData data, DateTime today) =>
        data.MistakeDay is { } day && day.Date.Date <= today.Date && (!day.Done || day.DoneOn?.Date == today.Date) ? day : null;

    public static List<Word> WordsOf(AppData data, MistakeDay day)
    {
        var words = WordsById(data);
        return day.WordIds.Where(words.ContainsKey).Select(id => words[id]).ToList();
    }

    /// <summary>
    /// Schedules a mistake day on <paramref name="date"/> with the waiting words (up to <see cref="MaxWords"/>).
    /// An unfinished day already scheduled keeps its words and gets the new ones. Returns the number of words.
    /// </summary>
    public static int Schedule(AppData data, DateTime date, DateTime today)
    {
        var keep = data.MistakeDay is { Done: false } current ? current.WordIds : [];
        var ids = keep.Concat(Waiting(data).Select(x => x.Word.Id)).Distinct().Take(MaxWords).ToList();
        if (ids.Count == 0) return 0;
        data.MistakeDay = new MistakeDay { Date = (date.Date < today.Date ? today : date).Date, WordIds = ids };
        return ids.Count;
    }

    /// <summary>Drops the scheduled day; its words stay in the waiting list.</summary>
    public static void Cancel(AppData data)
    {
        if (data.MistakeDay is { Done: false }) data.MistakeDay = null;
    }

    /// <summary>
    /// Finishes today's mistake day from a session: words answered right leave the waiting list
    /// (wrong ones were already recorded again). Returns false when today is not a mistake day.
    /// </summary>
    public static bool Complete(AppData data, IReadOnlyList<(Word Word, bool Correct)> answers, DateTime now)
    {
        if (Active(data, now.Date) is not { } day) return false;
        if (WordsOf(data, day).Count == 0)
        {
            // Its words were deleted, so today was an ordinary course day.
            data.MistakeDay = null;
            return false;
        }
        var inDay = day.WordIds.ToHashSet();
        var right = answers.Where(a => a.Correct && inDay.Contains(a.Word.Id)).Select(a => a.Word.Id)
            .Except(answers.Where(a => !a.Correct).Select(a => a.Word.Id)).ToHashSet();
        data.Mistakes.RemoveAll(m => right.Contains(m.WordId));
        day.Done = true;
        day.DoneOn = now.Date;
        return true;
    }

    /// <summary>Forgets a mistake day finished before today. Returns true when something changed.</summary>
    public static bool Cleanup(AppData data, DateTime today)
    {
        if (data.MistakeDay is not { Done: true } day || day.DoneOn?.Date >= today.Date) return false;
        data.MistakeDay = null;
        return true;
    }

    private static Dictionary<Guid, Word> WordsById(AppData data)
    {
        var words = new Dictionary<Guid, Word>();
        foreach (var word in data.Plans.SelectMany(p => p.Words)) words.TryAdd(word.Id, word);
        return words;
    }
}
