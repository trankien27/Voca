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
var translated = PlanFormat.Read(PlanFormat.Example).Plan!;
var withTranslation = PlanFormat.Read("# Tên bộ từ: T\n## Ngày 1\n| 1 | delay /dɪˈleɪ/ | n | sự chậm trễ | Our flight had a delay. | Chuyến bay bị chậm trễ. |").Plan!;
var jsonTranslated = PlanFormat.Read("""{"name":"J","days":[{"day":1,"words":[{"word":"cat","meaning":"mèo","example":"A cat.","exampleMeaning":"Một con mèo."}]}]}""").Plan!;
Check(translated.Words[1].ExampleMeaning == "Chuyến bay của chúng tôi bị chậm trễ hai tiếng." && withTranslation.Words[0].ExampleMeaning == "Chuyến bay bị chậm trễ."
      && jsonTranslated.Words[0].ExampleMeaning == "Một con mèo." && rp.Words.All(w => w.ExampleMeaning == ""),
    "form: the 7th column (and JSON exampleMeaning) is the example's translation; 6-column forms still read");
var withMeaning = new Plan { Name = "T", Words = [new Word { Text = "delay", Meaning = "sự chậm trễ", Example = "A | delay.", ExampleMeaning = "Một sự | chậm trễ." }] };
var withMeaningBack = PlanFormat.Read(PlanFormat.ToMarkdown(withMeaning)).Plan!.Words[0];
Check(withMeaningBack.ExampleMeaning == "Một sự | chậm trễ." && withMeaningBack.Meaning == "sự chậm trễ" && withMeaningBack.Example == "A | delay."
      && new[] { prompt, Subtitles.BuildPrompt("V", [new VideoWord("x", 1, "x")], 20) }
          .All(p => p.Contains(PlanFormat.TableHeader) && p.Contains("Dịch ví dụ: bản dịch tiếng Việt") && !p.Contains("| Ví dụ |\n"))
      && QuickAdd.BuildPrompt([new InboxWord { Text = "x" }]).Contains("\"exampleMeaning\": bản dịch tiếng Việt"),
    "export → import keeps the translation (also for a word without phonetic); every AI prompt (topic, video, my words) asks for it");

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
var myPrompt = QuickAdd.BuildPrompt(mw.Inbox);
Check(myPrompt.Contains("1. serendipity" + Environment.NewLine) && myPrompt.Contains("2. jaywalk" + Environment.NewLine) && myPrompt.Contains("```json") && myPrompt.Contains("Trả về đúng 2 mục"),
    "my words: the prompt lists the waiting words in order and asks for a JSON block");
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

// ---- my words: dictionary-style prompt with the subtitle sentence, and a forgiving reader of the AI's answer ----
Check(QuickAdd.ContextOf("- I was tired. Then I postponed the trip!\nSee you.", "postponed") == "Then I postponed the trip!"
      && QuickAdd.ContextOf("We met. It was pure Serendipity.", "serendipity") == "It was pure Serendipity."
      && QuickAdd.ContextOf("No word here", "zebra") == "No word here" && QuickAdd.ContextOf(null, "x") == ""
      && QuickAdd.ContextOf(string.Join(" ", Enumerable.Repeat("blah", 80)) + " zebra " + string.Join(" ", Enumerable.Repeat("blah", 80)), "zebra") is var cut
      && cut.Length <= 202 && cut.Contains("zebra") && cut.StartsWith('…') && cut.EndsWith('…'),
    "my words: the context is the sentence of the subtitle line that holds the word, cut around it when long");
var cx = new AppData();
QuickAdd.Add(cx, "postponed", d0, "- I was tired. Then I postponed the trip!");
QuickAdd.Add(cx, "jaywalk", d0.AddMinutes(1));
QuickAdd.Add(cx, "Jaywalk", d0.AddMinutes(2), "Don't jaywalk here.");
QuickAdd.Add(cx, "postponed", d0.AddMinutes(3), "Another line with postponed.");
Check(cx.Inbox.Count == 2 && cx.Inbox[0].Context == "Then I postponed the trip!" && cx.Inbox[1].Context == "Don't jaywalk here.",
    "my words: a word saved from a subtitle keeps its sentence; saving it again only fills a missing one");
var dictPrompt = QuickAdd.BuildPrompt(cx.Inbox);
Check(dictPrompt.Contains("1. postponed — câu gặp từ: \"Then I postponed the trip!\"") && dictPrompt.Contains("2. jaywalk — câu gặp từ:")
      && new[] { "Cambridge", "Oxford Learner's", "Longman", "IPA", "Anh-Anh", "\"headword\"", "\"contextMeaning\"", "Không bịa", "\"meaning\": \"\"", "```json" }.All(dictPrompt.Contains),
    "my words: the prompt asks for Cambridge IPA, the most common dictionary sense (and the sense in the sentence), no guessing, one JSON block");
var manyWaiting = new AppData();
for (var i = 0; i < QuickAdd.MaxPromptWords + 5; i++) manyWaiting.Inbox.Add(new InboxWord { Text = "word" + (char)('a' + i % 26) + (char)('a' + i / 26) });
var manyPrompt = QuickAdd.BuildPrompt(manyWaiting.Inbox);
Check(manyPrompt.Contains($"\n{QuickAdd.MaxPromptWords}. ") && !manyPrompt.Contains($"\n{QuickAdd.MaxPromptWords + 1}. ") && manyPrompt.Contains($"đúng {QuickAdd.MaxPromptWords} mục"),
    "my words: one prompt holds at most MaxPromptWords words, the rest wait for the next");

const string entryA = """{"word": "postponed", "headword": "postpone", "ipa": "pəʊstˈpəʊn", "pos": "Verb", "meaning": "hoãn lại", "contextMeaning": "", "example": "They postponed the match.", "exampleMeaning": "Họ hoãn trận đấu.", "note": ""}""";
const string entryB = """{"word": "jaywalk", "headword": "", "ipa": "/ˈdʒeɪ.wɔːk/", "pos": "verb", "meaning": "băng qua đường ẩu", "contextMeaning": "", "example": "Don't jaywalk.", "exampleMeaning": "Đừng băng qua đường ẩu.", "note": ""}""";
var fenced = QuickAdd.ReadAnswer($"Chắc chắn rồi! Đây là kết quả:\n```json\n[\n  {entryA},\n  {entryB},\n]\n```\nHy vọng hữu ích [1].");
Check(fenced.Errors.Count == 0 && fenced.Words.Select(w => w.Asked).SequenceEqual(["postponed", "jaywalk"])
      && fenced.Words[0].Word is { Text: "postpone", Phonetic: "/pəʊstˈpəʊn/", PartOfSpeech: "v", Meaning: "hoãn lại", ExampleMeaning: "Họ hoãn trận đấu." },
    "my words answer: read inside ```json after a greeting, trailing comma and text after it ignored; headword saved, IPA in /../, pos shortened");
var split = QuickAdd.ReadAnswer($"Phần 1:\n```json\n[{entryA}]\n```\nPhần 2:\n```json\n[{entryB}]\n```");
var bare = QuickAdd.ReadAnswer($"[ // two words\n{entryA}, {entryB}]");
var wrapped = QuickAdd.ReadAnswer($$"""{"words": [{{entryA}}, {{entryB}}]}""");
var cutOff = QuickAdd.ReadAnswer($"```json\n[\n{entryA},\n{entryB[..40]}");
var curly = QuickAdd.ReadAnswer("[{“word”: “jaywalk”, “pos”: “noun, verb”, “meaning”: [“băng qua đường ẩu”, “đi ẩu”]}]");
Check(split.Words.Count == 2 && bare.Words.Count == 2 && wrapped.Words.Count == 2 && cutOff.Words.Select(w => w.Asked).SequenceEqual(["postponed"])
      && curly.Words.Single().Word is { PartOfSpeech: "n, v", Meaning: "băng qua đường ẩu, đi ẩu" },
    "my words answer: several blocks, no fence with a comment, {\"words\": [...]}, an answer cut short, curly quotes and lists all read");
Check(QuickAdd.ShortPartOfSpeech("Phrasal verb") == "phr v" && QuickAdd.ShortPartOfSpeech("noun (countable)") == "n" && QuickAdd.ShortPartOfSpeech("adj.") == "adj"
      && QuickAdd.ShortPartOfSpeech("danh từ / động từ") == "n, v",
    "my words answer: parts of speech in words (English or Vietnamese) become Voca's short forms");
Check(QuickAdd.ReadAnswer("Xin lỗi, tôi không thể giúp.").Errors.Count == 1 && QuickAdd.ReadAnswer(dictPrompt).Errors.Count == 1
      && QuickAdd.ReadAnswer("  ").Errors.Count == 1,
    "my words answer: text without entries (or the prompt pasted back) is refused");

var ai = new AppData();
SeedData.EnsureSeeded(ai);
var known = ai.Plans[0].Words[0].Text;
foreach (var waitingText in new[] { "postponed", "jaywalk", "flummoxed", "grnadma" }) QuickAdd.Add(ai, waitingText, d0);
ai.Inbox.Add(new InboxWord { Text = known, AddedAt = d0 }); // waited before the library got it
var aiAnswer = $$"""
    Here you go:
    ```json
    [
      {{entryA}},
      {"word": "Jaywalk", "ipa": "ˈdʒeɪwɔːk", "pos": "v", "meaning": "băng qua đường ẩu", "contextMeaning": "đi bộ sai luật", "example": "Don't jaywalk.", "exampleMeaning": "Đừng băng qua đường ẩu."},
      {"word": "grnadma", "headword": "", "ipa": "", "pos": "", "meaning": "", "contextMeaning": "", "example": "", "exampleMeaning": "", "note": "viết sai, đúng là grandma"},
      {"word": "{{known}}", "ipa": "/x/", "pos": "n", "meaning": "đã có", "example": "x"}
    ]
    ```
    """;
var aiImport = QuickAdd.Import(ai, aiAnswer, d0.AddHours(9));
var aiPlan = ai.Plans.Single(p => p.Name == QuickAdd.PlanName);
Check(aiImport.Errors.Count == 0 && aiImport.Added.Select(w => w.Text).SequenceEqual(["postpone", "jaywalk"]) && aiPlan.Words.Count == 2
      && aiImport.Added[1] is { Phonetic: "/ˈdʒeɪwɔːk/", Meaning: "băng qua đường ẩu; trong câu: đi bộ sai luật", Day: 1 } && aiImport.Added.All(w => w.Review is not null)
      && aiImport.SkippedExisting.SequenceEqual([known]),
    "my words import: dictionary form saved, the sense in the sentence added to the meaning, words in the library skipped");
Check(ai.Inbox.Select(i => i.Text).SequenceEqual(["flummoxed", "grnadma"])
      && aiImport.StillWaiting.Contains("grnadma — viết sai, đúng là grandma") && aiImport.StillWaiting.Contains("flummoxed — AI chưa trả lời"),
    "my words import: a word the AI could not find in the dictionaries, or left out, keeps waiting with the reason");

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

// ---- playing a video with its subtitles ----
Check(Subtitles.LineAt(cleanSubs, TimeSpan.FromSeconds(2)) == cleanSubs[0] && Subtitles.LineAt(cleanSubs, TimeSpan.FromSeconds(5)) is null
      && Subtitles.LineAt(cleanSubs, TimeSpan.FromMinutes(61.05)) == cleanSubs[1] && Subtitles.LineAt(cleanSubs, TimeSpan.Zero) is null
      && Subtitles.LineAt(cleanSubs, TimeSpan.FromMinutes(62.1)) is null,
    "player: the line on screen at a time, none between lines or after the end");
var studying = vw.Plans[0].Words[0];
var learnedAlready = vw.Plans[0].Words[1];
learnedAlready.Learned = true;
vw.Inbox.Add(new InboxWord { Text = "committee", AddedAt = d0 });
var marker = new WordMarker(vw, videoWords);
var marked = Subtitles.Mark($"Delays, the {studying.Text}s and {learnedAlready.Text}: honestly the committee postponed it.", marker);
Check(string.Concat(marked.Select(p => p.Text)) == $"Delays, the {studying.Text}s and {learnedAlready.Text}: honestly the committee postponed it.",
    "player: marking keeps the line's text, spaces and punctuation");
var marks = marked.Where(p => p.Mark != WordMark.None).ToDictionary(p => p.Text.ToLowerInvariant(), p => p.Mark);
Check(marks["delays"] == WordMark.Learning && marks[studying.Text.ToLowerInvariant() + "s"] == WordMark.Learning
      && marked.Single(p => p.Text == studying.Text + "s").Word == studying,
    "player: library words being learned are marked, in any simple form, with the word for its meaning");
Check(!marks.ContainsKey(learnedAlready.Text.ToLowerInvariant()) && !marks.ContainsKey("the") && !marks.ContainsKey("it"),
    "player: learned and common words stay plain");
Check(marks["honestly"] == WordMark.New && marks["postponed"] == WordMark.New && marks["committee"] == WordMark.Waiting,
    "player: the video's new words are marked (any form); words saved to “Từ của tôi” show as waiting");
SubtitleLine Cue(double from, double to, string text) => new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);
var looped = new List<SubtitleLine> { Cue(0, 1, "Where are you?"), Cue(1, 2, "Where are you?"), Cue(2, 3, "Sasuke!") };
looped.AddRange(Enumerable.Range(0, 6).Select(i => Cue(3 + i, 4 + i, i % 2 == 0 ? "I'm going to kill you." : "i'm going to KILL you")));
looped.Add(Cue(10, 11, "Naruto."));
var unlooped = Subtitles.Clean(looped);
Check(unlooped.Select(l => l.Text).SequenceEqual(["Where are you?", "Where are you?", "Sasuke!", "Naruto."])
      && Subtitles.DropRepeats(looped.Take(6).ToList()).Count == 6,
    "subtitles: a line repeated 4+ times in a row (Whisper looping over music) is dropped; a short repeat stays");
Check(Subtitles.Clean([Cue(0, 1, "- Give us a hint."), Cue(1, 2, "♪"), Cue(2, 3, "– No hint?"), Cue(3, 4, "Well - maybe.")]).Select(l => l.Text)
          .SequenceEqual(["Give us a hint.", "No hint?", "Well - maybe."]),
    "subtitles: the dash before a new speaker and music notes are removed, a dash inside a line stays");
var talk = new List<SubtitleLine> { Cue(0, 2, "So we went"), Cue(2.2, 4, "to the market."), Cue(7, 9, "It was closed!"), Cue(9.1, 10, "Again.") };
var paragraphs = Subtitles.Paragraphs(talk);
Check(paragraphs.Count == 2 && paragraphs[0].SequenceEqual(talk.Take(2)) && paragraphs[1].SequenceEqual(talk.Skip(2)),
    "reading: lines join into paragraphs, a new one after a pause of 2 s or more");
var nonstop = Enumerable.Range(0, 20).Select(i => Cue(i, i + 1, i == 9 ? "end of a sentence." : "and then")).ToList();
var long1 = Subtitles.Paragraphs(nonstop);
var noStop = Subtitles.Paragraphs(Enumerable.Range(0, 20).Select(i => Cue(i, i + 1, "and then")).ToList());
Check(long1[0].Count == 10 && long1.Sum(p => p.Count) == 20 && noStop.Select(p => p.Count).SequenceEqual([16, 4]) && Subtitles.Paragraphs([]).Count == 0,
    "reading: a long paragraph breaks at a sentence end after 8 lines, and after 16 lines at the latest");

// ---------- continue watching ----------
var wp = new WatchHistory();
string[] episodes = [@"D:\Film\Show_E1.mkv", @"D:\Film\Show_E2.mkv", @"D:\Film\Show_E10.mkv", @"D:\Film\Show_E2.srt", @"D:\Film\notes.txt"];
IEnumerable<string> Folder(string _) => episodes.Reverse();
bool OnDisk(string path) => episodes.Contains(path, StringComparer.OrdinalIgnoreCase);
var minutes24 = TimeSpan.FromMinutes(24);
Check(WatchProgress.Continue(wp, OnDisk, Folder) is null && WatchProgress.ResumeAt(wp, episodes[0]) == TimeSpan.Zero,
    "watch: nothing to continue before any video is played");
WatchProgress.Save(wp, episodes[0], TimeSpan.FromSeconds(754.5), minutes24, d0);
Check(WatchProgress.ResumeAt(wp, @"d:\film\SHOW_E1.mkv") == TimeSpan.FromSeconds(754.5)
      && WatchProgress.Continue(wp, OnDisk, Folder) == new ContinueWatching(episodes[0], TimeSpan.FromSeconds(754.5), false),
    "watch: a video resumes at the exact second it was left off (path in any case)");
WatchProgress.Save(wp, episodes[0], minutes24 - TimeSpan.FromSeconds(10), minutes24, d0);
Check(WatchProgress.ResumeAt(wp, episodes[0]) == TimeSpan.Zero && wp.Videos.Count == 1
      && WatchProgress.Continue(wp, OnDisk, Folder) == new ContinueWatching(episodes[1], TimeSpan.Zero, true),
    "watch: watched to the end (last 30 s) → next time from the start, and “Xem tiếp” opens the next episode");
WatchProgress.Save(wp, episodes[1], minutes24, minutes24, d0);
Check(WatchProgress.Continue(wp, OnDisk, Folder)?.Video == episodes[2] && WatchProgress.NextEpisode(episodes[2], Folder) is null,
    "watch: episodes follow natural order (E2 before E10), only media files; none after the last");
WatchProgress.Save(wp, @"D:\Film\Gone.mkv", TimeSpan.FromMinutes(3), minutes24, d0);
Check(WatchProgress.Continue(wp, OnDisk, Folder)?.Video == episodes[2] && wp.Videos[0].Video == @"D:\Film\Gone.mkv",
    "watch: a video no longer on disk is skipped for the one watched before it");
WatchProgress.Save(wp, episodes[1], TimeSpan.FromSeconds(3), minutes24, d0);
Check(WatchProgress.ResumeAt(wp, episodes[1]) == TimeSpan.Zero && WatchProgress.Continue(wp, OnDisk, Folder)?.Start == TimeSpan.FromSeconds(3),
    "watch: replaying a finished video starts over; barely begun (under 5 s) plays from the start");
var watchDir = Directory.CreateTempSubdirectory("voca-watch").FullName;
Check(WatchProgress.Load(watchDir).Videos.Count == 0 && WatchProgress.Record(watchDir, episodes[0], TimeSpan.FromSeconds(42), minutes24, d0)
      && WatchProgress.Record(watchDir, episodes[1], TimeSpan.FromSeconds(7), minutes24, d0)
      && WatchProgress.Load(watchDir).Videos.Select(v => v.Video).SequenceEqual([episodes[1], episodes[0]])
      && WatchProgress.ResumeAt(WatchProgress.Load(watchDir), episodes[0]) == TimeSpan.FromSeconds(42)
      && !File.Exists(Path.Combine(watchDir, WatchProgress.FileName + ".tmp")),
    "watch: positions are kept in watch.json, newest first, with no temp file left behind");
File.WriteAllText(Path.Combine(watchDir, WatchProgress.FileName), "{ broken");
Check(WatchProgress.Load(watchDir).Videos.Count == 0, "watch: an unreadable watch.json starts an empty history instead of failing");
Directory.Delete(watchDir, true);

// ---- one word set per video, no word offered twice ----
var ws = new AppData();
SeedData.EnsureSeeded(ws);
var videoA = Path.Combine(Path.GetTempPath(), "voca-a.mp4");
var videoB = Path.Combine(Path.GetTempPath(), "voca-b.mp4");
List<SubtitleLine> Said(string text) => [new(TimeSpan.Zero, TimeSpan.FromSeconds(3), text)];
var linesA = Said("The committee postponed the annual meeting. Honestly, the turbine stalled.");
var linesB = Said("They postpone everything; the committee agreed, honestly. A turbine and a zeppelin.");
var wordsA = Subtitles.NewWords(ws, linesA);
var pickA = wordsA.Where(w => w.Text is "postponed" or "committee" or "turbine").ToList();
var (savedA, takenA) = Subtitles.SaveWordSet(ws, videoA, "Video A", pickA, 20, d0);
var setA = savedA!;
Check(setA is { Pending: true } && setA.Words.Count == 3 && takenA.Count == 0 && ws.VideoWordSets.Single() == setA
      && Subtitles.PendingSetFor(ws, videoA) == setA && Subtitles.PendingSetFor(ws, videoB) is null,
    "word sets: the words picked from a video are saved as its own pending set");
var wordsB = Subtitles.NewWords(ws, linesB).Select(w => w.Text).ToList();
Check(!wordsB.Contains("postpone") && !wordsB.Contains("committee") && !wordsB.Contains("turbine") && wordsB.Contains("zeppelin") && wordsB.Contains("honestly"),
    "word sets: another video does not offer words of a pending set again, in any form (postponed → postpone)");
Check(Subtitles.NewWords(ws, linesA, setA).Select(w => w.Text).Intersect(["postponed", "committee", "turbine"]).Count() == 3,
    "word sets: the video's own pending set is offered again when it is reopened");
ws.Inbox.Add(new InboxWord { Text = "zeppelin", AddedAt = d0 });
var (setB, takenB) = Subtitles.SaveWordSet(ws, videoB, "Video B", [new VideoWord("zeppelin", 1, ""), new VideoWord("honestly", 1, ""), new VideoWord("turbine", 1, "")], 20, d0);
Check(setB is not null && setB != setA && setB.Words.Single().Text == "honestly" && takenB.Order().SequenceEqual(["turbine", "zeppelin"]),
    "word sets: saving drops words taken meanwhile (Từ của tôi, another video's set) and names them");
var (againA, _) = Subtitles.SaveWordSet(ws, videoA, "Video A", pickA.Take(2).ToList(), 20, d0.AddHours(1));
Check(againA == setA && setA.Words.Count == 2 && ws.VideoWordSets.Count(s => s.Pending) == 2,
    "word sets: saving a video again replaces its pending set (one per video)");
var answerA = """
    # Tên bộ từ: Video A
    ## Ngày 1 — Họp
    | STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
    |---|---|---|---|---|---|
    | 1 | postpone | /pəˈspəʊn/ | v | hoãn lại | They postponed it. |
    | 2 | committee | /kəˈmɪti/ | n | ủy ban | The committee met. |
    | 3 | honestly | /ˈɒnɪstli/ | adv | thật lòng | Honestly, no. |
    """;
var (readA, skippedA) = Subtitles.ReadPlan(ws, answerA, "Video A", 2, 20, setA);
Check(readA.CanImport && readA.Plan!.Words.Select(w => w.Text).SequenceEqual(["postpone", "committee"]) && skippedA.SequenceEqual(["honestly"]),
    "word sets: importing skips words that belong to another video's pending set");
ws.Plans.Add(readA.Plan!);
Subtitles.MarkImported(setA, readA.Plan!, d0.AddHours(2));
Check(!setA.Pending && setA.PlanId == readA.Plan!.Id && Subtitles.PendingSetFor(ws, videoA) is null
      && Subtitles.ImportedSetsFor(ws, videoA).Single() == setA && !Subtitles.NewWords(ws, linesB).Any(w => w.Text is "postpone" or "committee"),
    "word sets: an imported set points to its plan, and its words stay taken through the library");
Check(Subtitles.DropPendingSet(ws, setB) && !Subtitles.DropPendingSet(ws, setA) && Subtitles.NewWords(ws, linesB).Any(w => w.Text == "honestly"),
    "word sets: dropping a pending set frees its words; an imported set cannot be dropped");

// ---------- all words ----------
var aw = new AppData();
SeedData.EnsureSeeded(aw);
var awPlan = aw.Plans[0];
var (awRight, awWrong, awMarked, awLong) = (awPlan.Words[0], awPlan.Words[1], awPlan.Words[2], awPlan.Words[3]);
ReviewScheduler.Rate(aw, awRight, true, d0.AddDays(-1));
ReviewScheduler.Rate(aw, awWrong, false, d0.AddDays(-1));
awMarked.Learned = true; awMarked.LearnedOn = d0;
awLong.Review = new ReviewState { Reps = 4, IntervalDays = 30, Due = d0.AddDays(30), LastReviewed = d0.AddDays(-2) };
Check(AllWords.Find(aw, d0).Count == aw.Plans.Sum(p => p.Words.Count), "all words: every word of every plan is listed");
Check(AllWords.StatusOf(awRight, d0) == WordStatus.Learning && AllWords.StatusOf(awWrong, d0) == WordStatus.Due
      && AllWords.StatusOf(awMarked, d0) == WordStatus.Learned && AllWords.StatusOf(awLong, d0) == WordStatus.Mastered
      && AllWords.StatusOf(awPlan.Words[4], d0) == WordStatus.New,
    "all words: status is learned / mastered / due / learning / new");
Check(AllWords.Find(aw, d0, filter: WordFilter.Seen).Select(e => e.Word).ToHashSet().SetEquals([awRight, awWrong, awMarked, awLong])
      && AllWords.Find(aw, d0, filter: WordFilter.Known).Count == 2 && AllWords.Find(aw, d0, filter: WordFilter.Due).Single().Word == awWrong
      && AllWords.Find(aw, d0, filter: WordFilter.Learning).Count == 2,
    "all words: filters seen, known (marked or mastered), due, learning (incl. due)");
var awSchool = awPlan.Words.First(w => w.Meaning.Contains("trường học"));
Check(AllWords.Find(aw, d0, "truong hoc").Any(e => e.Word == awSchool) && AllWords.Find(aw, d0, "SCHOOL").Any(e => e.Word == awSchool)
      && AllWords.Matches(new Word { Text = "x", Meaning = "đồng phục" }, "dong phuc"),
    "all words: search by word or meaning ignores case and accents (incl. đ)");
Check(AllWords.Find(aw, d0, planId: aw.Plans[1].Id).All(e => e.Plan == aw.Plans[1])
      && AllWords.Find(aw, d0, sort: WordSort.Recent)[0].Word == awMarked
      && AllWords.Find(aw, d0, sort: WordSort.MostForgotten)[0].Word == awWrong
      && AllWords.Find(aw, d0)[0].Plan == CourseEngine.CoursePlans(aw).First(),
    "all words: plan filter; order by course, last seen, most forgotten");
var awCopy = new Word { Day = 2, Text = "  " + awSchool.Text.ToUpperInvariant() + ". " };
var awOther = aw.Plans[1].Words[5];
var awOtherCopy = new Word { Day = 1, Text = awOther.Text };
aw.Plans[1].Words.Add(awCopy);
aw.Plans[2].Words.Add(awOtherCopy);
var awDuplicates = AllWords.Find(aw, d0, filter: WordFilter.Duplicate);
Check(AllWords.DuplicateKeys(aw).Count == 2 && awDuplicates.Select(e => e.Word).ToHashSet().SetEquals([awSchool, awCopy, awOther, awOtherCopy])
      && Math.Abs(awDuplicates.FindIndex(e => e.Word == awSchool) - awDuplicates.FindIndex(e => e.Word == awCopy)) == 1,
    "all words: duplicates ignore case, spaces and end punctuation, across plans; copies listed side by side");
Check(AllWords.Find(aw, d0, filter: WordFilter.Duplicate, planId: aw.Plans[1].Id).Select(e => e.Word).ToHashSet().SetEquals([awCopy, awOther])
      && AllWords.DuplicateKeys(new AppData { Plans = [new Plan { Words = [new Word { Text = "a" }, new Word { Text = "b" }] }] }).Count == 0,
    "all words: a copy in another plan still counts when filtering one plan; no duplicates → none found");

// ---------- phone page ----------
var phFolder = Path.Combine(Path.GetTempPath(), "voca-phone-" + Guid.NewGuid().ToString("N"));
var phStore = new Store(phFolder);
phStore.Update(SeedData.EnsureSeeded);
var phNow = d0.AddHours(9);
var phApi = new PhoneApi(phStore, () => phNow);
PhoneRequest PhReq(string method, string path, string body = "", string? cookie = null, Dictionary<string, string>? query = null, bool header = true)
{
    var headers = new Dictionary<string, string>();
    if (cookie is not null) headers["cookie"] = cookie;
    if (header) headers["x-voca"] = "1";
    return new PhoneRequest(method, path, query ?? [], headers, body);
}
System.Text.Json.JsonElement PhJson(PhoneResponse r) => System.Text.Json.JsonDocument.Parse(r.Body).RootElement;
Check(phApi.Handle(PhReq("GET", "/api/today")).Status == 401 && phApi.Handle(PhReq("GET", "/", query: new() { ["k"] = "" })).Status == 401,
    "phone: refused while no key is set (also with an empty key)");
phStore.Update(d => d.Settings.PhoneKey = PhoneAccess.NewKey());
var phKey = phStore.Data.Settings.PhoneKey;
var phCookie = $"{PhoneApi.CookieName}={phKey}";
var phLogin = phApi.Handle(PhReq("GET", "/", query: new() { ["k"] = phKey }));
Check(phKey.Length == 24 && phKey.All(char.IsLetterOrDigit) && PhoneAccess.NewKey() != phKey, "phone: key is 24 random letters/digits");
Check(phLogin.Status == 302 && phLogin.Headers!["Location"] == "/" && phLogin.Headers["Set-Cookie"].StartsWith(phCookie + ";"),
    "phone: the QR link stores the key in a cookie and drops it from the address");
Check(phApi.Handle(PhReq("GET", "/", query: new() { ["k"] = phKey + "x" })).Status == 401
      && phApi.Handle(PhReq("GET", "/api/today", cookie: $"{PhoneApi.CookieName}=wrong")).Status == 401
      && phApi.Handle(PhReq("GET", "/", cookie: "other=1")).Status == 401,
    "phone: a wrong key or no cookie is refused");
var phPage = phApi.Handle(PhReq("GET", "/", cookie: phCookie));
Check(phPage.Status == 200 && System.Text.Encoding.UTF8.GetString(phPage.Body).Contains("/api/today"), "phone: the page is served with the cookie");
Check(phApi.Handle(PhReq("POST", "/api/rate", "{}", phCookie, header: false)).Status == 403, "phone: changes need the X-Voca header (no cross-site requests)");
var phToday = PhJson(phApi.Handle(PhReq("GET", "/api/today", cookie: phCookie)));
var phWords = phToday.GetProperty("words");
Check(phWords.GetArrayLength() == 20 && phWords[0].GetProperty("kind").GetString() == "new"
      && phToday.GetProperty("course").GetProperty("title").GetString()!.EndsWith("Ngày 1/7")
      && phToday.GetProperty("course").GetProperty("dots").GetArrayLength() == 7,
    "phone: today = the 20 new words of day 1, with the course position");
var phFirst = phWords[0].GetProperty("id").GetGuid();
var phLearned = PhJson(phApi.Handle(PhReq("POST", "/api/rate", $$"""{"id":"{{phFirst}}","learned":true}""", phCookie)));
var phWord = phStore.Data.Plans.SelectMany(p => p.Words).First(w => w.Id == phFirst);
Check(phWord.Learned && phLearned.GetProperty("undo").GetBoolean()
      && new Store(phFolder).Data.Plans.SelectMany(p => p.Words).First(w => w.Id == phFirst).Learned,
    "phone: Đã thuộc marks the word and saves it");
PhJson(phApi.Handle(PhReq("POST", "/api/undo", "{}", phCookie)));
Check(!phWord.Learned, "phone: Hoàn tác takes it back");
var phSecond = phWords[1].GetProperty("id").GetGuid();
phApi.Handle(PhReq("POST", "/api/rate", $$"""{"id":"{{phSecond}}","learned":false}""", phCookie));
var phForgot = phStore.Data.Plans.SelectMany(p => p.Words).First(w => w.Id == phSecond);
Check(ReviewScheduler.IsWrongToday(phForgot, d0) && phStore.Data.Mistakes.Any(m => m.WordId == phSecond)
      && PhJson(phApi.Handle(PhReq("GET", "/api/today", cookie: phCookie))).GetProperty("words")[0].GetProperty("id").GetGuid() == phSecond,
    "phone: Chưa nhớ counts as wrong and puts the word first");
var phDay1 = CourseEngine.DayWords(phStore.Data).Select(w => $"\"{w.Id}\"");
var phMoved = PhJson(phApi.Handle(PhReq("POST", "/api/learned", $$"""{"ids":[{{string.Join(",", phDay1)}}]}""", phCookie)));
Check(phStore.Data.Position.Day == 2 && phMoved.GetProperty("message").GetString()!.Contains("Sang Ngày 2/7"),
    "phone: learning every word of the day moves the course on, and says so");
var phSession = PhJson(phApi.Handle(PhReq("GET", "/api/session", cookie: phCookie)));
var phQuiz = phSession.GetProperty("quiz");
Check(phSession.GetProperty("learn").GetArrayLength() == 20 && phQuiz.GetArrayLength() == 20
      && phQuiz.EnumerateArray().All(q => q.GetProperty("kind").GetString() == "Type" || q.GetProperty("options").EnumerateArray().Any(o => o.GetString() == q.GetProperty("correct").GetString())),
    "phone: session = day 2's new words to flip, then a quiz with the answer among the options");
var phAnswers = phQuiz.EnumerateArray().Select((q, i) => $$"""{"id":"{{q.GetProperty("id").GetGuid()}}","correct":{{(i == 0 ? "false" : "true")}}}""");
var phNewIds = phSession.GetProperty("newIds").EnumerateArray().Select(e => $"\"{e.GetGuid()}\"");
var phResult = PhJson(phApi.Handle(PhReq("POST", "/api/session", $$"""{"answers":[{{string.Join(",", phAnswers)}}],"newIds":[{{string.Join(",", phNewIds)}}]}""", phCookie)));
Check(phResult.GetProperty("score").GetString() == "19/20" && phStore.Data.Position.DayCompleted
      && phStore.Data.StudyLog[ReviewScheduler.Key(d0)].SessionDone && phResult.GetProperty("results").GetArrayLength() == 20,
    "phone: finishing the session records answers and marks today's session done");
Check(PhJson(phApi.Handle(PhReq("POST", "/api/check", """{"typed":" Take After. ","answer":"take after"}""", phCookie))).GetProperty("correct").GetBoolean()
      && !PhJson(phApi.Handle(PhReq("POST", "/api/check", """{"typed":"take","answer":"take after"}""", phCookie))).GetProperty("correct").GetBoolean(),
    "phone: typed answers are checked like on the computer");
var phFound = PhJson(phApi.Handle(PhReq("GET", "/api/words", cookie: phCookie, query: new() { ["q"] = "truong hoc" })));
var phAll = PhJson(phApi.Handle(PhReq("GET", "/api/words", cookie: phCookie, query: new() { ["skip"] = "50" })));
Check(phFound.GetProperty("total").GetInt32() > 0 && phAll.GetProperty("total").GetInt32() == 980 && phAll.GetProperty("items").GetArrayLength() == 50
      && phAll.GetProperty("items")[0].GetProperty("text").GetString() == AllWords.Find(phStore.Data, d0)[50].Word.Text,
    "phone: all words searchable and paged by 50");
var phAdd = PhJson(phApi.Handle(PhReq("POST", "/api/add", """{"text":"Serendipity.","meaning":"sự tình cờ may mắn","phonetic":"ˌserənˈdɪpəti","pos":"n","example":"","exampleMeaning":""}""", phCookie)));
var phAgain = PhJson(phApi.Handle(PhReq("POST", "/api/add", """{"text":"serendipity","meaning":"x"}""", phCookie)));
var phInbox = PhJson(phApi.Handle(PhReq("POST", "/api/add", """{"text":"quixotic","meaning":""}""", phCookie)));
Check(phAdd.GetProperty("ok").GetBoolean() && QuickAdd.FindInLibrary(phStore.Data, "serendipity") is var (phPlan, _) && phPlan.Name == QuickAdd.PlanName
      && !phAgain.GetProperty("ok").GetBoolean() && phInbox.GetProperty("ok").GetBoolean() && QuickAdd.IsWaiting(phStore.Data, "quixotic")
      && PhJson(phApi.Handle(PhReq("GET", "/api/lookup", cookie: phCookie, query: new() { ["text"] = "Serendipity" }))).GetProperty("message").GetString()!.StartsWith("Đã có trong Từ của tôi"),
    "phone: add a word with its meaning, to the AI list without one, never twice");
Check(phApi.Handle(PhReq("POST", "/api/rate", "not json", phCookie)).Status == 400
      && phApi.Handle(PhReq("POST", "/api/rate", $$"""{"id":"{{Guid.NewGuid()}}","learned":true}""", phCookie)).Status == 404
      && phApi.Handle(PhReq("GET", "/nothing", cookie: phCookie)).Status == 404,
    "phone: bad body 400, unknown word or page 404");

// The socket server, over loopback, with a real HTTP client.
using (var phServer = new PhoneServer(System.Net.IPAddress.Loopback, 0, r => Task.FromResult(phApi.Handle(r))))
using (var phHttp = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }))
{
    var phBase = $"http://127.0.0.1:{phServer.Port}";
    var phHttpLogin = phHttp.GetAsync($"{phBase}/?k={phKey}").Result;
    var phGet = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"{phBase}/api/words?q=s%C3%A1ch&filter=all");
    phGet.Headers.Add("Cookie", phCookie);
    var phGetResponse = phHttp.Send(phGet);
    var phPost = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"{phBase}/api/check")
        { Content = new System.Net.Http.StringContent("""{"typed":"cà phê","answer":"cà phê"}""", System.Text.Encoding.UTF8, "application/json") };
    phPost.Headers.Add("Cookie", phCookie);
    phPost.Headers.Add("X-Voca", "1");
    var phPostBody = phHttp.Send(phPost).Content.ReadAsStringAsync().Result;
    Check((int)phHttpLogin.StatusCode == 302 && phHttpLogin.Headers.Location?.ToString() == "/"
          && (int)phGetResponse.StatusCode == 200 && phGetResponse.Content.Headers.ContentType?.MediaType == "application/json"
          && phPostBody.Contains("\"correct\":true"),
        "phone server: redirect, GET with an accented query, POST with a UTF-8 body");
    var phLocked = phHttp.GetAsync($"{phBase}/").Result;
    Check((int)phLocked.StatusCode == 401 && phLocked.Content.ReadAsStringAsync().Result.Contains("quét mã QR"),
        "phone server: without the cookie the page says how to connect");
}
Check(PhoneServer.ParseQuery("q=c%C3%A0+ph%C3%AA&x").Count == 2 && PhoneServer.ParseQuery("q=c%C3%A0+ph%C3%AA&x")["q"] == "cà phê",
    "phone server: query strings decode + and %XX");
var phTaken = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
phTaken.Start();
var phTakenPort = ((System.Net.IPEndPoint)phTaken.LocalEndpoint).Port;
phStore.Update(d => { d.Settings.PhonePort = phTakenPort; d.Settings.PhoneAccess = true; });
using (var phAccess = new PhoneAccess(phStore, f => Task.FromResult(f())))
{
    var phBusy = !phAccess.Running && phAccess.Error?.Contains(phTakenPort.ToString()) == true;
    phTaken.Stop();
    phStore.Update(d => d.Settings.PhonePort = 0); // any free port
    var phStarted = phAccess.Running && phAccess.Error is null && phAccess.Port > 0 && phAccess.Link("1.2.3.4").EndsWith($"/?k={phKey}");
    phStore.Update(d => d.Settings.PhoneAccess = false);
    Check(phBusy && phStarted && !phAccess.Running, "phone access: a taken port is reported; follows the settings on and off");
}
Check(PhoneAccess.QrPng("http://192.168.1.5:8765/?k=abc").Take(4).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }),
    "phone access: QR code is a PNG");

// ---------- phone: videos ----------
var pvFolder = Path.Combine(Path.GetTempPath(), "voca-pv-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pvFolder);
var pvEp2 = Path.Combine(pvFolder, "Show E2.mp4");
var pvEp10 = Path.Combine(pvFolder, "Show E10.mkv");
File.WriteAllBytes(pvEp2, Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray());
File.WriteAllBytes(pvEp10, [1, 2, 3]);
File.WriteAllText(Path.Combine(pvFolder, "notes.txt"), "not a video");
var pvLearning = phStore.Data.Plans.SelectMany(p => p.Words).First(w => !w.Learned && w.Text == "university");
File.WriteAllText(Path.ChangeExtension(pvEp2, ".srt"),
    "1\n00:00:01,000 --> 00:00:03,000\nThe university is near the serendipity cafe.\n\n2\n00:00:04,000 --> 00:00:06,500\nWe ate flabbergasting dumplings.\n");
var pvKey = phStore.Data.Settings.PhoneKey;
Check(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie)) is var pvNone && PhJson(pvNone).GetProperty("folders").GetArrayLength() == 0,
    "phone videos: none until a folder is known");
phStore.Update(d => d.Settings.PhoneVideoFolders.Add(pvFolder));
var pvList = PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie)));
var pvVideos = pvList.GetProperty("folders")[0].GetProperty("videos");
var pvId = PhoneVideos.Id(pvEp2);
Check(pvVideos.GetArrayLength() == 2 && pvVideos[0].GetProperty("id").GetString() == pvId && pvVideos[0].GetProperty("srt").GetBoolean()
      && pvVideos[0].GetProperty("phone").GetBoolean() && !pvVideos[1].GetProperty("srt").GetBoolean() && !pvVideos[1].GetProperty("phone").GetBoolean(),
    "phone videos: media files of an added folder in episode order (E2 before E10), with .srt and phone-friendly flags");
var pvVideo = PhJson(phApi.Handle(PhReq("GET", "/api/video", cookie: phCookie, query: new() { ["id"] = pvId })));
var pvParts = pvVideo.GetProperty("lines")[0].GetProperty("parts").EnumerateArray().ToList();
string? PvMark(System.Text.Json.JsonElement part) => part.TryGetProperty("m", out var m) ? m.GetString() : null;
Check(pvVideo.GetProperty("lines").GetArrayLength() == 2 && pvVideo.GetProperty("lines")[1].GetProperty("s").GetDouble() == 4
      && string.Concat(pvParts.Select(p => p.GetProperty("t").GetString())) == "The university is near the serendipity cafe."
      && pvParts.Any(p => PvMark(p) == "learning" && p.GetProperty("meaning").GetString() == pvLearning.Meaning)
      && pvVideo.GetProperty("lines")[1].GetProperty("parts").EnumerateArray().Any(p => PvMark(p) == "new" && p.GetProperty("t").GetString() == "flabbergasting")
      && pvVideo.GetProperty("next").GetProperty("id").GetString() == PhoneVideos.Id(pvEp10),
    "phone videos: subtitle words marked like on the computer (learning with meaning, new), next episode");
var pvSaved = PhJson(phApi.Handle(PhReq("POST", "/api/video/word", """{"text":"flabbergasting"}""", phCookie)));
var pvAfter = PhJson(phApi.Handle(PhReq("GET", "/api/video", cookie: phCookie, query: new() { ["id"] = pvId })));
Check(pvSaved.GetProperty("ok").GetBoolean() && QuickAdd.IsWaiting(phStore.Data, "flabbergasting")
      && pvAfter.GetProperty("lines")[1].GetProperty("parts").EnumerateArray().Any(p => PvMark(p) == "waiting"),
    "phone videos: a tapped new word goes to Từ của tôi and is then marked as saved");
var pvWhole = phApi.Handle(PhReq("GET", "/media/" + pvId, cookie: phCookie));
var pvPart = phApi.Handle(new PhoneRequest("GET", "/media/" + pvId, new Dictionary<string, string>(),
    new Dictionary<string, string> { ["cookie"] = phCookie, ["range"] = "bytes=990-" }, ""));
var pvBad = phApi.Handle(new PhoneRequest("GET", "/media/" + pvId, new Dictionary<string, string>(),
    new Dictionary<string, string> { ["cookie"] = phCookie, ["range"] = "bytes=5000-" }, ""));
Check(pvWhole.Status == 200 && pvWhole.File! == new FileBody(pvEp2, 0, 1000) && pvWhole.ContentType == "video/mp4"
      && pvPart.Status == 206 && pvPart.File! == new FileBody(pvEp2, 990, 10) && pvPart.Headers!["Content-Range"] == "bytes 990-999/1000"
      && pvBad.Status == 416,
    "phone videos: the file is sent whole or by range (206), a range past the end is refused (416)");
Check(PhoneVideos.Range("bytes=0-99", 1000) == (0, 100) && PhoneVideos.Range("bytes=-100", 1000) == (900, 100)
      && PhoneVideos.Range("bytes=900-5000", 1000) == (900, 100) && PhoneVideos.Range("bytes=10-5", 1000) is null && PhoneVideos.Range("items=0-1", 1000) is null,
    "phone videos: Range headers start-end, suffix, open end clipped, invalid");
Check(phApi.Handle(PhReq("GET", "/media/0123456789abcdef", cookie: phCookie)).Status == 404
      && phApi.Handle(PhReq("GET", "/media/" + pvId)).Status == 401,
    "phone videos: files outside the known folders do not exist; no cookie, no video");
var pvVtt = System.Text.Encoding.UTF8.GetString(phApi.Handle(PhReq("GET", $"/media/{pvId}.vtt", cookie: phCookie)).Body);
Check(pvVtt.StartsWith("WEBVTT") && pvVtt.Contains("00:00:04.000 --> 00:00:06.500\nWe ate flabbergasting dumplings."),
    "phone videos: subtitles also as WebVTT for the phone's own full-screen player");
phApi.Handle(PhReq("POST", "/api/video/progress", $$"""{"id":"{{pvId}}","seconds":321.5,"length":1500}""", phCookie));
var pvHistory = WatchProgress.Load(phFolder);
var pvResumed = PhJson(phApi.Handle(PhReq("GET", "/api/video", cookie: phCookie, query: new() { ["id"] = pvId })));
var pvContinue = PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie))).GetProperty("continue");
Check(pvHistory.Videos[0].Video == pvEp2 && pvHistory.Videos[0].Seconds == 321.5 && pvResumed.GetProperty("resume").GetDouble() == 321.5
      && pvContinue.GetProperty("id").GetString() == pvId && pvContinue.GetProperty("text").GetString() == "Xem tiếp từ 5:21",
    "phone videos: where the phone stopped is saved in watch.json, so both the phone and the computer continue there");
phApi.Handle(PhReq("POST", "/api/video/progress", $$"""{"id":"{{pvId}}","seconds":1500,"length":1500}""", phCookie));
Check(PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie))).GetProperty("continue").GetProperty("id").GetString() == PhoneVideos.Id(pvEp10),
    "phone videos: watched to the end → Xem tiếp offers the next episode");
using (var pvServer = new PhoneServer(System.Net.IPAddress.Loopback, 0, r => Task.FromResult(phApi.Handle(r))))
using (var pvHttp = new System.Net.Http.HttpClient())
{
    var pvGet = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{pvServer.Port}/media/{pvId}");
    pvGet.Headers.Add("Cookie", phCookie);
    pvGet.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
    var pvResponse = pvHttp.Send(pvGet);
    var pvBytes = pvResponse.Content.ReadAsByteArrayAsync().Result;
    Check((int)pvResponse.StatusCode == 206 && pvBytes.Length == 100 && pvBytes[0] == 100 && pvBytes[99] == 199
          && pvResponse.Content.Headers.ContentRange?.ToString() == "bytes 100-199/1000",
        "phone server: streams the asked bytes of a video");
}
Directory.Delete(pvFolder, recursive: true);

// ---------- phone: the film folder ----------
var fmRoot = Path.Combine(Path.GetTempPath(), "voca-films-" + Guid.NewGuid().ToString("N"));
var fmNaruto = Path.Combine(fmRoot, "Naruto");
Directory.CreateDirectory(Path.Combine(fmNaruto, "srt-goc"));
Directory.CreateDirectory(Path.Combine(fmRoot, "Empty film"));
string FmEp(int n) => Path.Combine(fmNaruto, $"Naruto_Shippuden_[English]_720P_S01_E{n:00}.mp4");
foreach (var n in new[] { 10, 2, 1 }) File.WriteAllBytes(FmEp(n), [1]);
File.WriteAllText(Path.ChangeExtension(FmEp(1), ".srt"), "1\n00:00:01,000 --> 00:00:02,000\nBelieve it.\n");
File.WriteAllText(Path.Combine(fmNaruto, "srt-goc", "Naruto_Shippuden_[English]_720P_S01_E01.srt"), "old");
File.WriteAllBytes(Path.Combine(fmRoot, "Inception.mp4"), [1]);
Check(PhoneVideos.Labels([FmEp(1), FmEp(2), FmEp(10)]).SequenceEqual(["E01", "E02", "E10"])
      && PhoneVideos.Labels(["C:\\x\\My_Movie.mp4"]).SequenceEqual(["My Movie"])
      && PhoneVideos.Labels(["C:\\x\\Ep 1.mp4", "C:\\x\\Ep 10.mp4"]).SequenceEqual(["1", "10"]),
    "films: episode labels drop the shared start of the file names");
phStore.Update(d => d.Settings.FilmFolder = fmRoot);
var fmList = PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie)));
var fmFilms = fmList.GetProperty("films").EnumerateArray().ToList();
var fmNarutoFilm = fmFilms.Single(f => f.GetProperty("name").GetString() == "Naruto");
Check(fmFilms.Count == 2 && fmFilms.Any(f => f.GetProperty("name").GetString() == "Inception" && f.GetProperty("count").GetInt32() == 1)
      && fmNarutoFilm.GetProperty("count").GetInt32() == 3 && fmNarutoFilm.GetProperty("subtitles").GetInt32() == 1
      && fmNarutoFilm.GetProperty("videos").EnumerateArray().Select(v => v.GetProperty("name").GetString()).SequenceEqual(["E01", "E02", "E10"])
      && fmNarutoFilm.GetProperty("status").GetString() == "3 tập",
    "films: each subfolder is a film named after it with its episodes in order; a loose video is a film; an empty folder is not listed");
var fmE1 = PhoneVideos.Id(FmEp(1));
var fmVideo = PhJson(phApi.Handle(PhReq("GET", "/api/video", cookie: phCookie, query: new() { ["id"] = fmE1 })));
Check(fmVideo.GetProperty("name").GetString() == "Naruto · E01" && fmVideo.GetProperty("next").GetProperty("name").GetString() == "E02"
      && fmVideo.GetProperty("lines").GetArrayLength() == 1,
    "films: an episode plays as “Naruto · E01” with its .srt, and E02 comes next");
phApi.Handle(PhReq("POST", "/api/video/progress", $$"""{"id":"{{fmE1}}","seconds":754,"length":1400}""", phCookie));
var fmAfter = PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie)));
Check(fmAfter.GetProperty("films")[0].GetProperty("name").GetString() == "Naruto"
      && fmAfter.GetProperty("films")[0].GetProperty("status").GetString() == "Đang xem E01 · 12:34"
      && fmAfter.GetProperty("continue").GetProperty("name").GetString() == "Naruto · E01"
      && !fmAfter.GetProperty("folders").EnumerateArray().Any(f => f.GetProperty("path").GetString() == fmNaruto),
    "films: the film being watched comes first and says where; it is not listed again as a plain folder");
phApi.Handle(PhReq("POST", "/api/video/progress", $$"""{"id":"{{fmE1}}","seconds":1400,"length":1400}""", phCookie));
Check(PhJson(phApi.Handle(PhReq("GET", "/api/videos", cookie: phCookie))).GetProperty("films")[0].GetProperty("status").GetString() == "Tiếp theo: E02",
    "films: after an episode is watched, the next one is named");
phStore.Update(d => d.Settings.FilmFolder = "");
Directory.Delete(fmRoot, recursive: true);

// ---------- mkv: tracks, subtitles inside, one audio track for Windows ----------
static byte[] MkEl(uint id, params byte[][] children)
{
    var payload = children.SelectMany(c => c).ToArray();
    var idBytes = BitConverter.GetBytes(id).Reverse().SkipWhile(b => b == 0).ToArray();
    var n = payload.Length;
    return [.. idBytes, (byte)(0x10 | (n >> 24)), (byte)(n >> 16), (byte)(n >> 8), (byte)n, .. payload];
}
static byte[] MkUInt(uint id, byte value) => MkEl(id, [value]);
static byte[] MkText(uint id, string text) => MkEl(id, System.Text.Encoding.UTF8.GetBytes(text));
static uint MkCrc(ReadOnlySpan<byte> data)
{
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1; }
    return ~crc;
}
static byte[] MkBlock(int track, short rel, string text, bool zlib = false)
{
    var data = System.Text.Encoding.UTF8.GetBytes(text);
    if (zlib)
    {
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal)) z.Write(data);
        data = packed.ToArray();
    }
    return [(byte)(0x80 | track), (byte)(rel >> 8), (byte)rel, 0, .. data];
}
static byte[] MkGroup(int track, short rel, int duration, string text, bool zlib = false) =>
    MkEl(0xA0, MkEl(0xA1, MkBlock(track, rel, text, zlib)), MkEl(0x9B, [(byte)(duration >> 8), (byte)duration]));
static byte[] MkTrack(byte number, byte type, string codec, params byte[][] more) =>
    MkEl(0xAE, [MkUInt(0xD7, number), MkUInt(0x83, type), MkText(0x86, codec), .. more]);

var mkEntries = new[]
{
    MkTrack(1, 1, "V_MPEG4/ISO/AVC"),
    MkTrack(2, 2, "A_AAC", MkText(0x22B59C, "jpn"), MkText(0x536E, "Japanese")),
    MkTrack(3, 2, "A_AAC", MkUInt(0x88, 0)), // no Language element: English by the Matroska default
    MkTrack(4, 0x11, "S_TEXT/ASS", MkText(0x22B59C, "eng"), MkUInt(0x55AA, 1), MkText(0x536E, "Signs")),
    MkTrack(5, 0x11, "S_TEXT/ASS", MkText(0x22B59C, "eng"), MkEl(0x6D80, MkEl(0x6240, MkEl(0x5034, MkUInt(0x4254, 0))))),
    MkTrack(6, 0x11, "S_TEXT/UTF8", MkText(0x22B59C, "eng")),
}.SelectMany(e => e).ToArray();
var mkCrc = BitConverter.GetBytes(MkCrc(mkEntries)); // little-endian, as Matroska stores it
var mkFile = Path.Combine(Path.GetTempPath(), "voca-mkv-" + Guid.NewGuid().ToString("N") + ".mkv");
File.WriteAllBytes(mkFile, [
    .. MkEl(0x1A45DFA3, MkText(0x4282, "matroska")),
    .. MkEl(0x18538067,
        MkEl(0x1549A966, MkEl(0x2AD7B1, [0x0F, 0x42, 0x40])), // 1 ms per tick
        MkEl(0x1654AE6B, MkEl(0xBF, mkCrc), mkEntries),
        MkEl(0x1F43B675,
            MkEl(0xE7, [0x03, 0xE8]), // cluster at 1 s
            MkEl(0xA3, MkBlock(2, 0, "audio bytes")),
            MkGroup(5, 0, 1500, @"1,0,Default,,0,0,0,,{\i1}Hello\Nthere", zlib: true),
            MkEl(0xA3, MkBlock(6, 500, "<i>Plain</i> line")),
            MkGroup(5, 2000, 1000, "2,0,OP Romaji,,0,0,0,,mijika ni arumono", zlib: true),
            MkGroup(5, 2000, 1000, @"3,0,Default,,0,0,0,,{\p1}m 0 0 l 10 10{\p0}Look out!", zlib: true),
            MkGroup(5, 5000, 1000, "4,0,Default,,0,0,0,,A: Hi", zlib: true),
            MkGroup(5, 5000, 1000, "5,0,Default,,0,0,0,,B: Yo", zlib: true),
            MkGroup(4, 5000, 1000, "6,0,Sign,,0,0,0,,EXIT")),
        MkEl(0x1F43B675,
            MkEl(0xE7, [0x27, 0x10]), // cluster at 10 s
            MkGroup(5, 0, 2000, @"7,0,Default,,0,0,0,,Second\hcluster", zlib: true)))
]);
var mkTracks = Matroska.ReadTracks(mkFile);
Check(mkTracks.Select(t => t.Kind).SequenceEqual([MkvTrackKind.Video, MkvTrackKind.Audio, MkvTrackKind.Audio, MkvTrackKind.Subtitle, MkvTrackKind.Subtitle, MkvTrackKind.Subtitle])
      && mkTracks[1].Language == "jpn" && mkTracks[1].Label == "Tiếng Nhật · Japanese" && mkTracks[2].IsEnglish && !mkTracks[2].Default
      && mkTracks[3].Forced && mkTracks.Count(t => t.IsText) == 3,
    "mkv: tracks read with kind, language (English when left out), name and flags");
Check(Matroska.BestEnglishSubtitles(mkTracks)?.Number == 5, "mkv: the English dialogue track is preferred over the forced signs track");
var mkLines = Matroska.ReadSubtitles(mkFile, 5);
Check(mkLines.Select(l => (l.Start.TotalSeconds, l.End.TotalSeconds, l.Text)).SequenceEqual(
        [(1.0, 2.5, "Hello\nthere"), (3.0, 4.0, "Look out!"), (6.0, 7.0, "A: Hi\nB: Yo"), (10.0, 12.0, "Second cluster")]),
    "mkv: ASS subtitles inside (zlib-packed) read as lines: styling and drawings dropped, songs left out, lines of one moment joined");
Check(Matroska.ReadSubtitles(mkFile, 6) is [{ Text: "Plain line" } plain] && plain.Start == TimeSpan.FromSeconds(1.5) && plain.End == TimeSpan.FromSeconds(5.5),
    "mkv: SRT subtitles inside read without tags; a block without duration lasts 4 s at most");
Check(Matroska.ReadSubtitles(mkFile, 4) is [{ Text: "EXIT" }], "mkv: a track with only signs keeps them");
var mkFake = Path.ChangeExtension(mkFile, ".fake.mkv");
File.WriteAllText(mkFake, "not a video");
Check(Matroska.ReadTracks(Path.Combine(Path.GetTempPath(), "voca-missing.mkv")).Count == 0 && Matroska.ReadTracks(mkFake).Count == 0,
    "mkv: a missing or non-Matroska file has no tracks");
File.Delete(mkFake);

var mkPatches = Matroska.KeepAudio(mkFile, 3);
var mkBytes = File.ReadAllBytes(mkFile);
foreach (var p in mkPatches) mkBytes[p.Offset] = p.Value;
var mkPatched = Path.ChangeExtension(mkFile, ".patched.mkv");
File.WriteAllBytes(mkPatched, mkBytes);
var mkCrcAt = mkBytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xBF, 0x10, 0, 0, 4]) + 5; // the CRC-32 element: id, 4-byte size, then its 4 bytes
var mkPatchedTracks = Matroska.ReadTracks(mkPatched);
Check(mkPatchedTracks.Where(t => t.Kind == MkvTrackKind.Audio).Select(t => t.Number).SequenceEqual([255, 3])
      && BitConverter.ToUInt32(mkBytes, mkCrcAt) == MkCrc(mkBytes.AsSpan(mkCrcAt + 4, mkEntries.Length))
      && mkPatches.Count == 1 + 4 && Matroska.KeepAudio(mkFile, 2).Count == 5,
    "mkv: keeping one audio track gives the others an unused number and fixes the track list's CRC-32");
using (var mkStream = new PatchedStream(File.OpenRead(mkFile), mkPatches))
{
    var mkRead = new byte[mkBytes.Length];
    mkStream.Position = 0;
    var mkHalf = mkRead.Length / 2;
    mkStream.ReadExactly(mkRead, 0, mkHalf); // read in two parts: patches on either side of the cut still apply
    mkStream.ReadExactly(mkRead, mkHalf, mkRead.Length - mkHalf);
    Check(mkRead.SequenceEqual(mkBytes) && !File.ReadAllBytes(mkFile).SequenceEqual(mkBytes),
        "mkv: the file is read with the bytes changed, the file itself untouched");
}
var mkPhoneDir = Directory.CreateTempSubdirectory("voca-mkv-phone").FullName;
var mkPhoneVideo = Path.Combine(mkPhoneDir, "Episode 1.mkv");
File.Copy(mkFile, mkPhoneVideo);
phStore.Update(d => d.Settings.PhoneVideoFolders = [mkPhoneDir]);
var mkPhoneLines = PhJson(phApi.Handle(PhReq("GET", "/api/video", cookie: phCookie, query: new() { ["id"] = PhoneVideos.Id(mkPhoneVideo) }))).GetProperty("lines");
Check(mkPhoneLines.GetArrayLength() == 4 && !File.Exists(Path.ChangeExtension(mkPhoneVideo, ".srt")),
    "phone videos: an mkv without .srt shows the English subtitles inside it, and no .srt is written (left to Whisper)");
phStore.Update(d => d.Settings.PhoneVideoFolders = []);
Directory.Delete(mkPhoneDir, recursive: true);
File.Delete(mkFile);
File.Delete(mkPatched);

Console.WriteLine(fails == 0 ? "\nALL PASSED" : $"\n{fails} FAILED");
return fails == 0 ? 0 : 1;
