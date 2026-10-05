using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>Contents of a .voca file: plans to move to another computer, optionally with learning progress.</summary>
public sealed class VocaPackage
{
    public string Format { get; set; } = Transfer.FormatName;
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public bool IncludesProgress { get; set; }
    /// <summary>Plans in the order they had in the course (plans outside the course come last).</summary>
    public List<Plan> Plans { get; set; } = [];
    /// <summary>Ids (from <see cref="Plans"/>) that were in the course, in study order.</summary>
    public List<Guid> Course { get; set; } = [];
    public CourseState? Position { get; set; }
    public Dictionary<string, DayLog> StudyLog { get; set; } = [];
}

public sealed record TransferResult(int PlansAdded, int PlansMatched, int WordsUpdated, bool PositionRestored, string AddedNames);

/// <summary>
/// Moving word lists between computers: a .voca file (plans, optionally progress) or plain text holding
/// several plans in the import form. Importing never duplicates a plan that already exists.
/// </summary>
public static partial class Transfer
{
    public const string FormatName = "voca-package";
    public const string Extension = ".voca";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---------- export ----------

    public static VocaPackage CreatePackage(AppData data, IReadOnlyCollection<Guid> planIds, bool includeProgress)
    {
        var ordered = data.Course.Where(planIds.Contains)
            .Concat(data.Plans.Select(p => p.Id).Where(id => planIds.Contains(id) && !data.Course.Contains(id)))
            .Select(id => data.Plans.First(p => p.Id == id)).ToList();
        var package = new VocaPackage
        {
            IncludesProgress = includeProgress,
            Plans = ordered.Select(p => Copy(p, includeProgress)).ToList(),
            Course = data.Course.Where(planIds.Contains).ToList()
        };
        if (includeProgress)
        {
            package.StudyLog = data.StudyLog.ToDictionary(kv => kv.Key, kv => new DayLog { Known = kv.Value.Known, Forgot = kv.Value.Forgot, SessionDone = kv.Value.SessionDone });
            if (data.Position.PlanId is Guid current && planIds.Contains(current))
                package.Position = new CourseState
                {
                    PlanId = current, Day = data.Position.Day, DayCompleted = data.Position.DayCompleted,
                    DayCompletedOn = data.Position.DayCompletedOn, Finished = data.Position.Finished
                };
        }
        return package;
    }

    public static void Save(VocaPackage package, string path) => File.WriteAllText(path, JsonSerializer.Serialize(package, Json));

    public static VocaPackage Load(string path)
    {
        var package = JsonSerializer.Deserialize<VocaPackage>(File.ReadAllText(path), Json);
        if (package is null || package.Format != FormatName)
            throw new InvalidDataException("Đây không phải file xuất từ Voca (.voca).");
        return package;
    }

    /// <summary>Several plans as one text in the import form, ready to paste on another computer.</summary>
    public static string ToText(AppData data, IReadOnlyCollection<Guid> planIds) =>
        string.Join("\n\n", CreatePackage(data, planIds, includeProgress: false).Plans.Select(p => PlanFormat.ToMarkdown(p).TrimEnd()));

    // ---------- import ----------

    /// <summary>
    /// Merges a package. Existing plans (same name or ≥ 80% same words) receive progress only when the
    /// incoming answer is newer; other plans are added, joining the end of the course if they were in it.
    /// </summary>
    public static TransferResult Import(AppData data, VocaPackage package, bool restorePosition)
    {
        int added = 0, matched = 0, updated = 0;
        var names = new List<string>();
        var map = new Dictionary<Guid, Guid>();
        foreach (var incoming in package.Plans)
        {
            var target = CourseEngine.FindMatchingPlan(data, incoming.Name, incoming.Words.Select(w => w.Text));
            if (target is null)
            {
                target = Copy(incoming, package.IncludesProgress);
                target.Id = Guid.NewGuid();
                data.Plans.Add(target);
                if (package.Course.Contains(incoming.Id) && !data.Course.Contains(target.Id)) data.Course.Add(target.Id);
                added++;
                names.Add(target.Name);
            }
            else
            {
                matched++;
                if (package.IncludesProgress)
                {
                    foreach (var word in incoming.Words.Where(w => w.Review is not null))
                    {
                        var mine = CourseEngine.FindMatchingWord(target, word.Text, word.Day);
                        if (mine is null || (mine.Review is { } r && r.LastReviewed >= word.Review!.LastReviewed)) continue;
                        CopyProgress(word, mine);
                        updated++;
                    }
                }
            }
            map[incoming.Id] = target.Id;
        }

        foreach (var (day, log) in package.StudyLog)
        {
            if (!data.StudyLog.TryGetValue(day, out var mine)) { data.StudyLog[day] = new DayLog { Known = log.Known, Forgot = log.Forgot, SessionDone = log.SessionDone }; continue; }
            mine.Known = Math.Max(mine.Known, log.Known);
            mine.Forgot = Math.Max(mine.Forgot, log.Forgot);
            mine.SessionDone |= log.SessionDone;
        }

        var restored = false;
        if (restorePosition && package.Position?.PlanId is Guid p && map.TryGetValue(p, out var local))
        {
            if (!data.Course.Contains(local)) data.Course.Add(local);
            data.Position = new CourseState
            {
                PlanId = local, Day = package.Position.Day, DayCompleted = package.Position.DayCompleted,
                DayCompletedOn = package.Position.DayCompletedOn, Finished = package.Position.Finished
            };
            restored = true;
        }
        CourseEngine.EnsurePosition(data);
        return new TransferResult(added, matched, updated, restored, string.Join(", ", names));
    }

    /// <summary>
    /// Splits pasted text holding several plans (each starting with "# Tên bộ từ:") and reads each one.
    /// Text with a single plan returns one result.
    /// </summary>
    public static List<FormReadResult> ReadMany(string? text, PlanRequest? expected = null)
    {
        text ??= "";
        var starts = PlanHeader().Matches(text).Select(m => m.Index).ToList();
        if (starts.Count <= 1) return [PlanFormat.Read(text, expected)];
        // Text before the first header (a greeting, an opening ``` fence) belongs to no plan.
        return starts.Select((start, i) => PlanFormat.Read(text[start..(i + 1 < starts.Count ? starts[i + 1] : text.Length)])).ToList();
    }

    // ---------- helpers ----------

    private static Plan Copy(Plan plan, bool withProgress) => new()
    {
        Id = plan.Id, Name = plan.Name, Level = plan.Level, Description = plan.Description, CreatedAt = plan.CreatedAt,
        DayTitles = new Dictionary<int, string>(plan.DayTitles),
        Words = plan.Words.Select(w =>
        {
            var copy = new Word { Day = w.Day, Text = w.Text, Phonetic = w.Phonetic, PartOfSpeech = w.PartOfSpeech, Meaning = w.Meaning, Example = w.Example };
            if (withProgress) CopyProgress(w, copy);
            return copy;
        }).ToList()
    };

    private static void CopyProgress(Word from, Word to)
    {
        to.Review = from.Review?.Clone();
        to.ReviewBeforeToday = from.ReviewBeforeToday?.Clone();
        to.ForgotCount = from.ForgotCount;
        to.CorrectCount = from.CorrectCount;
        to.IntroducedOn = from.IntroducedOn ?? to.IntroducedOn;
    }

    [GeneratedRegex(@"^#\s*[*_]*(?:Tên bộ từ|Name)[*_]*\s*:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PlanHeader();
}
