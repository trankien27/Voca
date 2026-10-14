using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Voca.Models;

namespace Voca.Services;

/// <summary>
/// What the phone page can ask for: today's words with "Đã thuộc"/"Chưa nhớ", the daily session, all words and
/// "Từ của tôi". Every change goes through the same <see cref="Store"/> and <see cref="CourseEngine"/> rules as
/// the taskbar, so the computer and the phone never disagree. Must run on the thread that owns the store.
/// </summary>
public sealed class PhoneApi(Store store, Func<DateTime>? clock = null)
{
    public const string CookieName = "voca";
    private const int PageSize = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> IndexPage = new(() => ReadResource("Phone.index.html"));

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);
    /// <summary>The last word marked "Đã thuộc" from the phone and the course position before it, for "Hoàn tác".</summary>
    private (Guid WordId, CourseState Position)? _undo;

    private AppData Data => store.Data;

    private sealed record IdBody(Guid Id, bool Learned);
    private sealed record IdsBody(List<Guid> Ids);
    private sealed record AnswerBody(Guid Id, bool Correct);
    private sealed record SessionBody(List<AnswerBody> Answers, List<Guid> NewIds);
    private sealed record CheckBody(string Typed, string Answer);
    private sealed record AddBody(string Text, string Meaning, string Phonetic, string Pos, string Example, string ExampleMeaning);
    private sealed record ProgressBody(string Id, double Seconds, double Length);
    private sealed record WordBody(string Text);

    /// <summary>The videos listed last time, by id, so the many requests of one playing video do not list the folders again.</summary>
    private Dictionary<string, PhoneVideo> _videos = [];

    public PhoneResponse Handle(PhoneRequest request)
    {
        var key = Data.Settings.PhoneKey;
        // The QR code opens "/?k=<key>": keep the key in a cookie and drop it from the address bar.
        if (request is { Method: "GET", Path: "/" } && request.Query.TryGetValue("k", out var given))
            return IsKey(given, key)
                ? PhoneResponse.Redirect("/", new Dictionary<string, string>
                    { ["Set-Cookie"] = $"{CookieName}={Uri.EscapeDataString(key)}; Max-Age=31536000; Path=/; HttpOnly; SameSite=Lax" })
                : Locked();
        if (!IsKey(request.Cookie(CookieName), key))
            return request.Path.StartsWith("/api/") ? Error(401, "Điện thoại chưa được kết nối. Quét lại mã QR trong Voca trên máy tính.") : Locked();
        // Another site cannot add this header without the browser asking first, so it cannot make changes for you.
        if (request.Method == "POST" && request.Header("x-voca") != "1") return Error(403, "Yêu cầu không hợp lệ.");

        try
        {
            if (request.Method == "GET" && request.Path.StartsWith("/media/")) return Media(request);
            return (request.Method, request.Path) switch
            {
                ("GET", "/") => PhoneResponse.Text(200, "text/html; charset=utf-8", IndexPage.Value),
                ("GET", "/api/today") => Ok(Today()),
                ("POST", "/api/rate") => Ok(Rate(Read<IdBody>(request))),
                ("POST", "/api/undo") => Ok(Undo()),
                ("GET", "/api/session") => Ok(Session()),
                ("POST", "/api/session") => Ok(FinishSession(Read<SessionBody>(request))),
                ("POST", "/api/learned") => Ok(MarkLearned(Read<IdsBody>(request).Ids)),
                ("POST", "/api/check") => Ok(Check(Read<CheckBody>(request))),
                ("GET", "/api/words") => Ok(Words(request.Query)),
                ("GET", "/api/lookup") => Ok(Lookup(request.Query.GetValueOrDefault("text", ""))),
                ("POST", "/api/add") => Ok(Add(Read<AddBody>(request))),
                ("GET", "/api/videos") => Ok(Videos()),
                ("GET", "/api/video") => Ok(Video(request.Query.GetValueOrDefault("id", ""))),
                ("POST", "/api/video/progress") => Ok(SaveProgress(Read<ProgressBody>(request))),
                ("POST", "/api/video/word") => Ok(SaveVideoWord(Read<WordBody>(request))),
                _ => Error(404, "Không có trang này.")
            };
        }
        catch (JsonException)
        {
            return Error(400, "Dữ liệu gửi lên không đọc được.");
        }
        catch (KeyNotFoundException)
        {
            return Error(404, "Không tìm thấy từ này — có thể nó vừa bị xóa trên máy tính. Tải lại trang.");
        }
    }

    private static bool IsKey(string? given, string key) =>
        key.Length > 0 && given is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));

    private static T Read<T>(PhoneRequest request) =>
        JsonSerializer.Deserialize<T>(request.Body, Json) ?? throw new JsonException("empty body");

    private static PhoneResponse Ok(object value) =>
        PhoneResponse.Text(200, "application/json; charset=utf-8", JsonSerializer.Serialize(value, Json));

    private static PhoneResponse Error(int status, string message) =>
        PhoneResponse.Text(status, "application/json; charset=utf-8", JsonSerializer.Serialize(new { error = message }, Json));

    private static PhoneResponse Locked() => PhoneResponse.Text(401, "text/html; charset=utf-8", """
        <!doctype html><html lang="vi"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Voca</title>
        <body style="font-family:system-ui,sans-serif;max-width:420px;margin:15vh auto;padding:0 20px;color:#172033;line-height:1.5">
        <h2 style="color:#6C5CE7">Voca</h2>
        <p>Điện thoại này chưa được kết nối.</p>
        <p>Trên máy tính, mở <b>Voca → Thư viện → Cài đặt → Học trên điện thoại</b> rồi quét mã QR bằng camera điện thoại.</p>
        </body></html>
        """);

    private static string ReadResource(string name)
    {
        using var stream = typeof(PhoneApi).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Thiếu tài nguyên {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Word FindWord(Guid id) =>
        Data.Plans.SelectMany(p => p.Words).FirstOrDefault(w => w.Id == id) ?? throw new KeyNotFoundException();

    /// <summary>The same upkeep the taskbar does before showing today's words; saved only when it changed something.</summary>
    private void Upkeep()
    {
        if (CourseEngine.Upkeep(Data, _clock())) store.Save();
    }

    // ---------------------------------- today ----------------------------------

    /// <summary>Today's words as on the taskbar (answered wrong today first), with where the learner is in the course.</summary>
    private (List<Word> Words, HashSet<Word> Fresh, bool MistakeDay) BuildToday()
    {
        var today = _clock().Date;
        var words = CourseEngine.BuildToday(Data, today, out var fresh)
            .OrderByDescending(w => ReviewScheduler.IsWrongToday(w, today)).ToList();
        return (words, fresh, MistakeDays.Active(Data, today) is not null && fresh.Count > 0);
    }

    private object Today()
    {
        Upkeep();
        var today = _clock().Date;
        var (words, fresh, mistakeDay) = BuildToday();
        var stats = StatsService.Compute(Data, today, days: 1);
        return new
        {
            course = Course(words, fresh, mistakeDay),
            streak = stats.CurrentStreak,
            mistakes = MistakeDays.Waiting(Data).Count,
            words = words.Select(w => Card(w, fresh.Contains(w) ? mistakeDay ? "mistake" : "new" : "review")),
            version = Updater.Current.ToString(3)
        };
    }

    private object? Course(List<Word> words, HashSet<Word> fresh, bool mistakeDay)
    {
        var reviews = words.Count - fresh.Count;
        if (mistakeDay && MistakeDays.Active(Data, _clock().Date) is { } day)
            return new
            {
                title = $"Ngày học từ sai · {fresh.Count} từ",
                dayTitle = "",
                dots = Array.Empty<string>(),
                status = day.Done
                    ? $"Đã học xong ngày từ sai ✓ · còn {MistakeDays.Waiting(Data).Count} từ trong danh sách từ sai"
                    : $"Hôm nay: {fresh.Count} từ sai · {reviews} từ cần ôn",
                session = day.Done ? "Học lại / ôn tập" : "Bắt đầu học từ sai"
            };
        if (CourseEngine.Current(Data) is not var (plan, _)) return null;
        var position = Data.Position;
        var dayWords = CourseEngine.DayWords(Data);
        var learned = dayWords.Count(w => w.Learned);
        return new
        {
            title = $"{plan.Name} · Ngày {position.Day}/{plan.DayCount}",
            dayTitle = position.Finished ? "" : plan.DayTitle(position.Day),
            dots = Enumerable.Range(1, plan.DayCount)
                .Select(d => d < position.Day || position.Finished ? "done" : d == position.Day ? "now" : "later"),
            status = position.Finished ? "Đã học hết khóa học. Từ cũ vẫn được ôn khi đến hạn."
                : position.DayCompleted ? $"Đã học phiên hôm nay ✓ · đã thuộc {learned}/{dayWords.Count} từ"
                : $"Hôm nay: {dayWords.Count - learned} từ chưa thuộc · {reviews} từ cần ôn · đã thuộc {learned}/{dayWords.Count}",
            session = position.DayCompleted || position.Finished ? "Học lại / ôn tập" : "Bắt đầu phiên học"
        };
    }

    private object Card(Word word, string kind)
    {
        var plan = CourseEngine.Current(Data)?.Plan;
        var dayTitle = kind == "new" && plan is not null ? plan.DayTitle(word.Day) : "";
        return new
        {
            id = word.Id,
            text = word.Text,
            phonetic = word.Phonetic,
            pos = word.PartOfSpeech,
            meaning = word.Meaning,
            example = word.Example,
            exampleMeaning = word.ExampleMeaning,
            kind,
            badge = kind switch
            {
                "review" => "ÔN TẬP",
                "mistake" => $"TỪ SAI · SAI {Math.Max(1, MistakeDays.TimesWrong(Data, word))} LẦN",
                _ => dayTitle.Length == 0 ? "TỪ MỚI" : $"TỪ MỚI · {dayTitle}"
            },
            info = ReviewInfo(word),
            learned = word.Learned
        };
    }

    private string ReviewInfo(Word word)
    {
        var today = _clock().Date;
        if (ReviewScheduler.IsRatedToday(word, today))
            return (word.Review!.LastKnown ? "Hôm nay trả lời đúng · " : "Hôm nay trả lời sai · ") + NextReview(word);
        return word.Review is { } state ? $"Đúng {state.Reps} lần liên tiếp · sai {word.ForgotCount} lần" : "Từ mới · sẽ được kiểm tra trong phiên học";
    }

    private string NextReview(Word word)
    {
        var days = ((word.Review?.Due.Date ?? _clock().Date.AddDays(1)) - _clock().Date).Days;
        return days <= 1 ? "ôn lại ngày mai" : $"ôn lại sau {days} ngày";
    }

    // ---------------------------------- quick rating ----------------------------------

    private object Rate(IdBody body)
    {
        var word = FindWord(body.Id);
        var now = _clock();
        if (!body.Learned)
        {
            _undo = null;
            store.Update(d => CourseEngine.MarkNotRemembered(d, word, now));
            return new { message = $"Đã ghi “{word.Text}” là chưa nhớ — hiện trước hôm nay và ôn lại ngày mai.", undo = false };
        }
        var before = Data.Position.Copy();
        store.Update(d => CourseEngine.MarkLearned(d, word, now));
        Upkeep();
        _undo = (word.Id, before);
        return new { message = DayLearnedText(before) ?? $"Đã thuộc “{word.Text}” — từ này sẽ không hiện nữa.", undo = true };
    }

    private object Undo()
    {
        if (_undo is not var (id, position)) return new { message = "Không còn gì để hoàn tác." };
        _undo = null;
        var word = FindWord(id);
        store.Update(d =>
        {
            CourseEngine.UnmarkLearned(word);
            // That word may have finished the day and moved the course on: go back to the day.
            d.Position = position;
        });
        return new { message = $"Đã hoàn tác: “{word.Text}” sẽ hiện lại." };
    }

    /// <summary>"Đã thuộc hết Ngày N … Sang Ngày M." when the course left the day it was on; null while still on it.</summary>
    private string? DayLearnedText(CourseState before)
    {
        var position = Data.Position;
        if (before.PlanId is not Guid planId || Data.Plans.FirstOrDefault(p => p.Id == planId) is not { } plan) return null;
        if (before.Finished || (position.PlanId == planId && position.Day == before.Day && !position.Finished)) return null;
        var next = position.Finished ? "Đã học hết khóa học."
            : CourseEngine.Current(Data) is not var (now, _) ? ""
            : now == plan ? $"Sang Ngày {position.Day}/{plan.DayCount}."
            : $"Sang lộ trình {now.Name}.";
        return $"Đã thuộc hết Ngày {before.Day} · {plan.Name} 🎉 {next}".TrimEnd();
    }

    private object MarkLearned(List<Guid> ids)
    {
        var words = ids.Distinct().Select(FindWord).Where(w => !w.Learned).ToList();
        if (words.Count == 0) return new { message = "" };
        var before = Data.Position.Copy();
        var now = _clock();
        store.Update(d => { foreach (var word in words) CourseEngine.MarkLearned(d, word, now); });
        Upkeep();
        _undo = null;
        return new { message = DayLearnedText(before) ?? $"Đã đánh dấu thuộc {words.Count} từ." };
    }

    // ---------------------------------- session ----------------------------------

    /// <summary>Today's session, as the taskbar's "Bắt đầu phiên học" builds it: new words to flip, then the quiz.</summary>
    private object Session()
    {
        Upkeep();
        var (words, fresh, mistakeDay) = BuildToday();
        var newWords = words.Where(fresh.Contains).ToList();
        var reviews = words.Where(w => !fresh.Contains(w)).ToList();
        var quiz = CourseEngine.BuildQuiz(Data, [.. newWords, .. reviews], Random.Shared, Data.Settings.QuizTyping);
        var title = mistakeDay ? $"Ngày học từ sai · {newWords.Count} từ"
            : CourseEngine.Current(Data) is var (plan, _) ? $"{plan.Name} · Ngày {Data.Position.Day}/{plan.DayCount}" : "Phiên học";
        return new
        {
            title,
            mode = mistakeDay ? "mistake" : "course",
            learn = newWords.Select(w => Card(w, mistakeDay ? "mistake" : "new")),
            newIds = newWords.Select(w => w.Id),
            quiz = quiz.Select(q => new
            {
                id = q.Word.Id,
                kind = q.Kind.ToString(),
                prompt = q.Prompt,
                correct = q.Correct,
                options = q.Options,
                askWord = q.AskWord,
                text = q.Word.Text,
                phonetic = q.Word.Phonetic,
                meaning = q.Word.Meaning
            })
        };
    }

    private object FinishSession(SessionBody body)
    {
        var answers = body.Answers.Select(a => (Word: FindWord(a.Id), a.Correct)).ToList();
        var newWords = body.NewIds.Distinct().Select(FindWord).ToList();
        var mistakeDay = MistakeDays.Active(Data, _clock().Date) is not null && newWords.Count > 0;
        if (answers.Count > 0) store.Update(d => CourseEngine.ApplySession(d, answers, newWords, _clock()));
        var correct = answers.Count(a => a.Correct);
        return new
        {
            score = $"{correct}/{answers.Count}",
            note = mistakeDay
                ? correct == answers.Count
                    ? "Đúng hết! Các từ này đã ra khỏi danh sách từ sai. Mai học tiếp lộ trình."
                    : "Từ trả lời đúng đã ra khỏi danh sách từ sai; từ còn sai ở lại danh sách và quay lại vào ngày mai."
                : correct == answers.Count
                    ? "Tuyệt vời, đúng hết! Lịch ôn tập đã được giãn ra."
                    : "Từ làm sai sẽ quay lại vào ngày mai và xuất hiện trước hôm nay.",
            results = answers.Select(a => new
            {
                id = a.Word.Id, text = a.Word.Text, meaning = a.Word.Meaning, correct = a.Correct,
                next = NextReview(a.Word), learned = a.Word.Learned
            })
        };
    }

    private static object Check(CheckBody body) => new { correct = TestBuilder.IsTypedCorrectly(body.Typed, body.Answer) };

    // ---------------------------------- all words ----------------------------------

    private object Words(IReadOnlyDictionary<string, string> query)
    {
        var filter = query.GetValueOrDefault("filter") switch
        {
            "new" => WordFilter.New,
            "learning" => WordFilter.Learning,
            "due" => WordFilter.Due,
            "known" => WordFilter.Known,
            _ => WordFilter.All
        };
        var skip = int.TryParse(query.GetValueOrDefault("skip"), out var n) ? Math.Max(0, n) : 0;
        var found = AllWords.Find(Data, _clock().Date, query.GetValueOrDefault("q", ""), filter);
        return new
        {
            total = found.Count,
            items = found.Skip(skip).Take(PageSize).Select(e => new
            {
                id = e.Word.Id,
                text = e.Word.Text,
                phonetic = e.Word.Phonetic,
                pos = e.Word.PartOfSpeech,
                meaning = e.Word.Meaning,
                example = e.Word.Example,
                exampleMeaning = e.Word.ExampleMeaning,
                where = $"{e.Plan.Name} · Ngày {e.Word.Day}",
                status = e.Status.ToString().ToLowerInvariant(),
                statusText = e.Status switch
                {
                    WordStatus.Learned => "Đã thuộc",
                    WordStatus.Mastered => "Nhớ lâu",
                    WordStatus.Due => "Đến hạn ôn",
                    WordStatus.Learning => "Đang học",
                    _ => "Chưa học"
                }
            })
        };
    }

    // ---------------------------------- Từ của tôi ----------------------------------

    private object Lookup(string text)
    {
        var clean = QuickAdd.Normalize(text);
        if (clean is null) return new { valid = false, text = "", message = "" };
        if (QuickAdd.FindInLibrary(Data, clean) is var (plan, word))
            return new
            {
                valid = true, text = clean,
                message = $"Đã có trong {plan.Name} · Ngày {word.Day}{(word.Learned ? " · đã thuộc" : "")}: {word.Meaning}"
            };
        return new { valid = true, text = clean, message = QuickAdd.IsWaiting(Data, clean) ? "Từ này đang chờ AI điền nghĩa." : "" };
    }

    private object Add(AddBody body)
    {
        var now = _clock();
        var clean = QuickAdd.Normalize(body.Text);
        if (clean is null) return new { ok = false, message = "Hãy nhập một từ hoặc cụm từ ngắn (tối đa 6 từ, không có chữ số)." };
        if (QuickAdd.FindInLibrary(Data, clean) is var (plan, word))
            return new { ok = false, message = $"Đã có trong {plan.Name} · Ngày {word.Day} — không thêm trùng." };
        if ((body.Meaning ?? "").Trim().Length == 0)
        {
            QuickAddResult? result = null;
            store.Update(d => result = QuickAdd.Add(d, clean, now));
            return result!.AlreadyWaiting
                ? new { ok = false, message = $"“{clean}” đã có trong danh sách chờ AI điền nghĩa." }
                : new { ok = true, message = $"Đã đưa “{clean}” vào danh sách chờ ({Data.Inbox.Count} từ). Trên máy tính mở tab Từ của tôi để nhờ AI điền nghĩa." };
        }
        Word? saved = null;
        store.Update(d => saved = QuickAdd.SaveWord(d, clean, body.Phonetic ?? "", body.Pos ?? "", body.Meaning!, body.Example ?? "", now, body.ExampleMeaning ?? ""));
        return saved is null
            ? new { ok = false, message = "Không lưu được từ này." }
            : new { ok = true, message = $"Đã lưu “{saved.Text}” — {saved.Meaning}. Từ này sẽ hiện để ôn ngay hôm nay." };
    }

    // ---------------------------------- videos ----------------------------------

    private WatchHistory History() => WatchProgress.Load(store.Folder);

    private List<PhoneVideo> ListVideos()
    {
        var list = PhoneVideos.List(Data, History());
        _videos = list.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        return list;
    }

    private PhoneVideo FindVideo(string id)
    {
        if (!_videos.ContainsKey(id)) ListVideos();
        return _videos.TryGetValue(id, out var video) ? video : throw new KeyNotFoundException();
    }

    private static string Name(string path) => Path.GetFileNameWithoutExtension(path);

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>"Phim · E01" for an episode of a film, the file name otherwise.</summary>
    private static string Title(PhoneVideo v) =>
        v.Film is null ? Name(v.Path) : v.Label.Length == 0 || v.Label == v.Film ? v.Film : $"{v.Film} · {v.Label}";

    /// <summary>The episode after this one in its film (or folder), in list order.</summary>
    private PhoneVideo? NextOf(PhoneVideo video)
    {
        var group = _videos.Values.Where(v => v.Group == video.Group).ToList();
        var at = group.FindIndex(v => v.Id == video.Id);
        return at >= 0 && at + 1 < group.Count ? group[at + 1] : null;
    }

    private static object Item(PhoneVideo v) => new
    {
        id = v.Id,
        name = v.Label.Length > 0 ? v.Label : Name(v.Path),
        ext = Path.GetExtension(v.Path).TrimStart('.').ToLowerInvariant(),
        srt = v.Srt is not null,
        phone = PhoneVideos.PlaysOnPhone(v.Path),
        finished = v.Progress?.Finished ?? false,
        progress = v.Progress is { LengthSeconds: > 0 } p ? Math.Clamp(p.Seconds / p.LengthSeconds, 0, 1) : 0
    };

    /// <summary>"Xem tiếp", the films of the film folder (most recently watched first) and the other videos by folder.</summary>
    private object Videos()
    {
        var list = ListVideos();
        var next = WatchProgress.Continue(History());
        var cont = next is null ? null : list.FirstOrDefault(v => string.Equals(v.Path, Path.GetFullPath(next.Video), StringComparison.OrdinalIgnoreCase));
        var films = list.Where(v => v.Film is not null).GroupBy(v => v.Group)
            .Select(g => (Episodes: g.ToList(), Last: g.Where(v => v.Progress is not null).MaxBy(v => v.Progress!.WatchedAt)))
            .OrderByDescending(f => f.Last?.Progress!.WatchedAt ?? DateTime.MinValue)
            .Select(f => new
            {
                key = PhoneVideos.Id(f.Episodes[0].Group),
                name = f.Episodes[0].Film,
                count = f.Episodes.Count,
                subtitles = f.Episodes.Count(v => v.Srt is not null),
                watched = f.Episodes.Count(v => v.Progress?.Finished == true),
                status = FilmStatus(f.Episodes, f.Last),
                videos = f.Episodes.Select(Item)
            });
        return new
        {
            @continue = cont is null ? null : new
            {
                id = cont.Id,
                name = Title(cont),
                text = next!.NextEpisode ? "Tập tiếp theo" : $"Xem tiếp từ {Clock(next.Start.TotalSeconds)}"
            },
            filmFolder = Data.Settings.FilmFolder,
            films,
            folders = list.Where(v => v.Film is null).GroupBy(v => v.Group).Select(g => new
            {
                name = Path.GetFileName(g.Key) is { Length: > 0 } name ? name : g.Key,
                path = g.Key,
                videos = g.Select(Item)
            })
        };
    }

    /// <summary>Where the learner is in a film: "Đang xem E04 · 12:30", "Tiếp theo: E05", or how many episodes it has.</summary>
    private string FilmStatus(List<PhoneVideo> episodes, PhoneVideo? last)
    {
        var count = episodes.Count == 1 ? "" : $"{episodes.Count} tập";
        if (last?.Progress is not { } progress) return count.Length > 0 ? count : "Chưa xem";
        var single = episodes.Count == 1;
        if (!progress.Finished) return single ? $"Đang xem · {Clock(progress.Seconds)}" : $"Đang xem {last.Label} · {Clock(progress.Seconds)}";
        var at = episodes.IndexOf(last);
        return at + 1 < episodes.Count ? $"Tiếp theo: {episodes[at + 1].Label}" : single ? "Đã xem" : "Đã xem hết";
    }

    /// <summary>One video to play: its subtitles with each word marked as on the computer, where to start and what comes next.</summary>
    private object Video(string id)
    {
        var video = FindVideo(id);
        var lines = ReadLines(video);
        var marker = new WordMarker(Data, Subtitles.NewWords(Data, lines));
        var nextVideo = NextOf(video);
        return new
        {
            id = video.Id,
            name = Title(video),
            phone = PhoneVideos.PlaysOnPhone(video.Path),
            resume = WatchProgress.ResumeAt(History(), video.Path).TotalSeconds,
            next = nextVideo is null ? null : new { id = nextVideo.Id, name = nextVideo.Label.Length > 0 ? nextVideo.Label : Name(nextVideo.Path) },
            lines = lines.Select(line => new
            {
                s = line.Start.TotalSeconds,
                e = line.End.TotalSeconds,
                parts = Subtitles.Mark(line.Text, marker).Select(part => part.Mark switch
                {
                    WordMark.New => (object)new { t = part.Text, m = "new" },
                    WordMark.Waiting => new { t = part.Text, m = "waiting" },
                    WordMark.Learning => new { t = part.Text, m = "learning", word = part.Word!.Text, phonetic = part.Word.Phonetic, meaning = part.Word.Meaning },
                    _ => new { t = part.Text }
                })
            })
        };
    }

    private static List<SubtitleLine> ReadLines(PhoneVideo video)
    {
        if (video.Srt is null) return [];
        try { return Subtitles.ReadSrt(File.ReadAllText(video.Srt)).OrderBy(l => l.Start).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>"/media/&lt;id&gt;" streams the file (the part a "Range" header asks for); "/media/&lt;id&gt;.vtt" its subtitles.</summary>
    private PhoneResponse Media(PhoneRequest request)
    {
        var name = request.Path["/media/".Length..];
        if (name.EndsWith(".vtt"))
            return PhoneResponse.Text(200, "text/vtt; charset=utf-8", PhoneVideos.ToVtt(ReadLines(FindVideo(name[..^4]))));
        var video = FindVideo(name);
        long size;
        try { size = new FileInfo(video.Path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new KeyNotFoundException(); }
        var ranged = request.Header("range") is { Length: > 0 };
        if (PhoneVideos.Range(request.Header("range"), size) is not var (start, length))
            return new PhoneResponse(416, "text/plain; charset=utf-8", [], new Dictionary<string, string> { ["Content-Range"] = $"bytes */{size}" });
        var headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" };
        if (ranged) headers["Content-Range"] = $"bytes {start}-{start + length - 1}/{size}";
        return new PhoneResponse(ranged ? 206 : 200, PhoneVideos.ContentType(video.Path), [], headers, new FileBody(video.Path, start, length));
    }

    /// <summary>Where the phone stopped, saved like the computer's player does, so "Xem tiếp" works across both.</summary>
    private object SaveProgress(ProgressBody body)
    {
        var video = FindVideo(body.Id);
        if (body.Length > 0 && body.Seconds >= 0)
            WatchProgress.Record(store.Folder, video.Path, TimeSpan.FromSeconds(body.Seconds), TimeSpan.FromSeconds(body.Length), _clock());
        return new { ok = true };
    }

    /// <summary>A new word tapped in a subtitle goes to "Từ của tôi", waiting for its meaning.</summary>
    private object SaveVideoWord(WordBody body)
    {
        QuickAddResult? result = null;
        store.Update(d => result = QuickAdd.Add(d, body.Text, _clock()));
        return new { ok = result!.Added, message = QuickAdd.Describe(result, body.Text) };
    }
}
