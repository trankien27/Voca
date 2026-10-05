using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>Result of reading an import form: the plan (not yet stored) plus blocking errors and warnings.</summary>
public sealed record FormReadResult(Plan? Plan, List<string> Errors, List<string> Warnings)
{
    public bool CanImport => Plan is not null && Errors.Count == 0;
}

/// <summary>What the learner asked for when generating the prompt; used to check the AI's answer.</summary>
public sealed record PlanRequest(string Topic, int Days, int WordsPerDay, string Level, string Extra, string Name);

/// <summary>
/// The import form (Markdown template, or JSON) that AI chats are asked to produce, the prompt that asks
/// for it, and the reverse export. Lines around the form (greetings, ``` fences) are ignored.
/// </summary>
public static partial class PlanFormat
{
    public const int MaxDays = 365;
    public const int MaxWordsPerDay = 200;
    public const int MaxWordsPerPlan = 5000;

    public const string Example = """
        # Tên bộ từ: Travel — 7 ngày × 20 từ
        Cấp độ: B1–B2
        Mô tả: Từ vựng du lịch cho giao tiếp và IELTS

        ## Ngày 1 — Sân bay và chuyến bay

        | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
        |---|---|---|---|---|---|
        | 1 | boarding pass | /ˈbɔːdɪŋ pɑːs/ | n | thẻ lên máy bay | Please show your boarding pass at the gate. |
        | 2 | delay | /dɪˈleɪ/ | n | sự chậm trễ | Our flight had a two-hour delay. |

        ## Ngày 2 — Khách sạn và chỗ ở

        | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
        |---|---|---|---|---|---|
        | 3 | check in | /ˌtʃek ˈɪn/ | phr v | nhận phòng | We checked in at three o'clock. |
        """;

    // ---------- reading ----------

    /// <summary>
    /// Reads a pasted answer. <paramref name="expected"/> (optional) supplies the name/level when the form
    /// has none and the day/word counts to warn about.
    /// </summary>
    public static FormReadResult Read(string? text, PlanRequest? expected = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        text = (text ?? "").Trim();
        if (text.Length == 0) return new(null, ["Chưa dán nội dung."], warnings);

        Plan plan;
        var brace = text.IndexOf('{');
        if (brace >= 0 && text.Contains("\"days\"") && !text.Contains("| STT"))
        {
            try { plan = ReadJson(text[brace..(text.LastIndexOf('}') + 1)]); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return new(null, [$"JSON không hợp lệ: {ex.Message}"], warnings);
            }
        }
        else
        {
            plan = ReadMarkdown(text, expected?.WordsPerDay ?? 0);
        }

        if (string.IsNullOrWhiteSpace(plan.Name)) plan.Name = expected?.Name ?? "";
        if (string.IsNullOrWhiteSpace(plan.Level)) plan.Level = expected?.Level ?? "";

        var days = plan.Words.GroupBy(w => w.Day).OrderBy(g => g.Key).ToList();
        if (plan.Words.Count == 0) errors.Add("Không đọc được từ nào. Kiểm tra form có tiêu đề “## Ngày 1 — …” và bảng 6 cột.");
        if (string.IsNullOrWhiteSpace(plan.Name)) errors.Add("Thiếu tên bộ từ (dòng “# Tên bộ từ: …” ở đầu form).");
        if (days.Count > MaxDays) errors.Add($"Tối đa {MaxDays} ngày.");
        if (plan.Words.Count > MaxWordsPerPlan) errors.Add($"Một bộ từ tối đa {MaxWordsPerPlan} từ.");
        foreach (var day in days)
        {
            if (day.Count() > MaxWordsPerDay) errors.Add($"Ngày {day.Key} có quá {MaxWordsPerDay} từ.");
            var duplicate = day.GroupBy(w => w.Text.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) errors.Add($"Ngày {day.Key} có từ trùng: {duplicate.Key}.");
        }

        if (expected is not null && days.Count > 0)
        {
            if (days.Count != expected.Days) warnings.Add($"Có {days.Count} ngày, yêu cầu là {expected.Days} ngày.");
            foreach (var day in days.Where(d => d.Count() != expected.WordsPerDay))
                warnings.Add($"Ngày {day.Key} có {day.Count()} từ (yêu cầu {expected.WordsPerDay}).");
        }
        if (days.Count > 0 && days.Last().Key != days.Count) warnings.Add("Số ngày không liên tục (thiếu ngày ở giữa).");
        void Missing(Func<Word, string> field, string label)
        {
            var n = plan.Words.Count(w => string.IsNullOrWhiteSpace(field(w)));
            if (n > 0) warnings.Add($"{n} từ thiếu {label}.");
        }
        Missing(w => w.Phonetic, "phiên âm");
        Missing(w => w.Meaning, "nghĩa");
        Missing(w => w.Example, "ví dụ");
        var repeated = plan.Words.GroupBy(w => w.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(w => w.Day).Distinct().Count() > 1).Select(g => g.Key).ToList();
        if (repeated.Count > 0) warnings.Add($"Từ lặp lại giữa các ngày: {string.Join(", ", repeated.Take(10))}{(repeated.Count > 10 ? "…" : "")}.");
        return new(plan, errors, warnings);
    }

    /// <summary>
    /// Rows may be "| # | word /phonetic/ | pos | meaning | example |" or "| # | word | /phonetic/ | pos | meaning | example |".
    /// "## Ngày 2 — Title" (or "Day 2") assigns the following rows to that day; without headings, rows are
    /// split into days of <paramref name="wordsPerDay"/> (all on day 1 when 0). Header lines give name/level/description.
    /// </summary>
    public static Plan ReadMarkdown(string input, int wordsPerDay = 0)
    {
        var plan = new Plan();
        int? currentDay = null;
        var count = 0;
        foreach (var line in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            var field = HeaderField().Match(line);
            if (field.Success && !line.Contains('|'))
            {
                var value = Unwrap(field.Groups["value"].Value);
                switch (field.Groups["key"].Value.ToLowerInvariant())
                {
                    case "tên bộ từ" or "tên" or "name": if (plan.Name.Length == 0) plan.Name = value; break;
                    case "cấp độ" or "trình độ" or "level": if (plan.Level.Length == 0) plan.Level = value; break;
                    default: if (plan.Description.Length == 0) plan.Description = value; break;
                }
                continue;
            }
            var heading = DayHeading().Match(line);
            if (heading.Success)
            {
                currentDay = int.Parse(heading.Groups["day"].Value);
                var title = heading.Groups["title"].Value.Trim();
                if (title.Length > 0) plan.DayTitles.TryAdd(currentDay.Value, title);
                continue;
            }
            if (plan.Name.Length == 0 && currentDay is null && line.StartsWith("# "))
            {
                plan.Name = Unwrap(line[2..]);
                continue;
            }
            if (!line.Contains('|') || SeparatorRow().IsMatch(line)) continue;
            var cells = SplitRow(line);
            if (cells.Length < 5 || !int.TryParse(cells[0], out _)) continue;

            Word word;
            if (PhoneticOnly().IsMatch(cells[2]))
            {
                if (string.IsNullOrWhiteSpace(cells[1])) continue;
                word = new Word { Text = cells[1], Phonetic = NormalizePhonetic(cells[2]), PartOfSpeech = cells[3], Meaning = cells[4], Example = cells.Length > 5 ? cells[5] : "" };
            }
            else
            {
                var match = WordAndPhonetic().Match(cells[1]);
                var text = match.Success ? match.Groups["word"].Value.Trim() : cells[1];
                if (string.IsNullOrWhiteSpace(text)) continue;
                word = new Word { Text = text, Phonetic = match.Success ? NormalizePhonetic(match.Groups["phonetic"].Value) : "", PartOfSpeech = cells[2], Meaning = cells[3], Example = cells[4] };
            }
            word.Day = currentDay ?? (wordsPerDay > 0 ? count / wordsPerDay + 1 : 1);
            plan.Words.Add(word);
            count++;
        }
        return plan;
    }

    private static Plan ReadJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var plan = new Plan { Name = S(root, "name"), Level = S(root, "level"), Description = S(root, "description") };
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            var number = day.GetProperty("day").GetInt32();
            var title = S(day, "title");
            if (title.Length > 0) plan.DayTitles.TryAdd(number, title);
            foreach (var w in day.GetProperty("words").EnumerateArray())
            {
                var text = S(w, "word");
                if (text.Length == 0) continue;
                plan.Words.Add(new Word { Day = number, Text = text, Phonetic = NormalizePhonetic(S(w, "phonetic")), PartOfSpeech = S(w, "partOfSpeech"), Meaning = S(w, "meaning"), Example = S(w, "example") });
            }
        }
        plan.Words = plan.Words.OrderBy(w => w.Day).ToList();
        return plan;
    }

    // ---------- writing ----------

    /// <summary>The plan in the import form, so it can be backed up, shared or imported elsewhere.</summary>
    public static string ToMarkdown(Plan plan, int? onlyDay = null)
    {
        var sb = new StringBuilder();
        if (onlyDay is null)
        {
            sb.AppendLine($"# Tên bộ từ: {plan.Name}");
            if (plan.Level.Length > 0) sb.AppendLine($"Cấp độ: {plan.Level}");
            if (plan.Description.Length > 0) sb.AppendLine($"Mô tả: {plan.Description}");
        }
        var number = 0;
        foreach (var day in plan.Words.GroupBy(w => w.Day).OrderBy(g => g.Key).Where(g => onlyDay is null || g.Key == onlyDay))
        {
            var title = plan.DayTitle(day.Key);
            sb.AppendLine().AppendLine($"## Ngày {day.Key}{(title.Length > 0 ? " — " + title : "")}").AppendLine();
            sb.AppendLine("| STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |").AppendLine("|---|---|---|---|---|---|");
            foreach (var w in day)
                sb.AppendLine($"| {++number} | {Cell(w.Text)} | {Cell(w.Phonetic)} | {Cell(w.PartOfSpeech)} | {Cell(w.Meaning)} | {Cell(w.Example)} |");
        }
        return sb.ToString();

        static string Cell(string value) => value.Replace("|", @"\|").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// Prompt asking any AI chat to answer with the import form. With <paramref name="onlyDay"/>, asks for
    /// that single day and lists <paramref name="avoid"/> words already used elsewhere in the plan.
    /// </summary>
    public static string BuildPrompt(PlanRequest r, int? onlyDay = null, string dayTitle = "", IReadOnlyCollection<string>? avoid = null)
    {
        var scope = onlyDay is int d
            ? $"Viết lại NGÀY {d} (chủ đề con: \"{(dayTitle.Length > 0 ? dayTitle : r.Topic)}\") của lộ trình từ vựng tiếng Anh chủ đề \"{r.Topic}\", đúng {r.WordsPerDay} từ, trình độ {r.Level}."
            : $"Tạo lộ trình học từ vựng tiếng Anh cho người Việt: chủ đề \"{r.Topic}\", {r.Days} ngày, mỗi ngày đúng {r.WordsPerDay} từ, trình độ {r.Level}.\n" +
              $"Chia chủ đề thành {r.Days} chủ đề con (mỗi ngày một chủ đề, tiêu đề tiếng Việt ngắn), đi từ cơ bản đến nâng cao.";
        var sb = new StringBuilder(scope).AppendLine();
        if (r.Extra.Length > 0) sb.AppendLine($"Yêu cầu thêm: {r.Extra}");
        if (avoid is { Count: > 0 }) sb.AppendLine($"Không dùng lại các từ đã có: {string.Join(", ", avoid)}.");
        sb.AppendLine("""

            Quy tắc cho mỗi từ:
            - Từ: từ hoặc cụm từ tiếng Anh, viết thường (trừ danh từ riêng).
            - Phiên âm: IPA giọng Anh-Anh, đặt trong dấu /.../
            - Loại: n, v, adj, adv, phr v, phr, prep, conj
            - Nghĩa: nghĩa tiếng Việt ngắn gọn, đúng nghĩa trong chủ đề.
            - Ví dụ: một câu tiếng Anh tự nhiên có dùng từ đó.
            - Không trùng từ trong toàn bộ lộ trình. Không dùng ký tự | bên trong ô. Ưu tiên từ thông dụng, hữu ích cho giao tiếp và IELTS.
            """);
        sb.AppendLine($"Chỉ trả lời bằng MỘT khối code markdown theo đúng form sau, không giải thích gì thêm. STT đánh liên tục{(onlyDay is null ? " qua các ngày" : "")}:");
        sb.AppendLine().AppendLine("```markdown");
        if (onlyDay is null)
            sb.AppendLine($"# Tên bộ từ: {r.Name}").AppendLine($"Cấp độ: {r.Level}").AppendLine("Mô tả: <một câu mô tả ngắn>").AppendLine();
        sb.AppendLine($"## Ngày {onlyDay ?? 1} — <tiêu đề tiếng Việt>").AppendLine();
        sb.AppendLine("| STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |").AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine("| 1 | <từ> | /<IPA>/ | <loại> | <nghĩa> | <câu ví dụ> |");
        if (onlyDay is null) sb.AppendLine().AppendLine("## Ngày 2 — <tiêu đề tiếng Việt>").AppendLine("...");
        sb.AppendLine("```");
        if (onlyDay is null)
            sb.AppendLine().Append("Nếu câu trả lời quá dài, hãy dừng ở cuối một ngày trọn vẹn; khi tôi nhắn \"tiếp tục\", viết tiếp các ngày còn lại theo đúng form (không lặp lại phần đầu).");
        return sb.ToString().TrimEnd();
    }

    // ---------- helpers ----------

    public static string NormalizePhonetic(string value)
    {
        var core = value.Trim().Trim('/', '[', ']').Trim();
        return core.Length == 0 ? "" : $"/{core}/";
    }

    /// <summary>Splits a table row on unescaped pipes; "\|" inside a cell stays a literal pipe.</summary>
    private static string[] SplitRow(string line)
    {
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|') && !line.EndsWith("\\|")) line = line[..^1];
        return UnescapedPipe().Split(line).Select(cell => Clean(cell.Replace("\\|", "|"))).ToArray();
    }

    private static string Clean(string value) => value.Trim().Replace("**", "").Replace("__", "");
    private static string Unwrap(string value) => value.Trim().Trim('*', '_').Trim();

    [GeneratedRegex(@"^#{1,6}\s*(?:Ngày|Ngay|Day)\s*(?<day>\d+)\s*(?:[—–:\-]\s*(?<title>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DayHeading();
    [GeneratedRegex(@"^(?:#+\s*)?[*_]*(?<key>Tên bộ từ|Tên|Name|Cấp độ|Trình độ|Level|Mô tả|Description)[*_]*\s*:\s*[*_]*(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderField();
    [GeneratedRegex(@"^\s*(/[^/]+/|\[[^\]]+\])\s*$")]
    private static partial Regex PhoneticOnly();
    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();
    [GeneratedRegex(@"^\s*(?<word>.+?)\s+(?<phonetic>/[^/]+/)\s*$")]
    private static partial Regex WordAndPhonetic();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}")]
    private static partial Regex SeparatorRow();
}
