using Voca.Models;

namespace Voca.Services;

/// <summary>
/// Simplified SM-2 spaced repetition with two answers. "Known" pushes the next review out
/// (2 days, 6 days, then interval × ease); "not known" brings the word back tomorrow.
/// </summary>
public static class ReviewScheduler
{
    public const double MasteredIntervalDays = 21;
    private const double MinEase = 1.3;

    public static string Key(DateTime date) => date.ToString("yyyy-MM-dd");

    /// <summary>
    /// Records an answer and updates the day's log. Answering again on the same day replaces the
    /// earlier answer instead of counting twice.
    /// </summary>
    public static void Rate(AppData data, Word word, bool known, DateTime now)
    {
        var today = now.Date;
        var log = data.StudyLog.TryGetValue(Key(today), out var existing) ? existing : data.StudyLog[Key(today)] = new DayLog();

        var ratedToday = IsRatedToday(word, today);
        if (ratedToday)
        {
            if (word.Review!.LastKnown) log.Known--; else { log.Forgot--; word.ForgotCount--; }
        }
        var start = ratedToday ? word.ReviewBeforeToday : word.Review;
        word.ReviewBeforeToday = start?.Clone();

        var state = start?.Clone() ?? new ReviewState();
        if (known)
        {
            state.Reps++;
            state.IntervalDays = state.Reps switch
            {
                1 => 2,
                2 => 6,
                _ => Math.Max(state.IntervalDays + 1, Math.Round(state.IntervalDays * state.Ease))
            };
            log.Known++;
        }
        else
        {
            if (state.Reps > 0) state.Lapses++;
            state.Reps = 0;
            state.IntervalDays = 1;
            state.Ease = Math.Max(MinEase, state.Ease - 0.2);
            word.ForgotCount++;
            log.Forgot++;
        }
        state.Due = today.AddDays(state.IntervalDays);
        state.LastReviewed = now;
        state.LastKnown = known;
        word.Review = state;
    }

    /// <summary>The review state as it was when the day started (ignoring an answer given today).</summary>
    public static ReviewState? StateAtStartOf(Word word, DateTime today) =>
        IsRatedToday(word, today) ? word.ReviewBeforeToday : word.Review;

    public static bool IsRatedToday(Word word, DateTime today) =>
        word.Review is { } current && current.LastReviewed.Date == today.Date;

    public static bool IsKnownToday(Word word, DateTime today) => IsRatedToday(word, today) && word.Review!.LastKnown;

    /// <summary>Answered wrong today: shown first on the taskbar for extra exposure.</summary>
    public static bool IsWrongToday(Word word, DateTime today) => IsRatedToday(word, today) && !word.Review!.LastKnown;

    /// <summary>A learned word whose next review was due today or earlier (as of the start of the day).</summary>
    public static bool IsDueReview(Word word, DateTime today) =>
        StateAtStartOf(word, today) is { } state && state.Due.Date <= today.Date;

    public static bool IsMastered(Word word) => word.Review is { IntervalDays: >= MasteredIntervalDays };
}
