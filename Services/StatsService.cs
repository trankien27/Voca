using System.Globalization;
using Voca.Models;

namespace Voca.Services;

public sealed record StudyStats(
    int CurrentStreak,
    int BestStreak,
    int TotalWords,
    int Mastered,
    int Learning,
    int Unstudied,
    int DueToday,
    int AnsweredToday,
    IReadOnlyList<(DateTime Date, int Count)> LastDays,
    IReadOnlyList<(Word Word, Plan Plan)> OftenForgotten);

public static class StatsService
{
    public static StudyStats Compute(AppData data, DateTime today, int days = 30)
    {
        today = today.Date;
        var activeDays = new HashSet<DateTime>();
        foreach (var (key, log) in data.StudyLog)
        {
            if ((log.Total > 0 || log.SessionDone) &&
                DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                activeDays.Add(date.Date);
        }
        var words = data.Plans.SelectMany(p => p.Words.Select(w => (Word: w, Plan: p))).ToList();

        return new StudyStats(
            CurrentStreak(activeDays, today),
            BestStreak(activeDays),
            words.Count,
            words.Count(x => ReviewScheduler.IsMastered(x.Word)),
            words.Count(x => x.Word.Review is not null && !ReviewScheduler.IsMastered(x.Word)),
            words.Count(x => x.Word.Review is null),
            words.Count(x => ReviewScheduler.IsDueReview(x.Word, today)),
            data.StudyLog.GetValueOrDefault(ReviewScheduler.Key(today))?.Total ?? 0,
            Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1))
                .Select(d => (d, data.StudyLog.GetValueOrDefault(ReviewScheduler.Key(d))?.Total ?? 0)).ToList(),
            words.Where(x => x.Word.ForgotCount > 0)
                .OrderByDescending(x => x.Word.ForgotCount).ThenBy(x => x.Word.Text).Take(10).ToList());
    }

    /// <summary>
    /// Consecutive active days ending today. If nothing was studied yet today, the streak ending
    /// yesterday still counts, so it does not reset first thing in the morning.
    /// </summary>
    public static int CurrentStreak(IReadOnlySet<DateTime> activeDays, DateTime today)
    {
        var day = activeDays.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (activeDays.Contains(day)) { streak++; day = day.AddDays(-1); }
        return streak;
    }

    public static int BestStreak(IReadOnlySet<DateTime> activeDays)
    {
        var best = 0;
        foreach (var day in activeDays)
        {
            if (activeDays.Contains(day.AddDays(-1))) continue; // count only from the start of a run
            var length = 0;
            while (activeDays.Contains(day.AddDays(length))) length++;
            best = Math.Max(best, length);
        }
        return best;
    }
}
