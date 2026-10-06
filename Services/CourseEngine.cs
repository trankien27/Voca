using Voca.Models;

namespace Voca.Services;

/// <summary>What a session question asks.</summary>
public enum QuizKind
{
    /// <summary>Meaning shown, pick the word.</summary>
    ChooseWord,
    /// <summary>Word shown, pick the meaning.</summary>
    ChooseMeaning,
    /// <summary>Example sentence with a gap, pick the word.</summary>
    Cloze,
    /// <summary>Meaning shown, type the word.</summary>
    Type
}

/// <summary>A session question. <see cref="Options"/> is empty for <see cref="QuizKind.Type"/>.</summary>
public sealed record QuizQuestion(Word Word, QuizKind Kind, string Prompt, string Correct, IReadOnlyList<string> Options)
{
    /// <summary>The answer is the English word (so it is spoken after answering).</summary>
    public bool AskWord => Kind != QuizKind.ChooseMeaning;
}

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

    /// <summary>Words of the current day of the current plan (including ones marked learned).</summary>
    public static List<Word> DayWords(AppData data) =>
        Current(data) is var (plan, _) && !data.Position.Finished
            ? plan.Words.Where(w => w.Day == data.Position.Day).ToList()
            : [];

    /// <summary>Words introduced on the current day of the current plan, without those marked learned.</summary>
    public static List<Word> NewWords(AppData data) => DayWords(data).Where(w => !w.Learned).ToList();

    /// <summary>
    /// When every word of today's course day is marked learned there is nothing to study: the day counts
    /// as done (tomorrow moves on). Returns true when it changed the position.
    /// </summary>
    public static bool CompleteIfAllLearned(AppData data, DateTime today)
    {
        var day = DayWords(data);
        if (day.Count == 0 || day.Any(w => !w.Learned) || data.Position.DayCompleted) return false;
        if (MistakeDays.Active(data, today) is { Done: false }) return false;
        data.Position.DayCompleted = true;
        data.Position.DayCompletedOn = today.Date;
        return true;
    }


    /// <summary>
    /// Today's words: the current day's new words (or, on a mistake day, its wrong words instead), then
    /// due reviews from every plan (most overdue first, capped by MaxReviewsPerDay). Words answered today
    /// stay until tomorrow.
    /// </summary>
    public static List<Word> BuildToday(AppData data, DateTime today, out HashSet<Word> fresh)
    {
        var mistakes = MistakeDays.Active(data, today) is { } day ? MistakeDays.WordsOf(data, day).Where(w => !w.Learned).ToList() : [];
        var newWords = mistakes.Count > 0 ? mistakes : NewWords(data);
        fresh = newWords.ToHashSet();
        var freshSet = fresh;
        var reviews = data.Plans.SelectMany(p => p.Words)
            .Where(w => !w.Learned && !freshSet.Contains(w) && ReviewScheduler.IsDueReview(w, today))
            .OrderBy(w => ReviewScheduler.StateAtStartOf(w, today)!.Due)
            .Take(Math.Max(0, data.Settings.MaxReviewsPerDay));
        return [.. newWords, .. reviews];
    }

    public static void SetPosition(AppData data, Guid planId, int day, DateTime? today = null)
    {
        var plan = data.Plans.First(p => p.Id == planId);
        if (!data.Course.Contains(planId)) data.Course.Add(planId);
        data.Position.PlanId = planId;
        data.Position.Day = Math.Clamp(day, 1, Math.Max(1, plan.DayCount));
        data.Position.DayCompleted = false;
        data.Position.DayCompletedOn = null;
        data.Position.DayStartedOn = (today ?? DateTime.Today).Date;
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
    /// Moves to the next day on each new calendar day, whether or not the day's session was done — at most
    /// one day per date the app is opened, so days away are not piled up. Words of an unfinished day that were
    /// never answered go into reviews (due today) instead of being lost. A mistake day pauses the course:
    /// nothing moves that date, and a course day whose own date was taken by a mistake day gets today instead.
    /// Crossing the end of a plan moves to the next plan in the course and queues its summary. Returns true
    /// when the position changed.
    /// </summary>
    public static bool Advance(AppData data, DateTime today)
    {
        today = today.Date;
        var position = data.Position;
        if (Current(data) is not var (plan, index) || position.Finished) return false;
        if (MistakeDays.Active(data, today) is not null)
        {
            var changed = position.PausedOn?.Date != today;
            position.PausedOn = today;
            return changed;
        }

        var started = (position.DayStartedOn ?? InferDayStart(data, today)).Date;
        if (started >= today)
        {
            var changed = position.DayStartedOn?.Date != started;
            position.DayStartedOn = started;
            return changed;
        }
        if (position.PausedOn?.Date == started)
        {
            // The day's own date went to a mistake day: it is studied today instead of being skipped.
            position.DayStartedOn = today;
            return true;
        }

        MoveToNextDay(data, plan, index, today, started);
        return true;
    }

    /// <summary>
    /// "Học tiếp ngày sau" when today's words are done: moves to the next day right away instead of waiting
    /// for tomorrow (tomorrow then moves on again as usual). Not during a mistake day or after the course.
    /// </summary>
    public static bool StudyNextDayNow(AppData data, DateTime today)
    {
        today = today.Date;
        if (Current(data) is not var (plan, index) || data.Position.Finished) return false;
        if (MistakeDays.Active(data, today) is not null) return false;
        MoveToNextDay(data, plan, index, today, (data.Position.DayStartedOn ?? today).Date);
        return true;
    }

    /// <summary>The plan and day that come after the current one (null at the end of the course).</summary>
    public static (Plan Plan, int Day)? NextDay(AppData data)
    {
        if (Current(data) is not var (plan, index) || data.Position.Finished) return null;
        if (data.Position.Day < plan.DayCount) return (plan, data.Position.Day + 1);
        var next = data.Course.Skip(index + 1).Select(id => data.Plans.FirstOrDefault(p => p.Id == id)).OfType<Plan>().FirstOrDefault();
        return next is null ? null : (next, 1);
    }

    /// <summary>
    /// Leaves the current day for the next one. Words of the day that were never answered go into reviews so
    /// they are not lost; past the last day the next plan starts (its summary queued) or the course ends.
    /// </summary>
    private static void MoveToNextDay(AppData data, Plan plan, int index, DateTime today, DateTime started)
    {
        var position = data.Position;
        foreach (var word in plan.Words.Where(w => w.Day == position.Day && !w.Learned && w.Review is null))
        {
            word.IntroducedOn ??= started;
            word.Review = new ReviewState { Due = today };
        }
        position.DayCompleted = false;
        position.DayCompletedOn = null;
        position.DayStartedOn = today;
        if (position.Day < plan.DayCount)
        {
            position.Day++;
            return;
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
    }

    /// <summary>
    /// Data saved before the start date was kept: the day started when it was completed, else on the latest
    /// day anything was studied before today, else today.
    /// </summary>
    private static DateTime InferDayStart(AppData data, DateTime today)
    {
        if (data.Position.DayCompletedOn is DateTime done) return done.Date;
        var studied = data.StudyLog.Keys
            .Select(k => DateTime.TryParse(k, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null)
            .OfType<DateTime>().Where(d => d.Date < today).ToList();
        return studied.Count > 0 ? studied.Max().Date : today;
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

    // ---------- quick rating from the popup ----------

    /// <summary>"Đã thuộc": the word is no longer shown or reviewed, and leaves the mistake lists.</summary>
    public static void MarkLearned(AppData data, Word word, DateTime now)
    {
        word.Learned = true;
        word.LearnedOn = now.Date;
        word.IntroducedOn ??= now.Date;
        MistakeDays.Forget(data, word.Id);
    }

    /// <summary>Undo of <see cref="MarkLearned"/>: the word is shown and reviewed again.</summary>
    public static void UnmarkLearned(Word word)
    {
        word.Learned = false;
        word.LearnedOn = null;
    }

    /// <summary>"Chưa nhớ": counts as a wrong answer (review tomorrow, shown first today, kept as a mistake).</summary>
    public static void MarkNotRemembered(AppData data, Word word, DateTime now)
    {
        ReviewScheduler.Rate(data, word, false, now);
        word.IntroducedOn ??= now.Date;
        MistakeDays.Record(data, [word], now, MistakeDays.SessionTitle(now), onlyUnlisted: true);
    }

    // ---------- session ----------

    /// <summary>
    /// Today's quiz: every given word once. Kinds rotate meaning→word, word→meaning and — with
    /// <paramref name="typing"/> — gap-fill in the example sentence and typing the word. A gap-fill whose
    /// example does not contain the word falls back to meaning→word.
    /// </summary>
    public static List<QuizQuestion> BuildQuiz(AppData data, IReadOnlyList<Word> words, Random random, bool typing = false)
    {
        var owner = data.Plans.SelectMany(p => p.Words.Select(w => (w, p))).ToDictionary(x => x.w, x => x.p);
        var all = owner.Keys.Where(w => w.Meaning.Length > 0).ToList();
        QuizKind[] rotation = typing
            ? [QuizKind.ChooseWord, QuizKind.ChooseMeaning, QuizKind.Cloze, QuizKind.Type]
            : [QuizKind.ChooseWord, QuizKind.ChooseMeaning];
        var questions = new List<QuizQuestion>();
        var k = 0;
        foreach (var word in words.Take(MaxQuizQuestions))
        {
            var kind = rotation[k++ % rotation.Length];
            var gap = kind == QuizKind.Cloze ? Blank(word.Example, word.Text) : null;
            if (kind == QuizKind.Cloze && gap is null) kind = QuizKind.ChooseWord;
            if (kind == QuizKind.Type)
            {
                questions.Add(new QuizQuestion(word, kind, word.Meaning, word.Text, []));
                continue;
            }
            var askWord = kind != QuizKind.ChooseMeaning;
            var plan = owner.GetValueOrDefault(word);
            var pool = all.Where(w => w != word && !string.Equals(w.Text, word.Text, StringComparison.OrdinalIgnoreCase) && w.Meaning != word.Meaning)
                .OrderBy(w => owner[w] == plan && w.Day == word.Day ? 0 : owner[w] == plan ? 1 : 2)
                .ThenBy(_ => random.Next())
                .Take(12).OrderBy(_ => random.Next())
                .Select(w => askWord ? w.Text : w.Meaning).Distinct().Take(3).ToList();
            var correct = askWord ? word.Text : word.Meaning;
            var options = pool.Where(o => o != correct).Append(correct).OrderBy(_ => random.Next()).ToList();
            var prompt = kind switch
            {
                QuizKind.ChooseWord => word.Meaning,
                QuizKind.Cloze => gap!,
                _ => word.Text
            };
            questions.Add(new QuizQuestion(word, kind, prompt, correct, options));
        }
        return questions;
    }

    /// <summary>
    /// The example with the word replaced by a gap ("Please show your _____ at the gate."), allowing simple
    /// endings (delays, delayed, checked in). Null when the sentence does not contain the word.
    /// </summary>
    public static string? Blank(string example, string word)
    {
        var tokens = word.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (example.Trim().Length == 0 || tokens.Length == 0) return null;
        var parts = tokens.Select(t =>
        {
            var token = System.Text.RegularExpressions.Regex.Escape(t);
            return t.Length <= 3 ? $"{token}(?:s|es|d|ed)?" : $"{System.Text.RegularExpressions.Regex.Escape(t[..^1])}\\w{{0,4}}";
        });
        var pattern = $"\\b{string.Join("\\s+", parts)}\\b";
        var match = System.Text.RegularExpressions.Regex.Match(example, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? example[..match.Index] + "_____" + example[(match.Index + match.Length)..] : null;
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

        MistakeDays.Record(data, answers.Where(a => !a.Correct).Select(a => a.Word), now, MistakeDays.SessionTitle(now), onlyUnlisted: true);

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
