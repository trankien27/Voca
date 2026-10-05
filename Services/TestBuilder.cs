using System.Text;
using Voca.Models;

namespace Voca.Services;

public enum TestKind { WordToMeaning, MeaningToWord, Typing, Listening }

/// <summary>One test question. <see cref="Options"/> is empty for typing questions.</summary>
public sealed record TestQuestion(Word Word, TestKind Kind, string Prompt, string Correct, IReadOnlyList<string> Options);

public sealed record TestAnswer(TestQuestion Question, string Given, bool Correct);

/// <summary>
/// Tests over any range of a plan. Unlike the daily session they never touch spaced repetition;
/// wrong answers go to the mistake list (<see cref="MistakeDays"/>).
/// </summary>
public static class TestBuilder
{
    public static readonly IReadOnlyList<int> CountChoices = [10, 20, 30, 40, 0];

    public static string Label(TestKind kind) => kind switch
    {
        TestKind.WordToMeaning => "Từ → nghĩa",
        TestKind.MeaningToWord => "Nghĩa → từ",
        TestKind.Typing => "Gõ từ",
        _ => "Nghe chọn từ"
    };

    /// <summary>Words of <paramref name="plan"/> from day <paramref name="fromDay"/> to <paramref name="toDay"/> that can be asked.</summary>
    public static List<Word> Pool(Plan plan, int fromDay, int toDay) =>
        plan.Words.Where(w => w.Day >= fromDay && w.Day <= toDay && w.Text.Trim().Length > 0 && w.Meaning.Trim().Length > 0).ToList();

    /// <summary>A random selection of <paramref name="count"/> words (0 = all).</summary>
    public static List<Word> Pick(IReadOnlyList<Word> pool, int count, Random random) =>
        pool.OrderBy(_ => random.Next()).Take(count <= 0 ? pool.Count : count).ToList();

    /// <summary>
    /// One question per word, in random order, with the chosen kinds spread evenly. Wrong options come
    /// from the same day first, then the same plan, then other plans. A choice question that cannot get
    /// at least one wrong option becomes a typing question.
    /// </summary>
    public static List<TestQuestion> Build(AppData data, IReadOnlyList<Word> words, IReadOnlyCollection<TestKind> kinds, Random random)
    {
        var kindList = kinds.Distinct().ToList();
        if (words.Count == 0 || kindList.Count == 0) return [];

        var owner = new Dictionary<Word, Plan>(ReferenceEqualityComparer.Instance);
        foreach (var plan in data.Plans)
            foreach (var word in plan.Words)
                owner.TryAdd(word, plan);
        var candidates = owner.Keys.Where(w => w.Text.Trim().Length > 0 && w.Meaning.Trim().Length > 0).ToList();

        var order = words.OrderBy(_ => random.Next()).ToList();
        var assigned = Enumerable.Range(0, order.Count).Select(i => kindList[i % kindList.Count]).OrderBy(_ => random.Next()).ToList();
        var questions = new List<TestQuestion>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            var word = order[i];
            var kind = assigned[i];
            if (kind == TestKind.Typing)
            {
                questions.Add(new TestQuestion(word, kind, word.Meaning, word.Text, []));
                continue;
            }
            var askMeaning = kind == TestKind.WordToMeaning;
            string Side(Word w) => (askMeaning ? w.Meaning : w.Text).Trim();
            var correct = Side(word);
            var plan = owner.GetValueOrDefault(word);
            var wrong = candidates
                .Where(w => !Same(w.Text, word.Text) && !Same(w.Meaning, word.Meaning))
                .OrderBy(w => owner[w] == plan && w.Day == word.Day ? 0 : owner[w] == plan ? 1 : 2)
                .ThenBy(_ => random.Next())
                .Select(Side)
                .Where(o => !Same(o, correct))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            if (wrong.Count == 0)
            {
                questions.Add(new TestQuestion(word, TestKind.Typing, word.Meaning, word.Text, []));
                continue;
            }
            var options = wrong.Append(correct).OrderBy(_ => random.Next()).ToList();
            var prompt = kind switch
            {
                TestKind.WordToMeaning => word.Text,
                TestKind.MeaningToWord => word.Meaning,
                _ => ""
            };
            questions.Add(new TestQuestion(word, kind, prompt, correct, options));
        }
        return questions;
    }

    /// <summary>
    /// Checks a typed answer: case, extra spaces, curly apostrophes and end punctuation are ignored;
    /// "a/b" accepts either, and text in brackets is optional ("(be) keen on").
    /// </summary>
    public static bool IsTypedCorrectly(string typed, string answer)
    {
        var given = Normalize(typed);
        if (given.Length == 0) return false;
        return answer.Split('/', ';')
            .SelectMany(part => new[] { part, RemoveBrackets(part) })
            .Any(part => Normalize(part) == given);
    }

    /// <summary>First letter of each word, the rest as underscores: "take after" → "t___ a____".</summary>
    public static string Hint(string answer) =>
        string.Join(" ", answer.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[0] + new string('_', part.Length - 1)));

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string RemoveBrackets(string text)
    {
        var result = new StringBuilder();
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (depth == 0) result.Append(c);
        }
        return result.ToString();
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Replace('’', '\'').Replace('‘', '\''))
        {
            if (char.IsLetterOrDigit(c) || c is '\'' or '-') builder.Append(c);
            else if (char.IsWhiteSpace(c) || c is '(' or ')') builder.Append(' ');
        }
        return string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
