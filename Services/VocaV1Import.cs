using System.IO;
using System.Text.Json;
using Voca.Models;

namespace Voca.Services;

public sealed record V1ImportResult(int PlansAdded, int PlansMatched, int WordsWithProgress, int StudyDays, bool PositionRestored, string? AddedNames);

/// <summary>
/// Brings data over from Voca 1 (VocaTaskbar 1.x), whose local file caches every plan it synced from the
/// server plus the learner's progress. Plans already present here (same name) only receive progress;
/// other plans are added to the end of the course. Safe to run more than once.
/// </summary>
public static class VocaV1Import
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocaTaskbar", "vocabulary.json");

    public static bool Available(string? path = null) => File.Exists(path ?? DefaultPath);

    public static V1ImportResult Import(AppData data, string? path = null, bool includeSettings = false)
    {
        var v1 = JsonSerializer.Deserialize<V1Data>(File.ReadAllText(path ?? DefaultPath))
                 ?? throw new InvalidDataException("File dữ liệu Voca 1 rỗng.");
        var bySet = v1.Words.Where(w => w.RemoteSetId is not null).GroupBy(w => w.RemoteSetId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        // Plans in Voca 1's course order, then any synced plan that was not in the cached course.
        var entries = v1.CourseCache.Select(c => (c.SetId, c.Name, c.Level, c.DayTitles)).ToList();
        foreach (var (setId, words) in bySet.Where(kv => entries.All(e => e.SetId != kv.Key)))
            entries.Add((setId, words[0].SetName, "", new Dictionary<int, string>()));

        var setToPlan = new Dictionary<int, Guid>();
        int added = 0, matched = 0, withProgress = 0;
        var addedNames = new List<string>();
        foreach (var (setId, name, level, titles) in entries)
        {
            if (!bySet.TryGetValue(setId, out var words) || words.Count == 0) continue;
            var plan = CourseEngine.FindMatchingPlan(data, name, words.Select(w => w.Word));
            if (plan is null)
            {
                plan = new Plan { Name = name, Level = level ?? "", DayTitles = new Dictionary<int, string>(titles ?? []) };
                foreach (var w in words.Where(w => w.DayNumber is not null))
                    plan.Words.Add(ToWord(w));
                foreach (var w in words.Where(w => !string.IsNullOrEmpty(w.DayTitle) && w.DayNumber is int day))
                    plan.DayTitles.TryAdd(w.DayNumber!.Value, w.DayTitle);
                data.Plans.Add(plan);
                if (!data.Course.Contains(plan.Id)) data.Course.Add(plan.Id);
                added++;
                addedNames.Add(name);
                withProgress += plan.Words.Count(x => x.Review is not null);
            }
            else
            {
                matched++;
                foreach (var w in words.Where(w => w.Review is not null || w.ForgotCount > 0 || w.CorrectCount > 0))
                {
                    var target = CourseEngine.FindMatchingWord(plan, w.Word, w.DayNumber ?? 1);
                    if (target is null) continue;
                    CopyProgress(w, target);
                    withProgress++;
                }
            }
            setToPlan[setId] = plan.Id;
        }

        // Learned words typed in by hand in old versions: keep them reviewable, outside the course.
        var loose = v1.Words.Where(w => w.RemoteSetId is null && w.Review is not null).ToList();
        if (loose.Count > 0)
        {
            const string looseName = "Từ đã học ở Voca 1";
            var plan = data.Plans.FirstOrDefault(p => p.Name == looseName);
            if (plan is null)
            {
                plan = new Plan { Name = looseName, Description = "Từ nhập tay ở phiên bản cũ, giữ lại để tiếp tục ôn tập." };
                data.Plans.Add(plan);
            }
            foreach (var w in loose)
            {
                var target = plan.Words.FirstOrDefault(x => Same(x.Text, w.Word));
                if (target is null) { target = ToWord(w); target.Day = 1; plan.Words.Add(target); }
                else CopyProgress(w, target);
                withProgress++;
            }
        }

        foreach (var (day, log) in v1.StudyLog)
        {
            if (!data.StudyLog.TryGetValue(day, out var mine)) { data.StudyLog[day] = new DayLog { Known = log.Known, Forgot = log.Forgot, SessionDone = log.SessionDone }; continue; }
            mine.Known = Math.Max(mine.Known, log.Known);
            mine.Forgot = Math.Max(mine.Forgot, log.Forgot);
            mine.SessionDone |= log.SessionDone;
        }

        var restored = false;
        if (v1.Course?.CurrentSetId is int current && setToPlan.TryGetValue(current, out var planId))
        {
            data.Position = new CourseState
            {
                PlanId = planId,
                Day = Math.Max(1, v1.Course.CurrentDay),
                DayCompleted = v1.Course.DayCompleted,
                DayCompletedOn = v1.Course.DayCompletedOn,
                Finished = v1.Course.Finished,
                PendingSummaryPlanId = v1.Course.PendingSummarySetId is int s && setToPlan.TryGetValue(s, out var p) ? p : null
            };
            if (!data.Course.Contains(planId)) data.Course.Add(planId);
            restored = true;
        }
        CourseEngine.EnsurePosition(data);

        if (includeSettings)
        {
            data.Settings.RotationSeconds = v1.RotationSeconds > 0 ? v1.RotationSeconds : data.Settings.RotationSeconds;
            data.Settings.RevealSeconds = v1.RevealSeconds > 0 ? v1.RevealSeconds : data.Settings.RevealSeconds;
            data.Settings.MaxReviewsPerDay = v1.MaxReviewsPerDay >= 0 ? v1.MaxReviewsPerDay : data.Settings.MaxReviewsPerDay;
            if (!string.IsNullOrEmpty(v1.DisplayMode)) data.Settings.DisplayMode = v1.DisplayMode;
            data.Settings.PillLeft ??= v1.WordPillLeft;
            data.Settings.PillTop ??= v1.WordPillTop;
        }
        data.ImportedFromV1 = true;
        return new V1ImportResult(added, matched, withProgress, v1.StudyLog.Count, restored,
            addedNames.Count > 0 ? string.Join(", ", addedNames) : null);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Word ToWord(V1Word w)
    {
        var word = new Word
        {
            Day = w.DayNumber ?? 1, Text = w.Word, Phonetic = w.Phonetic, PartOfSpeech = w.PartOfSpeech,
            Meaning = w.Meaning, Example = w.Example
        };
        CopyProgress(w, word);
        return word;
    }

    private static void CopyProgress(V1Word from, Word to)
    {
        to.Review = from.Review?.Clone();
        to.ReviewBeforeToday = from.ReviewBeforeToday?.Clone();
        to.ForgotCount = from.ForgotCount;
        to.CorrectCount = from.CorrectCount;
        if (from.Review is not null && from.StudyDate > new DateTime(2000, 1, 1)) to.IntroducedOn ??= from.StudyDate.Date;
    }

    // Shapes of Voca 1's vocabulary.json (only the fields used here).
    private sealed class V1Data
    {
        public List<V1Word> Words { get; set; } = [];
        public Dictionary<string, DayLog> StudyLog { get; set; } = [];
        public V1Course? Course { get; set; }
        public List<V1CacheEntry> CourseCache { get; set; } = [];
        public int RotationSeconds { get; set; }
        public int RevealSeconds { get; set; }
        public int MaxReviewsPerDay { get; set; } = -1;
        public string? DisplayMode { get; set; }
        public double? WordPillLeft { get; set; }
        public double? WordPillTop { get; set; }
    }

    private sealed class V1Word
    {
        public string Word { get; set; } = "";
        public string Phonetic { get; set; } = "";
        public string PartOfSpeech { get; set; } = "";
        public string Meaning { get; set; } = "";
        public string Example { get; set; } = "";
        public string SetName { get; set; } = "";
        public DateTime StudyDate { get; set; }
        public int? RemoteSetId { get; set; }
        public int? DayNumber { get; set; }
        public string DayTitle { get; set; } = "";
        public ReviewState? Review { get; set; }
        public ReviewState? ReviewBeforeToday { get; set; }
        public int ForgotCount { get; set; }
        public int CorrectCount { get; set; }
    }

    private sealed class V1Course
    {
        public int? CurrentSetId { get; set; }
        public int CurrentDay { get; set; } = 1;
        public bool DayCompleted { get; set; }
        public DateTime? DayCompletedOn { get; set; }
        public int? PendingSummarySetId { get; set; }
        public bool Finished { get; set; }
    }

    private sealed class V1CacheEntry
    {
        public int SetId { get; set; }
        public string Name { get; set; } = "";
        public string Level { get; set; } = "";
        public Dictionary<int, string> DayTitles { get; set; } = [];
    }
}
