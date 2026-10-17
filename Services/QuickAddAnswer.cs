using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>One entry of the AI's answer: the waiting word it answers, the word to save (dictionary form) and the AI's note.</summary>
public sealed record AiWord(string Asked, Word Word, string Note);

/// <summary>The entries read from the AI's answer, or why none could be read.</summary>
public sealed record AiAnswer(List<AiWord> Words, IReadOnlyList<string> Errors);

/// <summary>
/// The AI round trip of "Từ của tôi": a prompt asking for the waiting words as the big learner's dictionaries
/// give them (Cambridge, Oxford Learner's, Longman), answered as a JSON array, and the reader of that answer.
/// </summary>
public static partial class QuickAdd
{
    /// <summary>At most this many words go in one prompt; long lists make AI chats cut the answer short.</summary>
    public const int MaxPromptWords = 40;

    private const int MaxContextLength = 200;

    /// <summary>The sentence of a subtitle line that holds the word (the whole line when none does), at most 200 letters.</summary>
    public static string ContextOf(string? line, string word)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        var text = Whitespace().Replace(line.Replace('’', '\''), " ").Trim().TrimStart('-', '–', '—', ' ');
        var find = new Regex($@"(?<![\p{{L}}']){Regex.Escape(word)}(?!\p{{L}})", RegexOptions.IgnoreCase);
        var sentence = SentenceEnd().Split(text).FirstOrDefault(find.IsMatch)?.Trim().TrimStart('-', '–', '—', ' ') ?? text;
        if (sentence.Length <= MaxContextLength) return sentence;
        var at = Math.Max(0, (find.Match(sentence) is { Success: true } m ? m.Index : 0) - MaxContextLength / 2);
        at = Math.Min(at, sentence.Length - MaxContextLength);
        return (at > 0 ? "…" : "") + sentence.Substring(at, MaxContextLength).Trim() + (at + MaxContextLength < sentence.Length ? "…" : "");
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceEnd();

    /// <summary>
    /// Prompt asking an AI chat to look the waiting words up the way Cambridge / Oxford Learner's / Longman give
    /// them, using the sentence each was met in, answered as one JSON array (<see cref="ReadAnswer"/>). Only the
    /// first <see cref="MaxPromptWords"/> words; the rest wait for the next prompt.
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<InboxWord> waiting)
    {
        var words = waiting.Take(MaxPromptWords).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Bạn là biên tập viên từ điển Anh–Việt. Hãy tra {words.Count} từ/cụm từ tiếng Anh dưới đây đúng như các từ điển lớn cho người học — Cambridge Dictionary, Oxford Learner's Dictionaries, Longman (LDOCE) — cho người Việt học từ vựng.");
        sb.AppendLine();
        sb.AppendLine("Danh sách (giữ đúng thứ tự):");
        for (var i = 0; i < words.Count; i++)
            sb.AppendLine(words[i].Context.Length > 0 ? $"{i + 1}. {words[i].Text} — câu gặp từ: \"{words[i].Context}\"" : $"{i + 1}. {words[i].Text}");
        sb.Append($$"""

            Mỗi từ là một mục JSON:
            - "word": chép y nguyên từ trong danh sách (Voca dùng nó để khớp), kể cả khi đó là dạng chia (postponed, studies).
            - "headword": dạng gốc của mục từ trong từ điển nếu khác "word" (postponed → postpone, studies → study); giống thì để "".
            - "ipa": phiên âm IPA giọng Anh-Anh (UK) đúng như Cambridge Dictionary ghi cho mục từ, trong dấu /.../, có dấu trọng âm ˈ ˌ; cụm từ thì phiên âm cả cụm.
            - "pos": từ loại của nghĩa đưa ra, viết tắt: n, v, adj, adv, phr v, idiom, phr, prep, conj, pron, det, excl.
            - "meaning": nghĩa tiếng Việt ngắn gọn (vài chữ, nhiều nhất 2 nghĩa cách nhau bằng dấu phẩy) của nghĩa phổ biến nhất — nghĩa đứng đầu mục từ trong Cambridge / Oxford Learner's.
            - "contextMeaning": chỉ khi từ có "câu gặp từ" và trong câu đó từ mang nghĩa KHÁC "meaning": nghĩa tiếng Việt ngắn gọn đúng với câu đó; còn lại để "".
            - "example": một câu ví dụ tiếng Anh ngắn, tự nhiên (không quá 15 từ) theo kiểu câu ví dụ của từ điển, đúng nghĩa ở "meaning"; không chép lại câu gặp từ.
            - "exampleMeaning": bản dịch tiếng Việt tự nhiên của câu ví dụ, trong đó từ được dịch đúng như "meaning".
            - "note": để "", trừ trường hợp dưới đây.

            Không bịa. Từ viết sai chính tả, tên riêng, không phải tiếng Anh hoặc không có trong các từ điển trên: vẫn giữ mục của nó nhưng để "meaning": "" và ghi lý do ngắn trong "note" (vd. "tên riêng", "viết sai, đúng là receive"). Phần nào không chắc thì để "", đừng đoán.

            Trả về đúng {{words.Count}} mục, cùng thứ tự danh sách. Chỉ trả lời bằng MỘT khối ```json chứa một mảng JSON hợp lệ (dấu nháy kép thẳng "), không giải thích gì thêm:
            ```json
            [
              {"word": "...", "headword": "", "ipa": "/.../", "pos": "n", "meaning": "...", "contextMeaning": "", "example": "...", "exampleMeaning": "...", "note": ""}
            ]
            ```
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Reads the AI's answer: every JSON array (or object) of word entries in it — inside ```json fences or not,
    /// after a greeting, split over several blocks, curly quotes, or cut short (the whole entries still count).
    /// An answer in the older table form (<see cref="PlanFormat"/>) is read too. Each waiting word counts once.
    /// </summary>
    public static AiAnswer ReadAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return new([], ["Câu trả lời đang trống."]);
        var entries = JsonEntries(answer);
        // Some chats turn the quotes of the JSON into “ ”; only then is it worth straightening them.
        if (entries.Count == 0 && answer.Contains('“')) entries = JsonEntries(answer.Replace('“', '"').Replace('”', '"'));
        if (entries.Count == 0 && PlanFormat.Read(answer).Plan is { Words.Count: > 0 } table)
            entries = table.Words.Where(w => Normalize(w.Text) is not null).Select(w => new AiWord(Normalize(w.Text)!, w, "")).ToList();
        if (entries.Count == 0)
            return new([], ["Không thấy danh sách từ (JSON) trong câu trả lời. Hãy copy toàn bộ câu trả lời của AI (nút Copy của khối code) rồi dán lại."]);
        return new(entries.DistinctBy(e => Key(e.Asked)).ToList(), []);
    }

    private static List<AiWord> JsonEntries(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var options = new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        var found = new List<AiWord>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is not ((byte)'[' or (byte)'{')) continue;
            var reader = new Utf8JsonReader(bytes.AsSpan(i), options);
            try
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var entries = Entries(doc.RootElement).ToList();
                if (entries.Count == 0) continue;
                found.AddRange(entries);
                i += (int)reader.BytesConsumed - 1;
            }
            // A broken or cut-off array: keep scanning, its whole entries are read one by one.
            catch (JsonException) { }
        }
        return found;
    }

    /// <summary>Word entries of a JSON value: an array of them, one entry, or an object holding such an array ({"words": [...]}).</summary>
    private static IEnumerable<AiWord> Entries(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Entries),
        JsonValueKind.Object when Field(value, "word", "text", "từ").Length > 0 => Entry(value) is { } entry ? [entry] : [],
        JsonValueKind.Object => value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).SelectMany(p => Entries(p.Value)),
        _ => []
    };

    private static AiWord? Entry(JsonElement e)
    {
        var asked = Normalize(Field(e, "word", "text", "từ"));
        if (asked is null) return null;
        var meaning = Field(e, "meaning", "nghĩa", "vi");
        var inContext = Field(e, "contextMeaning", "context_meaning");
        if (meaning.Length > 0 && inContext.Length > 0 && !meaning.Contains(inContext, StringComparison.OrdinalIgnoreCase))
            meaning += $"; trong câu: {inContext}";
        var word = new Word
        {
            Text = Normalize(Field(e, "headword", "lemma")) ?? asked,
            Phonetic = PlanFormat.NormalizePhonetic(Field(e, "ipa", "phonetic", "pronunciation", "phiên âm")),
            PartOfSpeech = ShortPartOfSpeech(Field(e, "pos", "partOfSpeech", "part_of_speech", "type", "loại")),
            Meaning = meaning,
            Example = Field(e, "example", "ví dụ"),
            ExampleMeaning = Field(e, "exampleMeaning", "example_meaning", "exampleTranslation", "dịch ví dụ")
        };
        return new AiWord(asked, word, Field(e, "note", "ghi chú"));
    }

    /// <summary>A text field by any of its names (any case); a list of texts is joined with commas.</summary>
    private static string Field(JsonElement e, params string[] names)
    {
        foreach (var p in e.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!.Trim(),
                JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.Trim()).Where(v => v.Length > 0)),
                _ => ""
            };
        }
        return "";
    }

    private static readonly Dictionary<string, string> PosShort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noun"] = "n", ["verb"] = "v", ["adjective"] = "adj", ["adverb"] = "adv", ["phrasal verb"] = "phr v",
        ["phrase"] = "phr", ["preposition"] = "prep", ["conjunction"] = "conj", ["pronoun"] = "pron", ["determiner"] = "det",
        ["exclamation"] = "excl", ["interjection"] = "excl", ["modal verb"] = "modal v", ["auxiliary verb"] = "aux v",
        ["danh từ"] = "n", ["động từ"] = "v", ["tính từ"] = "adj", ["trạng từ"] = "adv", ["phó từ"] = "adv", ["cụm động từ"] = "phr v",
        ["thành ngữ"] = "idiom", ["cụm từ"] = "phr", ["giới từ"] = "prep", ["liên từ"] = "conj", ["đại từ"] = "pron", ["thán từ"] = "excl"
    };

    /// <summary>"noun, verb" → "n, v"; Voca's short forms and anything unknown stay as written; "(countable)" and dots go.</summary>
    public static string ShortPartOfSpeech(string pos) => string.Join(", ", Parenthesis().Replace(pos, "")
        .Split([',', '/', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.TrimEnd('.').Trim()).Where(p => p.Length > 0).Select(p => PosShort.GetValueOrDefault(p, p)).Distinct());

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthesis();

    /// <summary>
    /// Imports the AI's answer: the waiting words it gives a meaning become a new day of "Từ của tôi" (created
    /// outside the course when missing), saved in their dictionary form, and leave the waiting list; words already
    /// in the library are skipped. Words left out, or without a meaning (not in the dictionaries), keep waiting.
    /// </summary>
    public static MyWordsImport Import(AppData data, string answer, DateTime now)
    {
        var read = ReadAnswer(answer);
        if (read.Errors.Count > 0) return new MyWordsImport([], [], 0, read.Errors, []);

        var waiting = data.Inbox.Select(i => i.Text).ToList();
        var existingPlan = data.Plans.FirstOrDefault(p => p.Name == PlanName);
        var day = (existingPlan?.DayCount ?? 0) + 1;
        var added = new List<(string Asked, Word Word)>();
        var skipped = new List<string>();
        var stillWaiting = new List<string>();
        foreach (var (asked, word, note) in read.Words)
        {
            if (word.Meaning.Length == 0)
            {
                if (IsWaiting(data, asked)) stillWaiting.Add($"{asked} — {(note.Length > 0 ? note : "AI không đưa nghĩa")}");
                continue;
            }
            if (FindInLibrary(data, word.Text) is not null || FindInLibrary(data, asked) is not null || added.Any(a => Key(a.Word.Text) == Key(word.Text)))
            {
                skipped.Add(word.Text);
                Remove(data, asked);
                Remove(data, word.Text);
                continue;
            }
            added.Add((asked, new Word
            {
                Day = day, Text = word.Text.Trim(), Phonetic = word.Phonetic, PartOfSpeech = word.PartOfSpeech,
                Meaning = word.Meaning, Example = word.Example, ExampleMeaning = word.ExampleMeaning
            }));
        }
        var answered = read.Words.Select(w => Key(w.Asked)).ToHashSet();
        stillWaiting.AddRange(waiting.Where(w => !answered.Contains(Key(w))).Select(w => $"{w} — AI chưa trả lời"));
        if (added.Count == 0) return new MyWordsImport([], skipped, 0, [], stillWaiting);

        var plan = MyPlan(data, now);
        plan.DayTitles[day] = DayTitleFor(now);
        foreach (var (asked, word) in added)
        {
            plan.Words.Add(word);
            StartReviewing(word, now);
            Remove(data, asked);
            Remove(data, word.Text);
        }
        return new MyWordsImport(added.Select(a => a.Word).ToList(), skipped, day, [], stillWaiting);
    }
}
