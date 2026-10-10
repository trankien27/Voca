using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>One subtitle cue.</summary>
public sealed record SubtitleLine(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A word heard in a video that is not in the library yet: how often, and the first line it is in.</summary>
public sealed record VideoWord(string Text, int Count, string Example);

/// <summary>
/// Subtitles as plain data: writing and reading .srt, and picking the words of a video worth learning — not
/// in the library under any form, not among the most common English words, not a name.
/// </summary>
public static partial class Subtitles
{
    public const int MinWordLength = 3;
    public const int MaxExampleLength = 160;

    // ---------- .srt ----------

    public static string ToSrt(IEnumerable<SubtitleLine> lines)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var line in lines)
        {
            sb.Append(++n).Append("\r\n");
            sb.Append(SrtTime(line.Start)).Append(" --> ").Append(SrtTime(line.End)).Append("\r\n");
            sb.Append(line.Text.Trim()).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string SrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    /// <summary>Reads an .srt file's text (numbering, blank lines and Windows/Unix line ends tolerated).</summary>
    public static List<SubtitleLine> ReadSrt(string text)
    {
        var lines = new List<SubtitleLine>();
        foreach (var block in BlankLine().Split(text.Replace("\r\n", "\n").Trim()))
        {
            var rows = block.Split('\n', StringSplitOptions.TrimEntries).Where(r => r.Length > 0).ToList();
            var timing = rows.FindIndex(r => r.Contains("-->"));
            if (timing < 0) continue;
            var parts = rows[timing].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1].Split(' ')[0], out var end)) continue;
            var words = string.Join(" ", rows.Skip(timing + 1)).Trim();
            if (words.Length > 0) lines.Add(new SubtitleLine(start, end, words));
        }
        return lines;
    }

    private static bool TryParseTime(string value, out TimeSpan time) =>
        TimeSpan.TryParseExact(value.Replace('.', ','), [@"hh\:mm\:ss\,fff", @"h\:mm\:ss\,fff", @"mm\:ss\,fff"], CultureInfo.InvariantCulture, out time);

    /// <summary>
    /// Tidies recognised lines: trims, drops empty ones and Whisper's sound tags ("[Music]", "(laughs)",
    /// "[BLANK_AUDIO]") so only speech is left.
    /// </summary>
    public static List<SubtitleLine> Clean(IEnumerable<SubtitleLine> lines) =>
        lines.Select(l => l with { Text = Whitespace().Replace(SoundTag().Replace(l.Text, " "), " ").Trim() })
             .Where(l => l.Text.Any(char.IsLetter))
             .ToList();

    // ---------- words to learn ----------

    /// <summary>
    /// Words of the subtitles not in the library (any plan, under any simple form: delays/delayed/delaying
    /// ↔ delay), leaving out very common words, names (only ever capitalised mid-sentence), contractions and
    /// words shorter than <see cref="MinWordLength"/>. Forms of one word are counted together under the form
    /// heard most. Most frequent first, then in order of appearance.
    /// </summary>
    public static List<VideoWord> NewWords(AppData data, IReadOnlyList<SubtitleLine> lines)
    {
        var known = data.Plans.SelectMany(p => p.Words).Select(w => w.Text.Trim().ToLowerInvariant()).ToHashSet();
        var seen = new Dictionary<string, (int Count, int First, string Example, bool Lower, bool CapitalMid)>();
        var order = 0;
        foreach (var line in lines)
        {
            var sentenceStart = true;
            foreach (Match m in Token().Matches(line.Text))
            {
                var raw = m.Value.Replace('’', '\'');
                var atStart = sentenceStart;
                var after = m.Index + m.Length < line.Text.Length ? line.Text[m.Index + m.Length] : ' ';
                sentenceStart = after is '.' or '!' or '?';
                if (raw.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
                if (raw.Contains('\'') || raw.Length < MinWordLength) continue;
                var key = raw.ToLowerInvariant();
                var isLower = raw == key;
                if (!seen.TryGetValue(key, out var e)) e = (0, order++, Example(line.Text), false, false);
                seen[key] = (e.Count + 1, e.First, e.Example, e.Lower || isLower, e.CapitalMid || (!isLower && !atStart));
            }
        }

        // Merge forms into the base form when the video also has it (delays → delay); otherwise keep the form heard.
        var groups = new Dictionary<string, List<string>>();
        foreach (var key in seen.Keys)
        {
            var root = BaseForms(key).FirstOrDefault(seen.ContainsKey) ?? key;
            if (!groups.TryGetValue(root, out var forms)) groups[root] = forms = [];
            forms.Add(key);
        }

        var result = new List<(VideoWord Word, int First)>();
        foreach (var (root, forms) in groups)
        {
            if (forms.Any(f => IsCommon(f) || known.Contains(f) || BaseForms(f).Any(b => known.Contains(b) || Common.Contains(b)))) continue;
            var stats = forms.Select(f => (Form: f, Info: seen[f])).ToList();
            if (stats.All(s => !s.Info.Lower && s.Info.CapitalMid)) continue; // a name
            var first = stats.MinBy(s => s.Info.First);
            var main = stats.MaxBy(s => s.Info.Count).Form;
            result.Add((new VideoWord(stats.Any(s => s.Form == root) ? root : main, stats.Sum(s => s.Info.Count), first.Info.Example), first.Info.First));
        }
        return result.OrderByDescending(r => r.Word.Count).ThenBy(r => r.First).Select(r => r.Word).ToList();
    }

    /// <summary>Possible base forms of an inflected word (delays → delay, studied → study, stopped → stop, making → make).</summary>
    public static IEnumerable<string> BaseForms(string word)
    {
        static bool Double(string w) => w.Length >= 2 && w[^1] == w[^2] && !"aeiouls".Contains(w[^1]);
        if (word.EndsWith("ies") && word.Length > 4) yield return word[..^3] + "y";
        if (word.EndsWith("es") && word.Length > 4) yield return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss") && word.Length > 3) yield return word[..^1];
        if (word.EndsWith("ied") && word.Length > 4) yield return word[..^3] + "y";
        if (word.EndsWith("ed") && word.Length > 4)
        {
            var stem = word[..^2];
            if (Double(stem)) yield return stem[..^1];
            yield return stem;
            yield return word[..^1];
        }
        if (word.EndsWith("ing") && word.Length > 5)
        {
            var stem = word[..^3];
            if (Double(stem)) yield return stem[..^1];
            yield return stem;
            yield return stem + "e";
        }
        if (word.EndsWith("ly") && word.Length > 5) yield return word[..^2];
        if (word.EndsWith("er") && word.Length > 5) yield return word[..^2];
        if (word.EndsWith("est") && word.Length > 6) yield return word[..^3];
    }

    private static bool IsCommon(string word) => Common.Contains(word);

    private static string Example(string text)
    {
        text = text.Trim();
        return text.Length <= MaxExampleLength ? text : text[..MaxExampleLength].TrimEnd() + "…";
    }

    // ---------- prompt for the AI chat ----------

    /// <summary>
    /// Prompt asking an AI chat to fill in the chosen words in the usual import form, in the given order,
    /// <paramref name="perDay"/> words a day, with the meaning the word has in the video.
    /// </summary>
    public static string BuildPrompt(string name, IReadOnlyList<VideoWord> words, int perDay)
    {
        perDay = Math.Clamp(perDay, 5, PlanFormat.MaxWordsPerDay);
        var days = (words.Count + perDay - 1) / perDay;
        var sb = new StringBuilder();
        sb.AppendLine($"Tạo bộ từ vựng tiếng Anh cho người Việt từ {words.Count} từ xuất hiện trong một video. Giữ đúng thứ tự, không thêm hay bớt từ, chia thành {days} ngày, mỗi ngày {perDay} từ (ngày cuối có thể ít hơn).");
        sb.AppendLine("Danh sách (từ — câu trong video):");
        foreach (var w in words) sb.AppendLine($"- {w.Text} — \"{w.Example.Replace('"', '\'')}\"");
        sb.AppendLine("""

            Quy tắc cho mỗi từ:
            - Từ: dạng gốc của từ (động từ nguyên thể, danh từ số ít), viết thường trừ danh từ riêng.
            - Phiên âm: IPA giọng Anh-Anh, đặt trong dấu /.../
            - Loại: n, v, adj, adv, phr v, phr, prep, conj
            - Nghĩa: nghĩa tiếng Việt ngắn gọn, đúng với nghĩa trong câu của video.
            - Ví dụ: dùng câu trong video nếu rõ nghĩa, nếu không thì viết một câu tự nhiên có dùng từ đó.
            - Không dùng ký tự | bên trong ô.
            Chỉ trả lời bằng MỘT khối code markdown theo đúng form sau, không giải thích gì thêm. STT đánh liên tục qua các ngày:
            """);
        sb.AppendLine("```markdown");
        sb.AppendLine($"# Tên bộ từ: {name}").AppendLine("Cấp độ: <cấp độ ước lượng, ví dụ B1–B2>").AppendLine("Mô tả: Từ vựng lấy từ video").AppendLine();
        sb.AppendLine("## Ngày 1 — <tiêu đề tiếng Việt ngắn>").AppendLine();
        sb.AppendLine("| STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |").AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine("| 1 | <từ> | /<IPA>/ | <loại> | <nghĩa> | <câu ví dụ> |");
        if (days > 1) sb.AppendLine().AppendLine("## Ngày 2 — <tiêu đề tiếng Việt ngắn>").AppendLine("...");
        sb.AppendLine("```");
        if (days > 1)
            sb.AppendLine().Append("Nếu câu trả lời quá dài, hãy dừng ở cuối một ngày trọn vẹn; khi tôi nhắn \"tiếp tục\", viết tiếp các ngày còn lại theo đúng form (không lặp lại phần đầu).");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads the AI's answer for <see cref="BuildPrompt"/> into a new plan (not stored yet). Words that are
    /// already in the library — the AI may have turned a form into a word that is — are left out and listed.
    /// </summary>
    public static (FormReadResult Read, List<string> Skipped) ReadPlan(AppData data, string answer, string name, int wordCount, int perDay)
    {
        var days = Math.Max(1, (wordCount + perDay - 1) / perDay);
        var read = PlanFormat.Read(answer, new PlanRequest(name, days, perDay, "", "", name));
        var skipped = new List<string>();
        if (read.Plan is { } plan)
        {
            skipped = plan.Words.Where(w => QuickAdd.FindInLibrary(data, w.Text) is not null).Select(w => w.Text).ToList();
            plan.Words.RemoveAll(w => skipped.Contains(w.Text));
            if (plan.Words.Count == 0 && read.Errors.Count == 0) read.Errors.Add("Mọi từ trong câu trả lời đều đã có trong thư viện.");
        }
        return (read, skipped);
    }

    [GeneratedRegex(@"[A-Za-z]+(?:[-'’][A-Za-z]+)*")]
    private static partial Regex Token();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|♪")]
    private static partial Regex SoundTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLine();

    /// <summary>Very common English words (function words and everyday basics) not worth a lesson.</summary>
    private static readonly HashSet<string> Common = """
        the and for are but not you all any can had her was one our out has him his how its may new now old see
        two way who boy did get got let put say she too use yes yet ago also back been both came come does done
        down each even ever from gave give goes gone good have here into just keep kept know knew last left like
        long look made make many more most much must need next once only over said same seen some such sure take
        than that them then they this time took told tell very want well went were what when will with your
        about above after again along always among another around away because become been before being below
        best better between big bit call called can't could day days didn doesn don done during early either else
        enough every everyone everything few find first found four friend friends full get gets getting girl go
        going great guy guys hand happen happened hard head hear heard help her here hers herself high himself
        home hope hour hours house idea its itself job kind kids lot lots little live lived love man men might
        mind mine minute minutes miss money month months morning mother father mom dad myself name never nice
        night nobody nothing number often okay open other others ours ourselves own part people person place
        play please point pretty quite rather real really right room run saw says school second seem seems
        show side since small something sometimes soon sorry start started still stop story talk talking thank
        thanks that's their theirs themselves there these thing things think thinking third those though thought
        three through today together tomorrow tonight under until upon used using wait walk wanted watch water
        week weeks what's where which while whole whom whose why wife without woman women word words work
        working world would write year years yeah yesterday yourself yourselves able ask asked away bad bring
        brought buy can car change child children city close country course cut different door eat end eye eyes
        face fact family far feel feeling felt fine five game gonna gotta hey hi hmm huh important inside
        instead kind later least less life line making maybe mean means meet money move moving music near new
        news nine oh outside paper past pay picture problem put question read ready reason remember rest right
        same sat say set seven several shall short should sit six sleep someone speak stand stay sun sure
        system ten than then try trying turn turned uh um wanna whatever whether white why wish wow yep along
        also another anyone anything anyway anywhere around became become begin began believe body book books
        bought brother call care case cool couple dark dead deal dear death die died dog door doing dream drink
        drive easy else everybody exactly example feet fight follow food forget form free front fun gave gets
        god gone group grow guess hair half happy hate heart hold hot human its keep kill knows land late laugh
        lead learn leave light listen lose lost loved low mad matter mind moment mouth movie myself need needs
        nothing open order parents party phone plan power probably pull rather red road round says sea seems
        sense send sent shit shot sister sit sitting son song soul sound special stuff sweet table taken taking
        team test themselves thinks times top town tree true truth understand used voice wanted war warm wants
        watching wear whatever window wrong young
        """.Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}
