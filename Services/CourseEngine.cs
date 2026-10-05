using Voca.Models;

namespace Voca.Services;

/// <summary>A quiz question: pick the word for a meaning, or the meaning for a word.</summary>
public sealed record QuizQuestion(Word Word, bool AskWord, string Prompt, string Correct, IReadOnlyList<string> Options);

/// <summary>
/// The course rules as pure functions over <see cref="AppData"/>: position, today's words, day/plan
/// advancement, course ordering, the daily quiz and review plans.
/// </summary>
public static class CourseEngine
{
    public const int MaxQuizQuestions = 40;

    // ---------- course and position ----------

    public static IEnumerable<Plan> CoursePlans(AppData data) =>
        data.Course.Select(id => data.Plans.FirstOrDefault(p => p.Id == id)).OfType<Plan>();

    public static (Plan Plan, int Index)? Current(AppData data)
    {
        if (data.Position.PlanId is not Guid id) return null;
        var index = data.Course.IndexOf(id);
        var plan = data.Plans.FirstOrDefault(p => p.Id == id);
        return index < 0 || plan is null ? null : (plan, index);
    }

    /// <summary>Words introduced on the current day of the current plan.</summary>
    public static List<Word> NewWords(AppData data) =>
        Current(data) is var (plan, _) && !data.Position.Finished
            ? plan.Words.Where(w => w.Day == data.Position.Day).ToList()
            : [];

    /// <summary>
    /// Today's words: the current day's new words (or, on a mistake day, its wrong words instead), then
    /// due reviews from every plan (most overdue first, capped by MaxReviewsPerDay). Words answered today
    /// stay until tomorrow.
    /// </summary>
    public static List<Word> BuildToday(AppData data, DateTime today, out HashSet<Word> fresh)
    {
        var mistakes = MistakeDays.Active(data, today) is { } day ? MistakeDays.WordsOf(data, day) : [];
        var newWords = mistakes.Count > 0 ? mistakes : NewWords(data);
        fresh = newWords.ToHashSet();
        var freshSet = fresh;
        var reviews = data.Plans.SelectMany(p => p.Words)
            .Where(w => !freshSet.Contains(w) && ReviewScheduler.IsDueReview(w, today))
            .OrderBy(w => ReviewScheduler.StateAtStartOf(w, today)!.Due)
            .Take(Math.Max(0, data.Settings.MaxReviewsPerDay));
        return [.. newWords, .. reviews];
    }

    public static void SetPosition(AppData data, Guid planId, int day)
    {
        var plan = data.Plans.First(p => p.Id == planId);
        if (!data.Course.Contains(planId)) data.Course.Add(planId);
        data.Position.PlanId = planId;
        data.Position.Day = Math.Clamp(day, 1, Math.Max(1, plan.DayCount));
        data.Position.DayCompleted = false;
        data.Position.DayCompletedOn = null;
        data.Position.Finished = false;
    }

    /// <summary>Starts the course at its first plan, or repairs a position whose plan left the course.</summary>
    public static void EnsurePosition(AppData data)
    {
        if (Current(data) is not null) return;
        var first = CoursePlans(data).FirstOrDefault();
        if (first is null)
        {
            data.Position = new CourseState();
            return;
        }
        SetPosition(data, first.Id, 1);
    }

    /// <summary>
    /// Moves to the next day once a finished day is in the past (at most one day per calendar day, so
    /// a missed day is caught up instead of skipped). Crossing the end of a plan moves to the next plan
    /// in the course and queues its summary. Returns true when the position changed.
    /// </summary>
    public static bool Advance(AppData data, DateTime today)
    {
        var position = data.Position;
        if (!position.DayCompleted || position.DayCompletedOn is not DateTime done || done.Date >= today.Date) return false;
        if (Current(data) is not var (plan, index)) return false;

        position.DayCompleted = false;
        position.DayCompletedOn = null;
        if (position.Day < plan.DayCount)
        {
            position.Day++;
            return true;
        }
        position.PendingSummaryPlanId = plan.Id;
        if (index + 1 < data.Course.Count)
        {
            position.PlanId = data.Course[index + 1];
            position.Day = 1;
        }
        else
        {
            position.Finished = true;
        }
        return true;
    }

    /// <summary>State of a plan in the course relative to the learner: done, current or waiting.</summary>
    public static string StateOf(AppData data, Guid planId)
    {
        var index = data.Course.IndexOf(planId);
        if (index < 0) return "out";
        var current = Current(data)?.Index ?? -1;
        if (current < 0) return "wait";
        if (index < current || (index == current && data.Position.Finished)) return "done";
        return index == current ? "now" : "wait";
    }

    /// <summary>Moves a waiting plan up or down; plans already studied or being studied stay put.</summary>
    public static bool Move(AppData data, Guid planId, int delta)
    {
        var index = data.Course.IndexOf(planId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= data.Course.Count) return false;
        if (StateOf(data, planId) != "wait" || StateOf(data, data.Course[target]) != "wait") return false;
        (data.Course[index], data.Course[target]) = (data.Course[target], data.Course[index]);
        return true;
    }

    public static void AddToCourse(AppData data, Guid planId)
    {
        if (!data.Course.Contains(planId)) data.Course.Add(planId);
        if (data.Position.Finished && Current(data) is var (_, index) && index == data.Course.Count - 2)
        {
            // The course had been finished: continue with the plan just added.
            data.Position.Finished = false;
            data.Position.PlanId = planId;
            data.Position.Day = 1;
        }
        EnsurePosition(data);
    }

    public static void RemoveFromCourse(AppData data, Guid planId)
    {
        data.Course.Remove(planId);
        EnsurePosition(data);
    }

    public static void DeletePlan(AppData data, Guid planId)
    {
        data.Plans.RemoveAll(p => p.Id == planId);
        data.Course.Remove(planId);
        if (data.Position.PendingSummaryPlanId == planId) data.Position.PendingSummaryPlanId = null;
        EnsurePosition(data);
    }

    /// <summary>
    /// Creates a review plan from the most forgotten words (fresh copies, so the originals keep their
    /// history) and puts it right after the plan being studied. Returns null when nothing was forgotten.
    /// </summary>
    public static Plan? CreateReviewPlan(AppData data, int count, int perDay, DateTime today)
    {
        var forgotten = data.Plans.SelectMany(p => p.Words).Where(w => w.ForgotCount > 0)
            .OrderByDescending(w => w.ForgotCount).ThenBy(w => w.Review?.IntervalDays ?? 0)
            .GroupBy(w => w.Text.Trim().ToLowerInvariant()).Select(g => g.First())
            .Take(Math.Clamp(count, 5, 100)).ToList();
        if (forgotten.Count == 0) return null;
        perDay = Math.Clamp(perDay, 5, 50);
        var plan = new Plan
        {
            Name = $"Ôn tập · {forgotten.Count} từ hay quên ({today:dd/MM})",
            Level = "Ôn tập",
            Description = "Tạo tự động từ những từ bạn hay trả lời sai."
        };
        for (var k = 0; k < forgotten.Count; k++)
        {
            var w = forgotten[k];
            plan.Words.Add(new Word { Day = k / perDay + 1, Text = w.Text, Phonetic = w.Phonetic, PartOfSpeech = w.PartOfSpeech, Meaning = w.Meaning, Example = w.Example });
        }
        data.Plans.Add(plan);
        var current = Current(data)?.Index ?? data.Course.Count - 1;
        data.Course.Insert(Math.Min(current + 1, data.Course.Count), plan.Id);
        if (data.Position.Finished)
        {
            data.Position.Finished = false;
            data.Position.PlanId = plan.Id;
            data.Position.Day = 1;
        }
        EnsurePosition(data);
        return plan;
    }

    /// <summary>
    /// The plan that is "the same" as an incoming one: same name, or (renamed on either side) at least
    /// 80% of the same words. Used when importing so nothing is duplicated.
    /// </summary>
    public static Plan? FindMatchingPlan(AppData data, string name, IEnumerable<string> wordTexts)
    {
        var byName = data.Plans.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        var texts = wordTexts.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
        if (texts.Count == 0) return null;
        return data.Plans.FirstOrDefault(p => p.Words.Count > 0 &&
            p.Words.Count(w => texts.Contains(w.Text.Trim().ToLowerInvariant())) >= 0.8 * Math.Max(p.Words.Count, texts.Count));
    }

    /// <summary>Finds the word in <paramref name="plan"/> that corresponds to an incoming word (same day first).</summary>
    public static Word? FindMatchingWord(Plan plan, string text, int day) =>
        plan.Words.FirstOrDefault(w => w.Day == day && string.Equals(w.Text.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? plan.Words.FirstOrDefault(w => string.Equals(w.Text.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---------- session ----------

    /// <summary>Today's quiz: every given word once, alternating meaning→word and word→meaning.</summary>
    public static List<QuizQuestion> BuildQuiz(AppData data, IReadOnlyList<Word> words, Random random)
    {
        var owner = data.Plans.SelectMany(p => p.Words.Select(w => (w, p))).ToDictionary(x => x.w, x => x.p);
        var all = owner.Keys.Where(w => w.Meaning.Length > 0).ToList();
        var questions = new List<QuizQuestion>();
        var k = 0;
        foreach (var word in words.Take(MaxQuizQuestions))
        {
            var askWord = k++ % 2 == 0;
            var plan = owner.GetValueOrDefault(word);
            var pool = all.Where(w => w != word && !string.Equals(w.Text, word.Text, StringComparison.OrdinalIgnoreCase) && w.Meaning != word.Meaning)
                .OrderBy(w => owner[w] == plan && w.Day == word.Day ? 0 : owner[w] == plan ? 1 : 2)
                .ThenBy(_ => random.Next())
                .Take(12).OrderBy(_ => random.Next())
                .Select(w => askWord ? w.Text : w.Meaning).Distinct().Take(3).ToList();
            var correct = askWord ? word.Text : word.Meaning;
            var options = pool.Where(o => o != correct).Append(correct).OrderBy(_ => random.Next()).ToList();
            questions.Add(new QuizQuestion(word, askWord, askWord ? word.Meaning : word.Text, correct, options));
        }
        return questions;
    }

    /// <summary>
    /// Records a finished session: every answer updates spaced repetition, wrong answers are kept for a
    /// mistake day, new words are stamped as introduced today and the day is marked complete (so tomorrow
    /// moves on). On a mistake day the mistake day is finished instead and the course day stays open.
    /// </summary>
    public static void ApplySession(AppData data, IReadOnlyList<(Word Word, bool Correct)> answers,
        IReadOnlyCollection<Word> newWords, DateTime now)
    {
        foreach (var (word, correct) in answers)
        {
            ReviewScheduler.Rate(data, word, correct, now);
            if (correct) word.CorrectCount++;
        }
        foreach (var word in newWords) word.IntroducedOn ??= now.Date;

        MistakeDays.Record(data, answers.Where(a => !a.Correct).Select(a => a.Word), now);

        var key = ReviewScheduler.Key(now);
        var log = data.StudyLog.TryGetValue(key, out var existing) ? existing : data.StudyLog[key] = new DayLog();
        log.SessionDone = true;
        if (MistakeDays.Complete(data, answers, now)) return;
        if (Current(data) is not null && !data.Position.Finished)
        {
            data.Position.DayCompleted = true;
            data.Position.DayCompletedOn = now.Date;
        }
    }
}
