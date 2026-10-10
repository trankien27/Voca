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
var d0 = DateTime.Today;

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
Check(data.Position.DayCompleted && data.Position.Day == 1 && data.StudyLog[ReviewScheduler.Key(d0)].SessionDone,
    "session marks today's session done, the course day stays");
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

// ---------- advancing: only once every word of the day is marked learned ----------
var adv = new AppData();
SeedData.EnsureSeeded(adv);
var advDay1 = CourseEngine.DayWords(adv);
CourseEngine.ApplySession(adv, advDay1.Select(w => (w, true)).ToList(), advDay1, d0.AddHours(9));
Check(!CourseEngine.Advance(adv, d0) && adv.Position is { Day: 1, DayCompleted: true }, "same date: stays on day 1");
Check(CourseEngine.Advance(adv, d0.AddDays(1)) && adv.Position is { Day: 1, DayCompleted: false }
      && CourseEngine.BuildToday(adv, d0.AddDays(1), out var advFresh).Count > 0 && advFresh.SetEquals(advDay1),
    "next date, words not learned: still day 1 with the same words, today's session to do again");
Check(!CourseEngine.Advance(adv, d0.AddDays(9)) && adv.Position.Day == 1, "days away do not move the course");
foreach (var w in advDay1.Take(19)) CourseEngine.MarkLearned(adv, w, d0.AddDays(9));
Check(!CourseEngine.Advance(adv, d0.AddDays(9)) && adv.Position.Day == 1 && CourseEngine.NewWords(adv).Single() == advDay1[19],
    "19 of 20 learned: still day 1, only the last word left");
CourseEngine.MarkLearned(adv, advDay1[19], d0.AddDays(9));
Check(CourseEngine.Advance(adv, d0.AddDays(9)) && adv.Position.Day == 2 && CourseEngine.NewWords(adv).Count == 20 && CourseEngine.NewWords(adv).All(w => w.Day == 2),
    "every word learned: day 2 right away, the same date");
foreach (var w in adv.Plans[0].Words.Where(w => w.Day is 2 or 3)) CourseEngine.MarkLearned(adv, w, d0.AddDays(9));
Check(CourseEngine.Advance(adv, d0.AddDays(9)) && adv.Position.Day == 4, "days already learned are all passed");
var emptyPlan = new AppData { Plans = [new Plan { Name = "trống" }] };
emptyPlan.Course.Add(emptyPlan.Plans[0].Id);
CourseEngine.EnsurePosition(emptyPlan);
Check(!CourseEngine.Advance(emptyPlan, d0) && !emptyPlan.Position.Finished && emptyPlan.Position.Day == 1, "a plan without words holds the course");
adv.Position.Day = 6; // older versions moved on each date, leaving days 4–5 unstudied
var skipped5 = adv.Plans[0].Words.Where(w => w.Day == 5).ToList();
Check(CourseEngine.SkippedDay(adv) is var (skippedDay, skippedWords) && skippedDay == 5 && skippedWords.SequenceEqual(skipped5),
    "a day skipped by an older version is offered to catch up (Học bù)");
Check(!CourseEngine.BuildToday(adv, d0.AddDays(9), out _).Any(skipped5.Contains), "words of a skipped day stay off the taskbar");
MistakeDays.ApplyPractice(adv, skipped5.Select(w => (w, true)).ToList(), d0.AddDays(9).AddHours(8));
Check(CourseEngine.SkippedDay(adv) is var (nextSkipped, _) && nextSkipped == 4 && skipped5.All(w => w.Review is not null),
    "after catching up its words are reviewed and the earlier skipped day is offered");
// 2.8.1–2.8.2 put skipped days into reviews without any answer; that is undone.
var repair = new AppData();
SeedData.EnsureSeeded(repair);
var fake = repair.Plans[0].Words.Take(3).ToList();
foreach (var w in fake) { w.Review = new ReviewState { Due = d0 }; w.IntroducedOn = d0.AddDays(-1); }
var answered = repair.Plans[0].Words[5];
ReviewScheduler.Rate(repair, answered, true, d0.AddDays(-1));
var mine = QuickAdd.SaveWord(repair, "flummox", "", "v", "làm bối rối", "", d0)!;
Check(CourseEngine.DropUnstudiedReviews(repair) && fake.All(w => w.Review is null && w.IntroducedOn is null)
      && answered.Review is not null && mine.Review is not null && !CourseEngine.DropUnstudiedReviews(repair),
    "repair: reviews of never-answered course words are dropped; answered words and “Từ của tôi” keep theirs");
var t2 = CourseEngine.BuildToday(data, d0.AddDays(1), out var fresh2);
Check(fresh2.Count == 20 && fresh2.Contains(wrong) && t2.Count == 20, "next date, nothing learned: day 1's 20 words again, the wrong one among them");
data.Position.Day = 7;
foreach (var w in data.Plans[0].Words.Where(w => w.Day == 7)) CourseEngine.MarkLearned(data, w, d0.AddDays(7));
Check(CourseEngine.Advance(data, d0.AddDays(7)) && data.Position.PlanId == data.Plans[1].Id && data.Position.Day == 1
      && data.Position.PendingSummaryPlanId == data.Plans[0].Id, "last day of plan 1 learned → plan 2 day 1, summary queued");
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
CourseEngine.SetPosition(data, last, 7);
foreach (var w in data.Plans.Single(p => p.Id == last).Words.Where(w => w.Day == 7)) CourseEngine.MarkLearned(data, w, d0.AddDays(30));
Check(CourseEngine.Advance(data, d0.AddDays(30)) && data.Position.Finished && CourseEngine.NewWords(data).Count == 0, "end of the last plan → finished, reviews only");
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
CourseEngine.SetPosition(machineA, machineA.Plans[0].Id, 2);
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
// d1: the mistake day replaces the lesson and the course pauses.
CourseEngine.Advance(md, d1);
var mToday = CourseEngine.BuildToday(md, d1, out var mWords);
Check(md.Position.Day == 1 && mWords.Count == 5 && mWords.All(w => md.MistakeDay.WordIds.Contains(w.Id)),
    "mistakes: on the mistake day its words are today's words and the course pauses");
var wrongAgain = mWords.First(w => w.Id == day1[0].Id);
CourseEngine.ApplySession(md, mToday.Select(w => (w, w != wrongAgain)).ToList(), mWords, d1.AddHours(9));
Check(md.Mistakes.Count == 1 && md.Mistakes[0].WordId == wrongAgain.Id && md.Mistakes[0].Times == 3, "mistakes: right answers leave the list, the wrong one stays (counted again)");
Check(md.MistakeDay!.Done && md.Position.Day == 1, "mistakes: finishing the mistake day does not move the course");
CourseEngine.BuildToday(md, d1, out var afterDone);
Check(afterDone.Count == 5, "mistakes: finished mistake day stays on the taskbar until tomorrow");
// d2: back to the course, still day 1 (its words are not learned yet).
CourseEngine.Advance(md, d2);
Check(md.Position.Day == 1 && MistakeDays.Cleanup(md, d2) && md.MistakeDay is null,
    "mistakes: next day the course is back, the finished mistake day is cleared");
CourseEngine.BuildToday(md, d2, out var day2Words);
Check(day2Words.All(w => w.Day == 1) && day2Words.Count == 20, "mistakes: the course continues with day 1's words");
Check(MistakeDays.Schedule(md, d2, d2) == 1 && MistakeDays.Active(md, d2) is not null, "mistakes: can be scheduled for today");
CourseEngine.BuildToday(md, d2, out var todayMistakes);
Check(todayMistakes.Single() == wrongAgain, "mistakes: today's words switch to the mistake day right away");
MistakeDays.Cancel(md);
CourseEngine.BuildToday(md, d2, out var cancelled);
Check(md.MistakeDay is null && cancelled.Count == 20 && md.Mistakes.Count == 1, "mistakes: cancelling keeps the words in the list and restores the lesson");
MistakeDays.Schedule(md, d2.AddDays(1), d2);
Check(MistakeDays.Active(md, d2.AddDays(1)) is not null && MistakeDays.Active(md, d2.AddDays(2)) is null
      && MistakeDays.Cleanup(md, d2.AddDays(2)) && md.MistakeDay is null && md.Mistakes.Count == 1,
    "mistakes: a missed mistake day is dropped the next day (words stay in the lists), so the course is never held up");
Check(MistakeDays.Schedule(new AppData(), d1, d0) == 0, "mistakes: nothing to schedule without wrong words");

// ---- a mistake day does not hold back a learned day ----
var pz = new AppData();
SeedData.EnsureSeeded(pz);
MistakeDays.Record(pz, [pz.Plans[0].Words[30]], d0);
MistakeDays.Schedule(pz, d0, d0);
foreach (var w in CourseEngine.DayWords(pz)) CourseEngine.MarkLearned(pz, w, d0.AddHours(9));
Check(CourseEngine.Advance(pz, d0) && pz.Position.Day == 2 && MistakeDays.Active(pz, d0) is not null,
    "mistakes: learning every word of the day moves the course on, even on a mistake day");

// ---- mistake lists ----
var ml = new AppData();
SeedData.EnsureSeeded(ml);
var mlPlan = ml.Plans[0];
var mlDay1 = CourseEngine.BuildToday(ml, d0, out var mlFresh);
CourseEngine.ApplySession(ml, mlDay1.Select((w, i) => (w, i >= 2)).ToList(), mlFresh, d0.AddHours(8));
var sessionList = ml.MistakeLists.Single();
Check(sessionList.Title == MistakeDays.SessionTitle(d0) && sessionList.WordIds.Count == 2, "lists: a session's wrong words make the day's list");
var t1 = d0.AddHours(9);
var testListId = MistakeDays.Record(ml, [mlDay1[0], mlPlan.Words[40], mlPlan.Words[41]], t1, "Bài kiểm tra 09:00 · Education", null);
Check(ml.MistakeLists.Count == 2 && ml.MistakeLists.Single(l => l.Id == testListId).WordIds.Count == 3,
    "lists: a test makes its own list, including a word already in another list");
Check(MistakeDays.Record(ml, [mlPlan.Words[40]], t1.AddMinutes(2), "ignored", testListId) == testListId && ml.MistakeLists.Count == 2,
    "lists: a retry round adds to the same test list");
var t2Id = MistakeDays.Record(ml, [mlPlan.Words[42]], d0.AddHours(11), "Bài kiểm tra 11:00 · Education", null);
Check(t2Id != testListId && ml.MistakeLists.Count == 3, "lists: a later test gets a new list");
CourseEngine.ApplySession(ml, [(mlPlan.Words[40], false), (mlPlan.Words[43], false)], [], d0.AddHours(20));
Check(sessionList.WordIds.Contains(mlPlan.Words[43].Id) && !sessionList.WordIds.Contains(mlPlan.Words[40].Id),
    "lists: the day's session list only adds words not already in a list");
Check(MistakeDays.Lists(ml)[0].List.Id == t2Id, "lists: newest list first");
var positionBefore = (ml.Position.Day, ml.Position.DayCompleted);
var practiceWords = MistakeDays.Lists(ml).Single(x => x.List.Id == testListId).Words;
MistakeDays.ApplyPractice(ml, practiceWords.Select(w => (w, w != mlPlan.Words[41])).ToList(), d0.AddHours(21));
Check(ml.MistakeLists.Single(l => l.Id == testListId).WordIds.SequenceEqual([mlPlan.Words[41].Id]),
    "lists: practice removes words answered right, keeps the wrong one");
Check(!sessionList.WordIds.Contains(mlDay1[0].Id) && ml.Mistakes.All(m => m.WordId != mlDay1[0].Id),
    "lists: a word answered right leaves every list and the count");
Check((ml.Position.Day, ml.Position.DayCompleted) == positionBefore && MistakeDays.TimesWrong(ml, mlPlan.Words[41]) == 2,
    "lists: practice does not move the course; a wrong answer is counted again");
MistakeDays.ApplyPractice(ml, [(mlPlan.Words[42], true)], d0.AddHours(22));
Check(ml.MistakeLists.All(l => l.Id != t2Id), "lists: a list with every word answered right disappears");
MistakeDays.Record(ml, [mlPlan.Words[50]], d0.AddHours(22));
Check(MistakeDays.Tidy(ml, d0.AddHours(23)) && ml.MistakeLists.Any(l => l.Title == MistakeDays.EarlierTitle && l.WordIds.Contains(mlPlan.Words[50].Id)),
    "lists: wrong words from before lists existed are gathered in one list");
Check(!MistakeDays.Tidy(ml, d0.AddHours(23)), "lists: tidying twice changes nothing");
MistakeDays.Record(ml, [mlPlan.Words[41]], d0.AddHours(23), MistakeDays.SessionTitle(d0));
MistakeDays.DeleteList(ml, testListId!.Value);
Check(ml.MistakeLists.All(l => l.Id != testListId) && MistakeDays.TimesWrong(ml, mlPlan.Words[41]) > 0,
    "lists: deleting a list keeps words that are still in another list");
var earlier = ml.MistakeLists.Single(l => l.Title == MistakeDays.EarlierTitle);
MistakeDays.DeleteList(ml, earlier.Id);
Check(MistakeDays.TimesWrong(ml, mlPlan.Words[50]) == 0, "lists: deleting a list drops words found only there");
Check(MistakeDays.Schedule(ml, d1, d0, [mlPlan.Words[41].Id]) == 1 && ml.MistakeDay!.WordIds.Single() == mlPlan.Words[41].Id,
    "lists: a list can be scheduled as tomorrow's mistake day");
var orphan = new AppData();
SeedData.EnsureSeeded(orphan);
orphan.MistakeDay = new MistakeDay { Date = d0, WordIds = [Guid.NewGuid()] };
var orphanToday = CourseEngine.BuildToday(orphan, d0, out var orphanFresh);
CourseEngine.ApplySession(orphan, orphanToday.Select(w => (w, true)).ToList(), orphanFresh, d0.AddHours(9));
Check(orphanFresh.Count == 20 && orphan.Position.DayCompleted && orphan.MistakeDay is null, "mistakes: a mistake day whose words were deleted falls back to the course day");
// ---- session question kinds ----
Check(CourseEngine.Blank("Please show your boarding pass at the gate.", "boarding pass") == "Please show your _____ at the gate."
      && CourseEngine.Blank("Our flight had a two-hour delay.", "delay") == "Our flight had a two-hour _____."
      && CourseEngine.Blank("We checked in at three o'clock.", "check in") == "We _____ at three o'clock."
      && CourseEngine.Blank("Delays are common.", "delay") == "_____ are common.",
    "quiz: gap-fill blanks the word, allowing simple endings");
Check(CourseEngine.Blank("She went home early.", "go") is null && CourseEngine.Blank("", "go") is null && CourseEngine.Blank("A cart.", "car") is null,
    "quiz: no gap when the sentence does not contain the word");
var qd = new AppData();
SeedData.EnsureSeeded(qd);
var qWords = qd.Plans[0].Words.Take(8).ToList();
var typed = CourseEngine.BuildQuiz(qd, qWords, new Random(2), typing: true);
Check(typed.Select(q => q.Kind).Take(4).SequenceEqual([QuizKind.ChooseWord, QuizKind.ChooseMeaning, typed[2].Kind, QuizKind.Type])
      && typed[2].Kind is QuizKind.Cloze or QuizKind.ChooseWord, "quiz: with typing the kinds rotate word, meaning, gap-fill, typing");
Check(typed.Where(q => q.Kind == QuizKind.Type).All(q => q.Options.Count == 0 && q.Prompt == q.Word.Meaning && q.Correct == q.Word.Text)
      && typed.Where(q => q.Kind == QuizKind.Cloze).All(q => q.Prompt.Contains("_____") && q.Options.Contains(q.Word.Text) && q.Options.Count == 4),
    "quiz: typing has no options; gap-fill shows the sentence and offers 4 words");
Check(CourseEngine.BuildQuiz(qd, qWords, new Random(2)).All(q => q.Kind is QuizKind.ChooseWord or QuizKind.ChooseMeaning),
    "quiz: without typing only the two choice kinds are used");

// ---- quick rating: learned / not remembered ----
var lr = new AppData();
SeedData.EnsureSeeded(lr);
var lrDay = CourseEngine.BuildToday(lr, d0, out _);
var learnedWord = lrDay[0];
MistakeDays.Record(lr, [learnedWord], d0, "Bài kiểm tra 08:00 · x");
CourseEngine.MarkLearned(lr, learnedWord, d0.AddHours(9));
var afterLearned = CourseEngine.BuildToday(lr, d0, out var lrFresh);
Check(!afterLearned.Contains(learnedWord) && lrFresh.Count == 19 && MistakeDays.TimesWrong(lr, learnedWord) == 0 && lr.MistakeLists.Count == 0,
    "learned: the word leaves today's words and the mistake lists");
ReviewScheduler.Rate(lr, learnedWord, false, d0.AddHours(10));
CourseEngine.BuildToday(lr, d0.AddDays(1), out _);
Check(!CourseEngine.BuildToday(lr, d0.AddDays(1), out _).Contains(learnedWord), "learned: never comes back as a review");
CourseEngine.UnmarkLearned(learnedWord);
Check(CourseEngine.BuildToday(lr, d0.AddDays(1), out _).Contains(learnedWord), "learned: undo brings it back");
var forgotten = lrDay[1];
CourseEngine.MarkNotRemembered(lr, forgotten, d0.AddHours(11));
Check(ReviewScheduler.IsWrongToday(forgotten, d0) && forgotten.Review!.Due == d0.AddDays(1)
      && lr.MistakeLists.Single().Title == MistakeDays.SessionTitle(d0) && lr.MistakeLists.Single().WordIds.Contains(forgotten.Id),
    "not remembered: review tomorrow, shown first today, kept in today's mistake list");
foreach (var w in CourseEngine.DayWords(lr)) CourseEngine.MarkLearned(lr, w, d0.AddHours(12));
Check(CourseEngine.Advance(lr, d0) && lr.Position.Day == 2, "learned: a day with every word learned moves on right away");
CourseEngine.UnmarkLearned(learnedWord);
Check(!CourseEngine.Advance(lr, d0) && lr.Position.Day == 2, "learned: unmarking a word of an earlier day does not move the course back");

// ---- Từ của tôi (Ctrl+Alt+V) ----
Check(QuickAdd.Normalize("  “Serendipity.” ") == "serendipity" && QuickAdd.Normalize("Look  Up\n") == "look up" && QuickAdd.Normalize("NASA") == "NASA"
      && QuickAdd.Normalize("don’t") == "don't",
    "my words: copied text is cleaned (spaces, quotes, punctuation, case)");
Check(QuickAdd.Normalize("") is null && QuickAdd.Normalize("12345") is null && QuickAdd.Normalize("a b c d e f g") is null
      && QuickAdd.Normalize(new string('x', 70)) is null && QuickAdd.Normalize("https://example.com/page") is null,
    "my words: sentences, numbers and links are refused");
var mw = new AppData();
SeedData.EnsureSeeded(mw);
var existing = mw.Plans[1].Words[5];
var dupe = QuickAdd.Add(mw, existing.Text.ToUpperInvariant() + ".", d0);
Check(!dupe.Added && dupe.ExistingPlan == mw.Plans[1] && dupe.ExistingWord == existing && mw.Inbox.Count == 0,
    "my words: a word already in the library is not added (says which plan)");
Check(QuickAdd.Add(mw, "serendipity", d0).Added && QuickAdd.Add(mw, "Serendipity", d0) is { Added: false, AlreadyWaiting: true }
      && QuickAdd.Add(mw, "jaywalk", d0).Added && QuickAdd.Add(mw, "lol 123 http://x", d0).Invalid && mw.Inbox.Count == 2,
    "my words: new words wait once; repeats and junk are refused");
var myPrompt = QuickAdd.BuildPrompt(mw.Inbox.Select(i => i.Text).ToList(), d0);
Check(myPrompt.Contains("serendipity, jaywalk") && myPrompt.Contains("# Tên bộ từ: Từ của tôi") && myPrompt.Contains("| STT | Từ |"),
    "my words: the prompt lists the waiting words and the form");
var answer = $"""
    ```markdown
    # Tên bộ từ: Từ của tôi

    ## Ngày 1 — Từ thêm 06/10

    | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
    |---|---|---|---|---|---|
    | 1 | serendipity | /ˌserənˈdɪpəti/ | n | sự tình cờ may mắn | Meeting her was pure serendipity. |
    | 2 | jaywalk | /ˈdʒeɪwɔːk/ | v | băng qua đường ẩu | Don't jaywalk on this busy road. |
    | 3 | {existing.Text} | /x/ | n | đã có | Already in the library. |
    ```
    """;
var imported = QuickAdd.Import(mw, answer, d0.AddHours(9));
var myPlan = mw.Plans.Single(p => p.Name == QuickAdd.PlanName);
Check(imported.Added.Count == 2 && imported.SkippedExisting.Single() == existing.Text && imported.Day == 1 && mw.Inbox.Count == 0
      && myPlan.Words.Count == 2 && !mw.Course.Contains(myPlan.Id) && myPlan.DayTitle(1).StartsWith("Từ thêm"),
    "my words: import adds new words as a day of “Từ của tôi” (outside the course), skips existing ones, empties the list");
QuickAdd.Add(mw, "zugzwang", d0.AddDays(1));
var second = QuickAdd.Import(mw, answer.Replace("serendipity", "zugzwang").Replace("jaywalk", "flummox"), d0.AddDays(1));
Check(second.Day == 2 && second.Added.Select(w => w.Text).SequenceEqual(["zugzwang", "flummox"]) && myPlan.DayCount == 2 && mw.Inbox.Count == 0,
    "my words: a later import becomes the next day");
Check(QuickAdd.Import(mw, "xin chào", d0).Errors.Count > 0, "my words: an answer without the form is reported, nothing added");
MistakeDays.ApplyPractice(mw, [(imported.Added[0], true), (imported.Added[1], false)], d0.AddHours(10));
Check(imported.Added.All(w => w.IntroducedOn == d0) && imported.Added[1].Review!.Due == d0.AddDays(1)
      && mw.MistakeLists.Single().Title == MistakeDays.SessionTitle(d0),
    "my words: studying them starts reviews; a wrong one joins today's mistake list");

// ---- Ctrl+Alt+V window: save a word written by hand ----
Check(QuickAdd.Normalize("v2.8.0") is null && QuickAdd.Normalize("COVID-19") is null && QuickAdd.Normalize("ver_2") is null,
    "my words: text with digits (versions, codes) is never taken as a word");
var sw = new AppData();
SeedData.EnsureSeeded(sw);
QuickAdd.Add(sw, "serendipity", d0);
var saved1 = QuickAdd.SaveWord(sw, " Serendipity ", "ˌserənˈdɪpəti", "n", "sự tình cờ may mắn", "Pure serendipity.", d0.AddHours(9));
var swPlan = sw.Plans.Single(p => p.Name == QuickAdd.PlanName);
Check(saved1 is { Text: "serendipity", Phonetic: "/ˌserənˈdɪpəti/", Day: 1 } && swPlan.DayTitle(1) == QuickAdd.DayTitleFor(d0)
      && !sw.Course.Contains(swPlan.Id) && sw.Inbox.Count == 0,
    "my words: a saved word goes to today's day of “Từ của tôi” and leaves the waiting list");
var saved2 = QuickAdd.SaveWord(sw, "flummox", "", "v", "làm bối rối", "", d0.AddHours(15));
var saved3 = QuickAdd.SaveWord(sw, "zugzwang", "", "n", "thế bắt buộc phải đi", "", d0.AddDays(1));
Check(saved2!.Day == 1 && saved3!.Day == 2 && swPlan.DayCount == 2, "my words: same date shares a day, the next date starts a new one");
Check(CourseEngine.BuildToday(sw, d0, out var swFresh).Contains(saved1!) && !swFresh.Contains(saved1!)
      && CourseEngine.BuildToday(sw, d0.AddDays(1), out _).Contains(saved3),
    "my words: a saved word shows up in today's reviews right away");
Check(QuickAdd.SaveWord(sw, "flummox", "", "", "x", "", d0) is null && QuickAdd.SaveWord(sw, sw.Plans[0].Words[0].Text, "", "", "x", "", d0) is null
      && QuickAdd.SaveWord(sw, "quokka", "", "", "  ", "", d0) is null && QuickAdd.SaveWord(sw, "v2.8.0", "", "", "x", "", d0) is null,
    "my words: no save for words already in the library, without a meaning, or not a word");

// ---- fields from a newer version survive a save by this one ----
var fwFolder = Path.Combine(Path.GetTempPath(), "voca-fw-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fwFolder);
File.WriteAllText(Path.Combine(fwFolder, "voca.json"), """
    { "DataVersion": 1, "FutureThing": { "a": 1 }, "Settings": { "RotationSeconds": 25, "FutureSetting": true },
      "Plans": [ { "Name": "P", "Words": [ { "Day": 1, "Text": "x", "Meaning": "y", "FutureWordFlag": "keep me" } ] } ] }
    """);
var fw = new Store(fwFolder);
fw.Update(d => d.Settings.RotationSeconds = 30);
var fwText = File.ReadAllText(Path.Combine(fwFolder, "voca.json"));
Check(fwText.Contains("\"FutureThing\"") && fwText.Contains("\"FutureSetting\": true") && fwText.Contains("\"FutureWordFlag\": \"keep me\"")
      && new Store(fwFolder).Data.Settings.RotationSeconds == 30,
    "data: fields this version does not know are kept when it saves (going back a version loses nothing)");
Directory.Delete(fwFolder, true);

// ---- self-update ----
Check(Updater.ParseTag("v2.7.0") == new Version(2, 7, 0, 0) && Updater.ParseTag("2.7") == new Version(2, 7, 0, 0) && Updater.ParseTag("latest") is null,
    "update: release tags v2.7.0 / 2.7 are read, others ignored");
Check(Updater.IsNewer(new Version(2, 10, 0), new Version(2, 9, 5)) && !Updater.IsNewer(new Version(2, 6, 1), new Version(2, 6, 1, 0))
      && !Updater.IsNewer(new Version(2, 6, 0), new Version(2, 6, 1)), "update: version comparison (2.10 > 2.9, 2.6.1 = 2.6.1.0)");
var shaLine = new string('a', 64);
Check(Updater.ParseSha256($"{shaLine.ToUpperInvariant()}  Voca.exe\n") == shaLine && Updater.ParseSha256("no hash") is null, "update: reads the .sha256 file");
const string releaseJson = """
    { "tag_name": "v2.7.0", "draft": false, "prerelease": false, "body": "Notes",
      "assets": [ { "name": "Voca.exe", "size": 1234, "browser_download_url": "https://x/Voca.exe" },
                  { "name": "Voca.exe.sha256", "size": 80, "browser_download_url": "https://x/Voca.exe.sha256" } ] }
    """;
var release = Updater.ParseRelease(releaseJson);
Check(release is { Tag: "v2.7.0", Size: 1234, ExeUrl: "https://x/Voca.exe", Sha256Url: "https://x/Voca.exe.sha256", Notes: "Notes" }
      && release.Version == new Version(2, 7, 0, 0), "update: GitHub release answer is read");
Check(Updater.ParseRelease(releaseJson.Replace("\"draft\": false", "\"draft\": true")) is null
      && Updater.ParseRelease(releaseJson.Replace("\"Voca.exe\", \"size\"", "\"Other.exe\", \"size\"")) is null,
    "update: drafts and releases without Voca.exe are ignored");
const string releasesJson = """
    [ { "tag_name": "v2.6.9", "draft": false, "prerelease": false, "published_at": "2026-10-01T08:00:00Z",
        "assets": [ { "name": "Voca.exe", "size": 10, "browser_download_url": "https://x/a" } ] },
      { "tag_name": "v2.8.0", "draft": false, "prerelease": false, "published_at": "2026-10-09T08:00:00Z", "body": "- Tab Từ sai\n- Sửa nút",
        "assets": [ { "name": "Voca.exe", "size": 10, "browser_download_url": "https://x/b" } ] },
      { "tag_name": "v2.9.0", "draft": true, "assets": [ { "name": "Voca.exe", "browser_download_url": "https://x/c" } ] },
      { "tag_name": "v2.7.5", "draft": false, "prerelease": false, "assets": [ { "name": "Notes.txt", "browser_download_url": "https://x/d" } ] },
      { "tag_name": "v2.7.0", "draft": false, "prerelease": false, "assets": [ { "name": "Voca.exe", "browser_download_url": "https://x/e" } ] } ]
    """;
var versions = Updater.ParseReleases(releasesJson);
Check(versions.Select(v => v.Version.ToString(3)).SequenceEqual(["2.8.0", "2.7.0", "2.6.9"]),
    "update: version list is newest first, without drafts or releases lacking Voca.exe");
Check(versions[0].PublishedAt?.Date == new DateTime(2026, 10, 9) && versions[0].Notes.Contains("Tab Từ sai") && versions[1].PublishedAt is null,
    "update: list shows release date and notes when GitHub has them");
Check(Updater.ParseReleases("[]").Count == 0 && Updater.ParseReleases("{}").Count == 0, "update: no releases → empty list");
var upDir = Path.Combine(Path.GetTempPath(), "voca-update-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(upDir);
var payload = Path.Combine(upDir, "payload.bin");
File.WriteAllBytes(payload, [1, 2, 3, 4]);
var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(payload))).ToLowerInvariant();
var info = new UpdateInfo(new Version(2, 7, 0, 0), "v2.7.0", "u", 4, "h", "");
bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
Check(!Throws(() => Updater.Verify(payload, info, payloadHash)) && Throws(() => Updater.Verify(payload, info, shaLine))
      && Throws(() => Updater.Verify(payload, info with { Size = 5 }, payloadHash)), "update: download kept only when size and SHA-256 match");
File.WriteAllText(Path.Combine(upDir, "Voca-2.7.0.exe"), "new");
File.WriteAllText(Path.Combine(upDir, "Voca-2.8.0.exe.part"), "partial");
var app = Path.Combine(upDir, "app");
Directory.CreateDirectory(app);
var exe = Path.Combine(app, "Voca.exe");
File.WriteAllText(exe, "running 2.6.1");
Updater.Swap(exe, Path.Combine(upDir, "Voca-2.7.0.exe"));
Check(File.ReadAllText(exe) == "new" && File.ReadAllText(exe + ".old") == "running 2.6.1", "update: swap puts the new exe in place and keeps the old one aside");
File.WriteAllText(exe, "2.7.0");
Check(Throws(() => { try { Updater.Swap(exe, Path.Combine(upDir, "missing.exe")); } catch (FileNotFoundException) { throw new InvalidOperationException(); } })
      && File.ReadAllText(exe) == "2.7.0", "update: a failed swap puts the running exe back");
Updater.CleanUp(exe, upDir);
Check(!File.Exists(exe + ".old") && Directory.GetFiles(upDir).Length == 0, "update: clean-up removes the old exe and leftover downloads");
Directory.Delete(upDir, true);
Check(!Updater.Enabled(@"D:\PersonalProject\voca\bin\Release\net8.0-windows\Voca.exe") && Updater.Enabled(@"D:\Apps\Voca\Voca.exe"),
    "update: builds run from bin\\ never update themselves");

// ---- subtitles from a video ----
var subs = new List<SubtitleLine>
{
    new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), " The committee postponed the meeting. "),
    new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7), "[Music]"),
    new(TimeSpan.FromMinutes(61).Add(TimeSpan.FromMilliseconds(5)), TimeSpan.FromMinutes(61.1), "Sarah said we're postponing it again, honestly."),
    new(TimeSpan.FromMinutes(62), TimeSpan.FromMinutes(62.1), "Meetings get postponed. Delays happen. Postpone nothing, Sarah!")
};
var cleanSubs = Subtitles.Clean(subs);
Check(cleanSubs.Count == 3 && cleanSubs[0].Text == "The committee postponed the meeting.", "subtitles: sound tags and empty lines are dropped, text trimmed");
var srt = Subtitles.ToSrt(cleanSubs);
Check(srt.StartsWith("1\r\n00:00:01,500 --> 00:00:04,000\r\nThe committee postponed the meeting.\r\n\r\n2\r\n01:01:00,005 --> ")
      && Subtitles.ReadSrt(srt).SequenceEqual(cleanSubs) && Subtitles.ReadSrt(srt.Replace("\r\n", "\n")).Count == 3,
    "subtitles: .srt timestamps (hours, milliseconds) and reading it back");
var vw = new AppData();
SeedData.EnsureSeeded(vw);
vw.Plans[0].Words.Add(new Word { Day = 1, Text = "delay", Meaning = "sự chậm trễ" });
var videoWords = Subtitles.NewWords(vw, cleanSubs);
var vwTexts = videoWords.Select(w => w.Text).ToList();
Check(vwTexts.Contains("postpone") && videoWords.Single(w => w.Text == "postpone").Count == 4
      && videoWords.Single(w => w.Text == "postpone").Example == "The committee postponed the meeting.",
    "subtitles: forms of a word are counted together under the base form heard, with its first line as example");
Check(!vwTexts.Contains("delays") && !vwTexts.Contains("delay"), "subtitles: a word already in the library is left out, whatever its form");
Check(!vwTexts.Contains("sarah") && !vwTexts.Contains("Sarah"), "subtitles: names are left out");
Check(!vwTexts.Contains("the") && !vwTexts.Contains("meetings") && !vwTexts.Contains("happen") && !vwTexts.Contains("we're"),
    "subtitles: very common words and contractions are left out");
Check(vwTexts.Contains("committee") && vwTexts.Contains("honestly") && vwTexts[0] == "postpone", "subtitles: other new words are kept, most frequent first");
Check(Subtitles.BaseForms("studied").Contains("study") && Subtitles.BaseForms("stopped").Contains("stop") && Subtitles.BaseForms("making").Contains("make")
      && Subtitles.BaseForms("boxes").Contains("box"), "subtitles: base forms of simple inflections");
var videoPrompt = Subtitles.BuildPrompt("Bài nói", videoWords.Take(3).ToList(), 2);
Check(videoPrompt.Contains("# Tên bộ từ: Bài nói") && videoPrompt.Contains("- postpone — \"The committee postponed the meeting.\"")
      && videoPrompt.Contains("chia thành 1 ngày, mỗi ngày 5 từ"), "subtitles: prompt lists the words with their line from the video");
var videoAnswer = """
    # Tên bộ từ: Bài nói
    ## Ngày 1 — Họp hành
    | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
    |---|---|---|---|---|---|
    | 1 | postpone | /pəˈspəʊn/ | v | hoãn lại | The committee postponed the meeting. |
    | 2 | delay | /dɪˈleɪ/ | n | sự chậm trễ | Delays happen. |
    """;
var (videoRead, videoSkipped) = Subtitles.ReadPlan(vw, videoAnswer, "Bài nói", 2, 20);
Check(videoRead.CanImport && videoRead.Plan!.Words.Single().Text == "postpone" && videoSkipped.SequenceEqual(["delay"]),
    "subtitles: words of the AI answer already in the library are skipped");

Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;
