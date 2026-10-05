// Behaviour checks for Voca's core rules. Run: dotnet run --project tests/Voca.Checks
using System.IO;
using Voca.Models;
using Voca.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok) fails++;
}
var d0 = new DateTime(2026, 10, 6);

// ---------- bundled content ----------
var data = new AppData();
SeedData.EnsureSeeded(data);
Check(data.Plans.Count == 7 && data.Course.Count == 7, $"seed: 7 plans in the course ({data.Plans.Count})");
Check(data.Plans.All(p => p.DayCount == 7 && p.Words.Count == 140), "seed: every plan has 7 days × 20 words");
Check(data.Plans[0].Name == "Education — 7 ngày × 20 từ" && data.Plans[1].Name == "Tuần 3 — Work & Money" && data.Plans[1].Level == "B1–C1", "seed: course order Education, Tuần 3, …");
Check(data.Plans.All(p => p.Words.All(w => w.Phonetic.StartsWith('/') && w.Meaning.Length > 0 && w.Example.Length > 0)), "seed: every word has phonetic, meaning and example");
Check(data.Plans.All(p => p.DayTitles.Count == 7), "seed: every day has a title");
Check(CourseEngine.Current(data)?.Plan == data.Plans[0] && data.Position.Day == 1, "seed: starts at plan 1, day 1");
var seededAgain = data.Plans.Count; SeedData.EnsureSeeded(data);
Check(data.Plans.Count == seededAgain, "seed runs only once");

// ---------- today and the session ----------
var today = CourseEngine.BuildToday(data, d0, out var fresh);
Check(today.Count == 20 && fresh.Count == 20, "day 1: 20 new words, no reviews yet");
var quiz = CourseEngine.BuildQuiz(data, today, new Random(1));
Check(quiz.Count == 20 && quiz.All(q => q.Options.Count == 4 && q.Options.Distinct().Count() == 4 && q.Options.Contains(q.Correct)), "quiz: 4 distinct options including the answer");
Check(quiz.Where((q, i) => i % 2 == 0).All(q => q.AskWord) && quiz.Where((q, i) => i % 2 == 1).All(q => !q.AskWord), "quiz alternates meaning→word and word→meaning");
var plan1 = data.Plans[0];
Check(quiz.Where(q => q.AskWord).All(q => q.Options.Count(o => plan1.Words.Any(w => w.Text == o)) >= 3), "distractors come from the same plan first");
var answers = quiz.Select((q, i) => (q.Word, i != 1)).ToList(); // second answer wrong
CourseEngine.ApplySession(data, answers, fresh, d0.AddHours(9));
var wrong = answers[1].Word;
Check(data.Position.DayCompleted && data.StudyLog[ReviewScheduler.Key(d0)].SessionDone, "session completes the day");
Check(wrong.Review!.Due == d0.AddDays(1) && ReviewScheduler.IsWrongToday(wrong, d0) && wrong.ForgotCount == 1, "wrong answer: back tomorrow, flagged wrong today");
Check(answers[0].Word.Review!.Due == d0.AddDays(2) && answers[0].Word.CorrectCount == 1, "right answer: next review in 2 days");
Check(fresh.All(w => w.IntroducedOn == d0), "new words stamped as introduced today");
CourseEngine.ApplySession(data, answers, fresh, d0.AddHours(10)); // "học lại"
var log = data.StudyLog[ReviewScheduler.Key(d0)];
Check(log.Known == 19 && log.Forgot == 1 && wrong.ForgotCount == 1, "redoing the session the same day replaces answers instead of adding");

// ---------- spaced repetition ----------
var w1 = new Word { Text = "x" };
var sr = new AppData();
ReviewScheduler.Rate(sr, w1, true, d0);
ReviewScheduler.Rate(sr, w1, true, d0.AddDays(2));
ReviewScheduler.Rate(sr, w1, true, d0.AddDays(8));
Check(w1.Review!.IntervalDays == 15, "right ×3 → 2, 6, 15 days");
ReviewScheduler.Rate(sr, w1, false, d0.AddDays(23));
Check(w1.Review.IntervalDays == 1 && w1.Review.Lapses == 1 && Math.Abs(w1.Review.Ease - 2.3) < 1e-9, "wrong after reps: lapse, 1 day, ease 2.3");

// ---------- advancing ----------
Check(!CourseEngine.Advance(data, d0), "same day: stays on day 1");
Check(CourseEngine.Advance(data, d0.AddDays(1)) && data.Position.Day == 2, "next day: day 2");
Check(!CourseEngine.Advance(data, d0.AddDays(9)), "unfinished day: no advance even after missed days");
var t2 = CourseEngine.BuildToday(data, d0.AddDays(1), out var fresh2);
Check(fresh2.Count == 20 && t2.Contains(wrong), "day 2: 20 new + the wrong word as review");
data.Position.Day = 7; data.Position.DayCompleted = true; data.Position.DayCompletedOn = d0.AddDays(7);
Check(CourseEngine.Advance(data, d0.AddDays(8)) && data.Position.PlanId == data.Plans[1].Id && data.Position.Day == 1
      && data.Position.PendingSummaryPlanId == data.Plans[0].Id, "end of plan 1 → plan 2 day 1, summary queued");
Check(CourseEngine.StateOf(data, data.Plans[0].Id) == "done" && CourseEngine.StateOf(data, data.Plans[1].Id) == "now"
      && CourseEngine.StateOf(data, data.Plans[2].Id) == "wait", "plan states: done / now / wait");

// ---------- course ordering ----------
Check(!CourseEngine.Move(data, data.Plans[1].Id, 1), "the plan being studied cannot be moved");
Check(!CourseEngine.Move(data, data.Plans[2].Id, -1), "a waiting plan cannot jump ahead of the current one");
var p3 = data.Plans[2].Id; var p4 = data.Plans[3].Id;
Check(CourseEngine.Move(data, p3, 1) && data.Course[2] == p4 && data.Course[3] == p3, "waiting plans can swap");
CourseEngine.RemoveFromCourse(data, p4);
Check(!data.Course.Contains(p4) && data.Plans.Any(p => p.Id == p4), "remove from course keeps the plan");
CourseEngine.AddToCourse(data, p4);
Check(data.Course[^1] == p4, "add to course appends");
CourseEngine.RemoveFromCourse(data, data.Plans[1].Id);
Check(CourseEngine.Current(data)?.Plan == data.Plans[0] && data.Position.Day == 1, "removing the current plan restarts at the first plan");

// ---------- finishing, review plan, delete ----------
var last = data.Course[^1];
CourseEngine.SetPosition(data, last, 7); data.Position.DayCompleted = true; data.Position.DayCompletedOn = d0.AddDays(30);
Check(CourseEngine.Advance(data, d0.AddDays(31)) && data.Position.Finished && CourseEngine.NewWords(data).Count == 0, "end of the last plan → finished, reviews only");
var review = CourseEngine.CreateReviewPlan(data, 20, 10, d0.AddDays(31));
Check(review is not null && review.Words.Count == 1 && review.Words[0].Text == wrong.Text && review.Words[0].Review is null, "review plan copies forgotten words fresh");
Check(!data.Position.Finished && data.Position.PlanId == review?.Id, "review plan after a finished course becomes the current plan");
CourseEngine.DeletePlan(data, review!.Id);
Check(data.Plans.All(p => p.Id != review.Id) && !data.Course.Contains(review.Id), "delete plan removes it everywhere");

// ---------- import form ----------
var pasted = """
    Dưới đây là lộ trình của bạn:

    ```markdown
    # Tên bộ từ: **Travel — 2 ngày × 5 từ**
    Cấp độ: B1–B2
    Mô tả: Từ vựng du lịch

    ## Ngày 1 — Sân bay

    | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
    |---|---|---|---|---|---|
    | 1 | boarding pass | /ˈbɔːdɪŋ pɑːs/ | n | thẻ lên máy bay | Show your boarding pass. |
    | 2 | either | [ˈaɪðə] | conj | hoặc A \| hoặc B | either A \| B |
    | 3 | **routine** /ruːˈtiːn/ | n | thói quen | daily routine |
    | 4 | delay | /dɪˈleɪ/ | n | sự chậm trễ | |
    | 5 | layover | /ˈleɪəʊvə(r)/ | n | quá cảnh | We had a layover. |

    ## Ngày 2 — Khách sạn

    | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
    |---|---|---|---|---|---|
    | 6 | hostel | /ˈhɒstl/ | n | nhà nghỉ | We stayed in a hostel. |
    | 7 | delay | /dɪˈleɪ/ | n | sự chậm trễ | Another delay. |
    ```
    Chúc bạn học tốt!
    """;
var read = PlanFormat.Read(pasted, new PlanRequest("Travel", 2, 5, "B1–B2", "", "x"));
var rp = read.Plan!;
Check(read.CanImport && rp.Name == "Travel — 2 ngày × 5 từ" && rp.Level == "B1–B2" && rp.Description == "Từ vựng du lịch", "form: header read, greeting and fences ignored");
Check(rp.DayCount == 2 && rp.DayTitle(1) == "Sân bay" && rp.Words.Count == 7, "form: 2 days with titles, 7 words");
Check(rp.Words[1].Phonetic == "/ˈaɪðə/" && rp.Words[1].Meaning == "hoặc A | hoặc B", "form: [..] phonetic normalised, escaped pipes kept");
Check(rp.Words[2].Text == "routine" && rp.Words[2].Phonetic == "/ruːˈtiːn/" && rp.Words[2].PartOfSpeech == "n", "form: 5-column rows still supported");
Check(read.Warnings.Any(w => w.Contains("Ngày 2 có 2 từ")) && read.Warnings.Any(w => w.Contains("thiếu ví dụ")) && read.Warnings.Any(w => w.Contains("delay")),
      "form warnings: short day, missing example, word repeated across days");
var bad = PlanFormat.Read("## Ngày 1\n| 1 | a | /a/ | n | x | y |\n| 2 | A | /a/ | n | x | y |");
Check(!bad.CanImport && bad.Errors.Any(e => e.Contains("tên")) && bad.Errors.Any(e => e.Contains("trùng")), "form errors: missing name, duplicate in a day");
var json = PlanFormat.Read("""{"name":"J","level":"A2","days":[{"day":1,"title":"T","words":[{"word":"cat","phonetic":"kæt","partOfSpeech":"n","meaning":"mèo","example":"A cat."}]}]}""");
Check(json.CanImport && json.Plan!.Words[0].Phonetic == "/kæt/" && json.Plan.DayTitle(1) == "T", "JSON form accepted");
var roundTrip = PlanFormat.Read(PlanFormat.ToMarkdown(data.Plans[0]));
Check(roundTrip.CanImport && roundTrip.Plan!.Words.Count == 140 && roundTrip.Plan.DayTitles.Count == 7 && roundTrip.Plan.Name == data.Plans[0].Name, "export → import round trip keeps every word and day title");
var prompt = PlanFormat.BuildPrompt(new PlanRequest("Du lịch", 7, 20, "A2–C1", "", "Du lịch — 7 ngày × 20 từ"));
Check(prompt.Contains("7 ngày, mỗi ngày đúng 20 từ") && prompt.Contains("# Tên bộ từ: Du lịch — 7 ngày × 20 từ") && prompt.Contains("```markdown"), "prompt asks for the exact form");

// ---------- storage ----------
var folder = Path.Combine(Path.GetTempPath(), "voca-checks-" + Guid.NewGuid().ToString("N"));
var store = new Store(folder);
store.Update(SeedData.EnsureSeeded);
store.Update(d => d.Settings.RotationSeconds = 33);
store.Update(d => d.Settings.RotationSeconds = 44);
File.WriteAllText(Path.Combine(folder, "voca.json"), "{ broken");
var reopened = new Store(folder);
Check(reopened.Data.Plans.Count == 7 && reopened.Data.Settings.RotationSeconds == 33, "damaged file: restored from .bak");
Check(Directory.GetFiles(folder, "voca.json.corrupt-*").Length == 1, "damaged file kept for recovery");
var changed = 0; reopened.Changed += () => changed++; reopened.Save();
Check(changed == 1, "save raises Changed for open windows");
Directory.Delete(folder, true);

// ---------- import from Voca 1 ----------
var v1Folder = Path.Combine(Path.GetTempPath(), "voca-v1-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(v1Folder);
var v1File = Path.Combine(v1Folder, "vocabulary.json");
File.WriteAllText(v1File, """
    {"Words":[
      {"Word":"school","Phonetic":"/skuːl/","PartOfSpeech":"n","Meaning":"trường học","Example":"x","SetName":"Education — 7 ngày × 20 từ","StudyDate":"2026-10-05T00:00:00","RemoteSetId":1,"RemoteId":10,"DayNumber":1,"DayTitle":"Trường lớp","Review":{"Reps":1,"Lapses":0,"Ease":2.5,"IntervalDays":2,"Due":"2026-10-07T00:00:00","LastReviewed":"2026-10-05T09:00:00","LastKnown":true},"ForgotCount":0,"CorrectCount":1},
      {"Word":"boarding pass","Phonetic":"/ˈbɔːdɪŋ pɑːs/","PartOfSpeech":"n","Meaning":"thẻ lên máy bay","Example":"y","SetName":"Du lịch — 7 ngày × 20 từ","RemoteSetId":8,"RemoteId":80,"DayNumber":1,"DayTitle":"Sân bay","ForgotCount":2},
      {"Word":"delay","Phonetic":"/dɪˈleɪ/","PartOfSpeech":"n","Meaning":"sự chậm trễ","Example":"z","SetName":"Du lịch — 7 ngày × 20 từ","RemoteSetId":8,"RemoteId":81,"DayNumber":2,"DayTitle":"Khách sạn"},
      {"Word":"community","Phonetic":"/kəˈmjuːnəti/","PartOfSpeech":"n","Meaning":"cộng đồng","Example":"","SetName":"","StudyDate":"2026-10-05T00:00:00","Review":{"Reps":1,"Ease":2.5,"IntervalDays":2,"Due":"2026-10-07T00:00:00","LastReviewed":"2026-10-05T09:00:00","LastKnown":true}},
      {"Word":"unlearned","SetName":"","StudyDate":"2026-10-05T00:00:00"}],
     "StudyLog":{"2026-10-05":{"Known":5,"Forgot":1,"SessionDone":true}},
     "Course":{"CurrentSetId":8,"CurrentDay":2,"DayCompleted":false},
     "CourseCache":[{"SetId":1,"Name":"Education — 7 ngày × 20 từ","Level":"A2–C1","DayCount":7,"DayTitles":{"1":"Trường lớp"}},
                    {"SetId":8,"Name":"Du lịch — 7 ngày × 20 từ","Level":"A2–C1","DayCount":2,"DayTitles":{"1":"Sân bay","2":"Khách sạn"}}],
     "RotationSeconds":25,"RevealSeconds":5,"MaxReviewsPerDay":20,"DisplayMode":"flash","WordPillLeft":300,"WordPillTop":1000}
    """);
var imp = new AppData();
SeedData.EnsureSeeded(imp);
var r1 = VocaV1Import.Import(imp, v1File, includeSettings: true);
var travel = imp.Plans.FirstOrDefault(p => p.Name.StartsWith("Du lịch"));
Check(r1.PlansAdded == 1 && r1.PlansMatched == 1 && imp.Plans.Count == 9 && travel is not null && imp.Course[^1] == travel.Id, "v1: Du lịch added to the end of the course, Education matched by name");
Check(travel!.DayCount == 2 && travel.DayTitle(2) == "Khách sạn" && travel.Words[0].ForgotCount == 2, "v1: added plan keeps days, titles and counters");
Check(imp.Plans[0].Words.First(w => w.Text == "school").Review?.Reps == 1, "v1: progress copied onto the matching bundled word");
Check(imp.Plans.Any(p => p.Name == "Từ đã học ở Voca 1" && p.Words.Count == 1 && !imp.Course.Contains(p.Id)), "v1: learned loose words kept outside the course, unlearned ones skipped");
Check(imp.Position.PlanId == travel.Id && imp.Position.Day == 2 && imp.StudyLog["2026-10-05"].Known == 5, "v1: position and study log restored");
Check(imp.Settings.RotationSeconds == 25 && imp.Settings.DisplayMode == "flash" && imp.Settings.PillLeft == 300, "v1: display settings and pill position on first import");
VocaV1Import.Import(imp, v1File);
Check(imp.Plans.Count == 9 && imp.Course.Count == 8 && imp.Plans.Single(p => p.Name == "Từ đã học ở Voca 1").Words.Count == 1, "v1: importing twice creates no duplicates");
Directory.Delete(v1Folder, true);

// ---------- moving to another computer ----------
var machineA = new AppData();
SeedData.EnsureSeeded(machineA);
var own = PlanFormat.Read("""
    # Tên bộ từ: Food
    ## Ngày 1 — Món ăn
    | 1 | rice | /raɪs/ | n | cơm | I eat rice. |
    | 2 | noodle | /ˈnuːdl/ | n | mì | Hot noodles. |
    """).Plan!;
machineA.Plans.Add(own); CourseEngine.AddToCourse(machineA, own.Id);
var aFresh = CourseEngine.NewWords(machineA);
CourseEngine.ApplySession(machineA, aFresh.Select((w, i) => (w, i != 0)).ToList(), aFresh, d0.AddHours(9));
CourseEngine.Advance(machineA, d0.AddDays(1));
var pkgFile = Path.Combine(Path.GetTempPath(), "voca-" + Guid.NewGuid().ToString("N") + Transfer.Extension);
Transfer.Save(Transfer.CreatePackage(machineA, machineA.Plans.Select(p => p.Id).ToList(), includeProgress: true), pkgFile);
var pkg = Transfer.Load(pkgFile);
Check(pkg.Plans.Count == 8 && pkg.Course.Count == 8 && pkg.IncludesProgress && pkg.Plans[^1].Name == "Food", "package: 8 plans in course order with progress");
var machineB = new AppData();
SeedData.EnsureSeeded(machineB);
var tr = Transfer.Import(machineB, pkg, restorePosition: true);
Check(tr.PlansAdded == 1 && tr.PlansMatched == 7 && machineB.Plans.Count == 8 && machineB.Course.Count == 8, "import: Food added, 7 bundled plans matched (no duplicates)");
Check(tr.WordsUpdated == 20 && machineB.Plans[0].Words.Count(w => w.Review is not null) == 20 && machineB.Plans[0].Words[0].ForgotCount == 1, "import: progress of 20 words carried over");
Check(machineB.Position.PlanId == machineB.Plans[0].Id && machineB.Position.Day == 2 && machineB.StudyLog.ContainsKey(ReviewScheduler.Key(d0)), "import: position (day 2) and study log restored");
var again = Transfer.Import(machineB, pkg, restorePosition: false);
Check(again.PlansAdded == 0 && again.WordsUpdated == 0 && machineB.Plans.Count == 8, "import twice: nothing duplicated, older progress not re-applied");
var newer = machineB.Plans[0].Words[0]; ReviewScheduler.Rate(machineB, newer, true, d0.AddDays(3));
Transfer.Import(machineB, pkg, restorePosition: false);
Check(newer.Review!.LastReviewed == d0.AddDays(3), "import keeps the newer progress already on this computer");
var noProgress = Transfer.CreatePackage(machineA, [own.Id], includeProgress: false);
Check(noProgress.Plans.Single().Words.All(w => w.Review is null) && noProgress.StudyLog.Count == 0 && noProgress.Position is null, "package without progress carries words only");
File.Delete(pkgFile);
var text = Transfer.ToText(machineA, machineA.Course.Take(3).ToList());
var many = Transfer.ReadMany("Gửi bạn:\n```markdown\n" + text + "\n```");
Check(many.Count == 3 && many.All(r => r.CanImport) && many.Select(r => r.Plan!.Words.Count).SequenceEqual([140, 140, 140]), "text with 3 plans splits into 3 importable plans");
Check(many.All(r => CourseEngine.FindMatchingPlan(machineB, r.Plan!.Name, r.Plan.Words.Select(w => w.Text)) is not null), "plans already in the library are recognised (skipped on paste)");
Check(Transfer.ReadMany(PlanFormat.Example).Count == 1, "a single plan is still read as one");

// ---------- stats ----------
var st = new AppData();
foreach (var off in new[] { 0, -1, -2, -5, -6, -7, -8 }) st.StudyLog[ReviewScheduler.Key(d0.AddDays(off))] = new DayLog { Known = 1 };
var s = StatsService.Compute(st, d0);
Check(s.CurrentStreak == 3 && s.BestStreak == 4, "streak 3, best 4");
st.StudyLog.Remove(ReviewScheduler.Key(d0));
Check(StatsService.Compute(st, d0).CurrentStreak == 2, "not studied yet today: yesterday's streak still counts");

// ---- taskbar text style ----
var styleFolder = Path.Combine(Path.GetTempPath(), "voca-style-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(styleFolder);
File.WriteAllText(Path.Combine(styleFolder, "voca.json"), """{ "DataVersion": 1, "Settings": { "RotationSeconds": 30 } }""");
var oldFile = new Store(styleFolder);
Check(oldFile.Data.Settings.RotationSeconds == 30 && oldFile.Data.Settings.Pill.FontSize == 16 && oldFile.Data.Settings.Pill.TextColor == "#F7F7FA",
    "style: data saved before text settings existed gets the default look");
oldFile.Update(d => d.Settings.Pill = new PillStyle { FontFamily = "Consolas", FontSize = 22, TextColor = "#FFE066", Background = "#1E2130", BackgroundOpacity = 50, MaxWidth = 500 });
var reread = new Store(styleFolder).Data.Settings.Pill;
Check(reread.FontFamily == "Consolas" && reread.FontSize == 22 && reread.TextColor == "#FFE066" && reread.Background == "#1E2130" && reread.MaxWidth == 500,
    "style: text settings survive a restart");
Directory.Delete(styleFolder, true);
Check(PillPainter.Normalize("ffe066") == "#FFE066" && PillPainter.Normalize("#abc") is null && PillPainter.Normalize("red") is null && PillPainter.Normalize("") is null,
    "style: colours accept #RRGGBB (with or without #), reject anything else");
Check(PillPainter.WindowHeight(new PillStyle { FontSize = 16 }) == 48 && PillPainter.WindowHeight(new PillStyle { FontSize = 32 }) > 48
      && PillPainter.Size(new PillStyle { FontSize = 99 }) == PillStyle.MaxFontSize && PillPainter.Width(new PillStyle { MaxWidth = 5 }) == PillStyle.NarrowestWidth,
    "style: big fonts grow the pill, out-of-range values are clamped");

// ---- tests (bài kiểm tra) ----
var td = new AppData();
SeedData.EnsureSeeded(td);
var tplan = td.Plans[1];
var tpool = TestBuilder.Pool(tplan, 2, 4);
Check(tpool.Count == 60 && tpool.All(w => w.Day is >= 2 and <= 4), "test: pool is exactly the chosen days (3 × 20)");
var tpick = TestBuilder.Pick(tpool, 20, new Random(3));
Check(tpick.Count == 20 && tpick.Distinct().Count() == 20 && TestBuilder.Pick(tpool, 0, new Random(3)).Count == 60, "test: picks N different words, 0 = all");
TestKind[] allKinds = [TestKind.WordToMeaning, TestKind.MeaningToWord, TestKind.Typing, TestKind.Listening];
var tq = TestBuilder.Build(td, tpick, allKinds, new Random(4));
Check(tq.Count == 20 && tq.Select(q => q.Word).Distinct().Count() == 20, "test: one question per word");
Check(allKinds.All(k => tq.Count(q => q.Kind == k) == 5), "test: the 4 kinds are spread evenly (5 each)");
Check(tq.Where(q => q.Kind != TestKind.Typing).All(q => q.Options.Count == 4 && q.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4 && q.Options.Contains(q.Correct)),
    "test: choice questions have 4 distinct options including the answer");
Check(tq.Where(q => q.Kind == TestKind.WordToMeaning).All(q => q.Correct == q.Word.Meaning && q.Prompt == q.Word.Text)
      && tq.Where(q => q.Kind is TestKind.MeaningToWord or TestKind.Listening).All(q => q.Correct == q.Word.Text)
      && tq.Where(q => q.Kind == TestKind.Typing).All(q => q.Options.Count == 0 && q.Prompt == q.Word.Meaning),
    "test: each kind asks the right side");
Check(tq.Where(q => q.Kind == TestKind.Listening).All(q => q.Prompt == ""), "test: listening does not show the word");
Check(tq.Where(q => q.Kind == TestKind.MeaningToWord).All(q => q.Options.Count(o => tplan.Words.Any(w => w.Text == o)) == 4),
    "test: wrong options come from the same plan");
var tiny = new AppData { Plans = [new Plan { Name = "x", Words = [new Word { Day = 1, Text = "solo", Meaning = "một mình" }] }] };
Check(TestBuilder.Build(tiny, tiny.Plans[0].Words, [TestKind.WordToMeaning], new Random(1)).Single().Kind == TestKind.Typing,
    "test: a choice question with no possible wrong option becomes typing");
Check(TestBuilder.Build(td, tpick, [], new Random(1)).Count == 0, "test: no kinds chosen → no questions");
Check(TestBuilder.IsTypedCorrectly("  Take   After ", "take after") && TestBuilder.IsTypedCorrectly("dont", "don't") == false
      && TestBuilder.IsTypedCorrectly("don’t", "don't") && TestBuilder.IsTypedCorrectly("keen on", "(be) keen on")
      && TestBuilder.IsTypedCorrectly("be keen on", "(be) keen on") && TestBuilder.IsTypedCorrectly("color", "colour/color")
      && TestBuilder.IsTypedCorrectly("hello.", "hello") && !TestBuilder.IsTypedCorrectly("", "hello") && !TestBuilder.IsTypedCorrectly("helo", "hello"),
    "test: typing ignores case, spaces, curly quotes, brackets; accepts a/b; rejects typos");
Check(TestBuilder.Hint("take after") == "t___ a____", "test: hint shows first letters");
// ---- mistake days (ngày học từ sai) ----
var md = new AppData();
SeedData.EnsureSeeded(md);
var mPlan = md.Plans[0];
var day1 = CourseEngine.BuildToday(md, d0, out var mFresh);
var d1 = d0.AddDays(1); var d2 = d0.AddDays(2);
// d0: session with 3 wrong answers, then a test with 2 wrong (one of them the same word again).
CourseEngine.ApplySession(md, day1.Select((w, i) => (w, i >= 3)).ToList(), mFresh, d0.AddHours(9));
MistakeDays.Record(md, [day1[0], mPlan.Words[30]], d0.AddHours(10));
Check(md.Mistakes.Count == 4 && md.Mistakes.Single(m => m.WordId == day1[0].Id).Times == 2, "mistakes: wrong answers from sessions and tests are kept, repeats counted");
Check(MistakeDays.Waiting(md)[0].Word == day1[0], "mistakes: most-often-wrong word first");
Check(md.Position.DayCompleted, "mistakes: recording does not change the course day");
Check(MistakeDays.Schedule(md, d1, d0) == 4 && MistakeDays.Upcoming(md, d0) is not null && MistakeDays.Active(md, d0) is null,
    "mistakes: scheduled for tomorrow with all 4 words; not active today");
CourseEngine.BuildToday(md, d0, out var stillDay1);
Check(stillDay1.SetEquals(mFresh), "mistakes: today's lesson is unchanged when the day is tomorrow");
MistakeDays.Record(md, [mPlan.Words[31]], d0.AddHours(20));
Check(md.MistakeDay!.WordIds.Count == 5, "mistakes: a wrong word before the mistake day joins it");
// d1: the course would move to day 2, but the mistake day replaces the lesson.
CourseEngine.Advance(md, d1);
var mToday = CourseEngine.BuildToday(md, d1, out var mWords);
Check(md.Position.Day == 2 && mWords.Count == 5 && mWords.All(w => md.MistakeDay.WordIds.Contains(w.Id)) && !mWords.Any(w => w.Day == 2 && w.Id != mPlan.Words[31].Id && w.Id != mPlan.Words[30].Id),
    "mistakes: on the mistake day its words are today's words instead of day 2");
var wrongAgain = mWords.First(w => w.Id == day1[0].Id);
CourseEngine.ApplySession(md, mToday.Select(w => (w, w != wrongAgain)).ToList(), mWords, d1.AddHours(9));
Check(md.Mistakes.Count == 1 && md.Mistakes[0].WordId == wrongAgain.Id && md.Mistakes[0].Times == 3, "mistakes: right answers leave the list, the wrong one stays (counted again)");
Check(md.MistakeDay!.Done && md.Position.Day == 2 && !md.Position.DayCompleted, "mistakes: the mistake day is done, the course day stays open");
CourseEngine.BuildToday(md, d1, out var afterDone);
Check(afterDone.Count == 5, "mistakes: finished mistake day stays on the taskbar until tomorrow");
// d2: back to the course, day 2.
Check(!CourseEngine.Advance(md, d2) && MistakeDays.Cleanup(md, d2) && md.MistakeDay is null, "mistakes: next day the course does not skip, the finished day is cleared");
CourseEngine.BuildToday(md, d2, out var day2Words);
Check(day2Words.All(w => w.Day == 2) && day2Words.Count == 20, "mistakes: the course continues with day 2");
Check(MistakeDays.Schedule(md, d2, d2) == 1 && MistakeDays.Active(md, d2) is not null, "mistakes: can be scheduled for today");
CourseEngine.BuildToday(md, d2, out var todayMistakes);
Check(todayMistakes.Single() == wrongAgain, "mistakes: today's words switch to the mistake day right away");
MistakeDays.Cancel(md);
CourseEngine.BuildToday(md, d2, out var cancelled);
Check(md.MistakeDay is null && cancelled.Count == 20 && md.Mistakes.Count == 1, "mistakes: cancelling keeps the words in the list and restores the lesson");
MistakeDays.Schedule(md, d2.AddDays(1), d2);
Check(MistakeDays.Active(md, d2.AddDays(4)) is not null, "mistakes: a missed mistake day is still waiting days later");
Check(MistakeDays.Schedule(new AppData(), d1, d0) == 0, "mistakes: nothing to schedule without wrong words");
var orphan = new AppData();
SeedData.EnsureSeeded(orphan);
orphan.MistakeDay = new MistakeDay { Date = d0, WordIds = [Guid.NewGuid()] };
var orphanToday = CourseEngine.BuildToday(orphan, d0, out var orphanFresh);
CourseEngine.ApplySession(orphan, orphanToday.Select(w => (w, true)).ToList(), orphanFresh, d0.AddHours(9));
Check(orphanFresh.Count == 20 && orphan.Position.DayCompleted && orphan.MistakeDay is null, "mistakes: a mistake day whose words were deleted falls back to the course day");
Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;
