using Voca.Models;

namespace Voca.Services;

/// <summary>
/// Wrong answers are kept in <see cref="AppData.Mistakes"/> (per word: how often, when) and grouped in
/// <see cref="AppData.MistakeLists"/> (one list per test, one per day of sessions). A list can be studied
/// right away (a practice session; the course is not affected) or turned into a mistake day, which
/// replaces that day's course lesson. A word leaves every list once it is answered right in either.
/// </summary>
public static class MistakeDays
{
    public const int MaxWords = 30;
    public const string EarlierTitle = "Từ sai trước đây";

    public static string SessionTitle(DateTime now) => $"Phiên học {now:dd/MM}";

    /// <summary>
    /// Counts the wrong answers and puts the words in a list: <paramref name="listId"/> if it exists, else
    /// today's list called <paramref name="title"/> (created when missing). Without a title the words are
    /// only counted. With <paramref name="onlyUnlisted"/> words already in another list are not added
    /// again. Returns the list used, if any.
    /// </summary>
    public static Guid? Record(AppData data, IEnumerable<Word> wrong, DateTime now,
        string? title = null, Guid? listId = null, bool onlyUnlisted = false)
    {
        var words = wrong.Distinct().ToList();
        if (words.Count == 0) return listId;
        foreach (var word in words)
        {
            var entry = data.Mistakes.FirstOrDefault(m => m.WordId == word.Id);
            if (entry is null) data.Mistakes.Add(entry = new MistakeEntry { WordId = word.Id });
            entry.Times++;
            entry.LastWrong = now;
            // A mistake day that has not been studied yet picks up new wrong words too.
            if (data.MistakeDay is { Done: false } day && !day.WordIds.Contains(word.Id) && day.WordIds.Count < MaxWords)
                day.WordIds.Add(word.Id);
        }

        var list = data.MistakeLists.FirstOrDefault(l => l.Id == listId)
                   ?? (title is null ? null : data.MistakeLists.FirstOrDefault(l => l.Title == title && l.CreatedAt.Date == now.Date));
        if (list is null && title is null) return null;
        var listed = onlyUnlisted ? data.MistakeLists.Where(l => l != list).SelectMany(l => l.WordIds).ToHashSet() : [];
        var add = words.Select(w => w.Id).Where(id => !listed.Contains(id)).ToList();
        if (list is null)
        {
            if (add.Count == 0) return null;
            data.MistakeLists.Add(list = new MistakeList { Title = title!, CreatedAt = now });
        }
        foreach (var id in add)
            if (!list.WordIds.Contains(id)) list.WordIds.Add(id);
        return list.Id;
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

    /// <summary>Lists with at least one existing word, newest first.</summary>
    public static List<(MistakeList List, List<Word> Words)> Lists(AppData data)
    {
        var words = WordsById(data);
        return data.MistakeLists
            .Select(l => (l, l.WordIds.Where(words.ContainsKey).Select(id => words[id]).ToList()))
            .Where(x => x.Item2.Count > 0)
            .OrderByDescending(x => x.l.CreatedAt)
            .ToList();
    }

    public static int TimesWrong(AppData data, Word word) => data.Mistakes.FirstOrDefault(m => m.WordId == word.Id)?.Times ?? 0;

    /// <summary>
    /// Keeps lists and the word counts in step: words wrong before lists existed go to
    /// <see cref="EarlierTitle"/>, listed words that left the count leave their lists, empty lists go.
    /// Returns true when something changed.
    /// </summary>
    public static bool Tidy(AppData data, DateTime now)
    {
        var counted = data.Mistakes.Select(m => m.WordId).ToHashSet();
        var changed = false;
        foreach (var list in data.MistakeLists)
            changed |= list.WordIds.RemoveAll(id => !counted.Contains(id)) > 0;
        changed |= data.MistakeLists.RemoveAll(l => l.WordIds.Count == 0) > 0;
        var listed = data.MistakeLists.SelectMany(l => l.WordIds).ToHashSet();
        var loose = data.Mistakes.Where(m => !listed.Contains(m.WordId)).ToList();
        if (loose.Count > 0)
        {
            // Dated by the earliest of its mistakes so it sorts with the lists of that time.
            var since = loose.Min(m => m.LastWrong);
            data.MistakeLists.Add(new MistakeList
            {
                Title = EarlierTitle,
                CreatedAt = since == default ? now : since,
                WordIds = loose.Select(m => m.WordId).ToList()
            });
            changed = true;
        }
        return changed;
    }

    /// <summary>Deletes a list; its words stop counting as wrong unless another list still has them.</summary>
    public static void DeleteList(AppData data, Guid listId)
    {
        data.MistakeLists.RemoveAll(l => l.Id == listId);
        var listed = data.MistakeLists.SelectMany(l => l.WordIds).ToHashSet();
        data.Mistakes.RemoveAll(m => !listed.Contains(m.WordId));
    }

    /// <summary>
    /// Records a practice session over wrong words: answers update spaced repetition, words answered
    /// right leave every list, wrong ones are counted again. The course position is not touched.
    /// </summary>
    public static void ApplyPractice(AppData data, IReadOnlyList<(Word Word, bool Correct)> answers, DateTime now)
    {
        foreach (var (word, correct) in answers)
        {
            ReviewScheduler.Rate(data, word, correct, now);
            if (correct) word.CorrectCount++;
        }
        Record(data, answers.Where(a => !a.Correct).Select(a => a.Word), now);
        Resolve(data, answers);
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
    /// Schedules a mistake day on <paramref name="date"/> with <paramref name="wordIds"/> (default: every
    /// waiting word), up to <see cref="MaxWords"/>. An unfinished day already scheduled keeps its words.
    /// Returns the number of words.
    /// </summary>
    public static int Schedule(AppData data, DateTime date, DateTime today, IEnumerable<Guid>? wordIds = null)
    {
        var keep = data.MistakeDay is { Done: false } current ? current.WordIds : [];
        var ids = keep.Concat(wordIds ?? Waiting(data).Select(x => x.Word.Id)).Distinct().Take(MaxWords).ToList();
        if (ids.Count == 0) return 0;
        data.MistakeDay = new MistakeDay { Date = (date.Date < today.Date ? today : date).Date, WordIds = ids };
        return ids.Count;
    }

    /// <summary>Drops the scheduled day; its words stay in their lists.</summary>
    public static void Cancel(AppData data)
    {
        if (data.MistakeDay is { Done: false }) data.MistakeDay = null;
    }

    /// <summary>
    /// Finishes today's mistake day from a session: words answered right leave the lists (wrong ones
    /// were already counted again). Returns false when today is not a mistake day.
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
        Resolve(data, answers.Where(a => inDay.Contains(a.Word.Id)).ToList(), alsoWrong: answers);
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

    /// <summary>Words answered right (and not also wrong) stop counting and leave every list.</summary>
    private static void Resolve(AppData data, IReadOnlyList<(Word Word, bool Correct)> answers,
        IReadOnlyList<(Word Word, bool Correct)>? alsoWrong = null)
    {
        var wrong = (alsoWrong ?? answers).Where(a => !a.Correct).Select(a => a.Word.Id).ToHashSet();
        var right = answers.Where(a => a.Correct && !wrong.Contains(a.Word.Id)).Select(a => a.Word.Id).ToHashSet();
        data.Mistakes.RemoveAll(m => right.Contains(m.WordId));
        foreach (var list in data.MistakeLists) list.WordIds.RemoveAll(right.Contains);
        data.MistakeLists.RemoveAll(l => l.WordIds.Count == 0);
    }

    private static Dictionary<Guid, Word> WordsById(AppData data)
    {
        var words = new Dictionary<Guid, Word>();
        foreach (var word in data.Plans.SelectMany(p => p.Words)) words.TryAdd(word.Id, word);
        return words;
    }
}
