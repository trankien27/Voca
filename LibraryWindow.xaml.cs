using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voca.Models;
using Voca.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Voca;

/// <summary>
/// Everything that used to live on the web page: the course order, editing plans, creating a topic
/// from an AI prompt + pasted form, and settings. Works on the shared <see cref="Store"/>.
/// </summary>
public partial class LibraryWindow : Window
{
    private static readonly Brush DoneWash = Frozen(0xE7, 0xF7, 0xEE), DoneInk = Frozen(0x06, 0x76, 0x47);
    private static readonly Brush NowWash = Frozen(0x6C, 0x5C, 0xE7), NowInk = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush WaitWash = Frozen(0xEE, 0xEC, 0xFF), WaitInk = Frozen(0x4B, 0x3C, 0xC4);
    private static readonly Brush WarnInk = Frozen(0xB5, 0x47, 0x08), Clear = Frozen(0, 0, 0);

    private readonly Store _store;
    private readonly Action _openStats;
    private readonly AppActions _actions;
    private bool _nameEdited;
    private bool _promptCopied;
    private bool _loading = true;

    private sealed record PlanRow(Guid Id, string Order, string Name, string Meta, string Chip, Brush ChipBrush, Brush ChipInk);
    private sealed record DayRow(int Day, string Label);
    private sealed record PreviewDay(string Title, string Count, Brush CountInk, string Sample);

    private AppData Data => _store.Data;
    private Plan? EditedPlan => PlanBox.SelectedItem as Plan;
    private int? SelectedDay => (DaysList.SelectedItem as DayRow)?.Day;

    public LibraryWindow(Store store, AppActions actions)
    {
        InitializeComponent();
        _store = store;
        _actions = actions;
        _openStats = actions.OpenStats;
        MistakesTab.Content = new MistakesView(store, actions);
        // The version list is fetched the first time Settings is opened.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedItem == SettingsTab && !_versionsLoaded) _ = LoadVersionsAsync();
        };
        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;

        RefreshCourse();
        RefreshPlanBox(select: CourseEngine.Current(Data)?.Plan);
        LoadSettings();
        RefreshPrompt();
        GuideExampleBox.Text = PlanFormat.Example;
        ShowGuide(Data.Settings.ShowCreateGuide);
        _loading = false;
        UpdateGuide();
    }

    /// <summary>Brings the "Từ sai" tab to the front.</summary>
    public void ShowMistakes() => Tabs.SelectedItem = MistakesTab;

    /// <summary>Brings Settings → "Cập nhật phiên bản" to the front.</summary>
    public void ShowUpdates()
    {
        Tabs.SelectedItem = SettingsTab;
        Dispatcher.BeginInvoke(() => UpdatesPanel.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnStoreChanged() => Dispatcher.Invoke(() =>
    {
        RefreshCourse();
        PlanBox.Items.Refresh();
    });

    private void Status(string text) => StatusText.Text = text;

    // =============================== course ===============================

    private void RefreshCourse()
    {
        var current = CourseEngine.Current(Data);
        CourseList.ItemsSource = CourseEngine.CoursePlans(Data).Select((p, i) => Row(p, (i + 1).ToString())).ToList();
        OutsideList.ItemsSource = Data.Plans.Where(p => !Data.Course.Contains(p.Id)).Select(p => Row(p, "")).ToList();
        if (current is var (plan, _)) CourseList.SelectedItem = ((List<PlanRow>)CourseList.ItemsSource).FirstOrDefault(r => r.Id == plan.Id);
    }

    private PlanRow Row(Plan plan, string order)
    {
        var state = CourseEngine.StateOf(Data, plan.Id);
        var studied = plan.Words.Count(w => w.Review is not null);
        var meta = $"{(plan.Level.Length > 0 ? plan.Level + " · " : "")}{plan.DayCount} ngày · {plan.Words.Count} từ · đã học {studied}" +
                   (state == "now" ? $" · đang ở Ngày {Data.Position.Day}" : "");
        return state switch
        {
            "done" => new PlanRow(plan.Id, order, plan.Name, meta, "Đã xong", DoneWash, DoneInk),
            "now" => new PlanRow(plan.Id, order, plan.Name, meta, "Đang học", NowWash, NowInk),
            "wait" => new PlanRow(plan.Id, order, plan.Name, meta, "Chờ", WaitWash, WaitInk),
            _ => new PlanRow(plan.Id, order, plan.Name, meta, "", System.Windows.Media.Brushes.Transparent, Clear)
        };
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (CourseList.SelectedItem is not PlanRow row) { Status("Hãy chọn một tuần trong khóa học."); return; }
        var moved = false;
        _store.Update(d => moved = CourseEngine.Move(d, row.Id, delta));
        if (!moved) Status("Chỉ đổi được thứ tự giữa các tuần đang chờ (tuần đã học hoặc đang học giữ nguyên vị trí).");
        CourseList.SelectedItem = ((List<PlanRow>)CourseList.ItemsSource).FirstOrDefault(r => r.Id == row.Id);
    }

    private void RemoveFromCourse_Click(object sender, RoutedEventArgs e)
    {
        if (CourseList.SelectedItem is not PlanRow row) { Status("Hãy chọn một tuần trong khóa học."); return; }
        var state = CourseEngine.StateOf(Data, row.Id);
        if (state == "now" && System.Windows.MessageBox.Show(this, $"“{row.Name}” đang được học. Bỏ khỏi khóa học thì app chuyển về tuần đầu tiên của khóa. Tiếp tục?",
                "Voca", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Update(d => CourseEngine.RemoveFromCourse(d, row.Id));
        Status($"Đã bỏ “{row.Name}” khỏi khóa học. Lộ trình vẫn còn ở cột bên phải.");
    }

    private void AddToCourse_Click(object sender, RoutedEventArgs e)
    {
        if (OutsideList.SelectedItem is not PlanRow row) { Status("Hãy chọn một lộ trình ngoài khóa học."); return; }
        _store.Update(d => CourseEngine.AddToCourse(d, row.Id));
        Status($"Đã thêm “{row.Name}” vào cuối khóa học.");
    }

    private void OpenTest(Guid? planId, int? fromDay = null, int? toDay = null) =>
        new TestWindow(_store, planId, fromDay, toDay, _actions) { Owner = this }.Show();

    private void CourseTest_Click(object sender, RoutedEventArgs e) =>
        OpenTest((CourseList.SelectedItem as PlanRow)?.Id ?? (OutsideList.SelectedItem as PlanRow)?.Id);

    private void DayTest_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) { Status("Hãy chọn một lộ trình."); return; }
        OpenTest(plan.Id, SelectedDay, SelectedDay);
    }

    private void EditCourseItem_Click(object sender, RoutedEventArgs e) => OpenEditor((CourseList.SelectedItem as PlanRow)?.Id);
    private void EditOutsideItem_Click(object sender, RoutedEventArgs e) => OpenEditor((OutsideList.SelectedItem as PlanRow)?.Id);

    private void OpenEditor(Guid? planId)
    {
        if (planId is null) return;
        RefreshPlanBox(select: Data.Plans.FirstOrDefault(p => p.Id == planId));
        Tabs.SelectedItem = EditTab;
    }

    // =============================== plan editor ===============================

    private void RefreshPlanBox(Plan? select = null)
    {
        PlanBox.ItemsSource = null;
        PlanBox.ItemsSource = Data.Plans;
        PlanBox.SelectedItem = select ?? Data.Plans.FirstOrDefault();
    }

    private void PlanBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelRegen_Click(this, e);
        var plan = EditedPlan;
        PlanNameBox.Text = plan?.Name ?? "";
        PlanLevelBox.Text = plan?.Level ?? "";
        PlanDescriptionBox.Text = plan?.Description ?? "";
        RefreshDays();
    }

    private void RefreshDays(int? select = null)
    {
        var plan = EditedPlan;
        if (plan is null) { DaysList.ItemsSource = null; WordsGrid.ItemsSource = null; return; }
        var keep = select ?? SelectedDay;
        var position = Data.Position;
        var days = Enumerable.Range(1, Math.Max(1, plan.DayCount)).Select(d =>
        {
            var marker = position.PlanId == plan.Id && position.Day == d ? "▶ " : "";
            var title = plan.DayTitle(d);
            return new DayRow(d, $"{marker}Ngày {d} · {plan.Words.Count(w => w.Day == d)} từ{(title.Length > 0 ? "\n" + title : "")}");
        }).ToList();
        DaysList.ItemsSource = days;
        DaysList.SelectedItem = days.FirstOrDefault(d => d.Day == keep) ?? days.First();
    }

    private void DaysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var plan = EditedPlan;
        if (plan is null || SelectedDay is not int day) { WordsGrid.ItemsSource = null; return; }
        WordsGrid.ItemsSource = plan.Words.Where(w => w.Day == day).ToList();
        DayTitleBox.Text = plan.DayTitle(day);
        RegenPanel.Visibility = Visibility.Collapsed;
    }

    private void WordsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        // The binding writes the value after this event; save once it has.
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Row.Item is Word word) word.Phonetic = PlanFormat.NormalizePhonetic(word.Phonetic);
            _store.Save();
            Status($"Đã lưu lúc {DateTime.Now:HH:mm:ss}.");
        });
    }

    private void SavePlanInfo_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) return;
        if (string.IsNullOrWhiteSpace(PlanNameBox.Text)) { Status("Tên lộ trình không được để trống."); return; }
        _store.Update(_ =>
        {
            plan.Name = PlanNameBox.Text.Trim();
            plan.Level = PlanLevelBox.Text.Trim();
            plan.Description = PlanDescriptionBox.Text.Trim();
        });
        RefreshPlanBox(select: plan);
        Status("Đã lưu thông tin lộ trình.");
    }

    private void SaveDayTitle_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan || SelectedDay is not int day) return;
        _store.Update(_ =>
        {
            if (string.IsNullOrWhiteSpace(DayTitleBox.Text)) plan.DayTitles.Remove(day);
            else plan.DayTitles[day] = DayTitleBox.Text.Trim();
        });
        RefreshDays(day);
    }

    private void AddDay_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) return;
        var day = plan.DayCount + 1;
        _store.Update(_ => plan.Words.Add(new Word { Day = day, Text = "new word" }));
        RefreshDays(day);
        Status($"Đã thêm Ngày {day}. Sửa từ đầu tiên rồi thêm các từ khác, hoặc dùng “Tạo lại ngày này bằng prompt”.");
    }

    private void AddWord_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan || SelectedDay is not int day) return;
        var word = new Word { Day = day, Text = "new word" };
        _store.Update(_ =>
        {
            var last = plan.Words.FindLastIndex(w => w.Day == day);
            plan.Words.Insert(last < 0 ? plan.Words.Count : last + 1, word);
        });
        RefreshDays(day);
        WordsGrid.SelectedItem = word;
        WordsGrid.ScrollIntoView(word);
        WordsGrid.CurrentCell = new DataGridCellInfo(word, WordsGrid.Columns[0]);
        WordsGrid.BeginEdit();
    }

    private void DeleteWord_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) return;
        var selected = WordsGrid.SelectedItems.OfType<Word>().ToList();
        if (selected.Count == 0) { Status("Hãy chọn từ cần xóa trong bảng."); return; }
        var learned = selected.Count(w => w.Review is not null);
        if (learned > 0 && System.Windows.MessageBox.Show(this, $"{learned} từ đã có tiến độ học sẽ mất luôn tiến độ. Xóa?",
                "Voca", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.Update(_ => plan.Words.RemoveAll(selected.Contains));
        RefreshDays();
        Status($"Đã xóa {selected.Count} từ.");
    }

    private void StudyFromDay_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan || SelectedDay is not int day) return;
        if (!plan.Words.Any(w => w.Day == day)) { Status("Ngày này chưa có từ."); return; }
        _store.Update(d => CourseEngine.SetPosition(d, plan.Id, day));
        RefreshDays(day);
        Status($"Đã chuyển vị trí học sang “{plan.Name}”, Ngày {day}. Tiến độ các từ đã học vẫn giữ nguyên.");
    }

    private void RegenDay_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan || SelectedDay is not int day) return;
        var count = Math.Max(5, plan.Words.Count(w => w.Day == day));
        var avoid = plan.Words.Where(w => w.Day != day).Select(w => w.Text).ToList();
        var request = new PlanRequest(plan.Name, plan.DayCount, count, plan.Level.Length > 0 ? plan.Level : "B1–B2", "", plan.Name);
        var prompt = PlanFormat.BuildPrompt(request, day, plan.DayTitle(day), avoid);
        RegenPanel.Visibility = Visibility.Visible;
        if (!TryCopy(prompt)) RegenBox.Text = prompt;
        Status(TryCopyStatus);
    }

    private void ApplyRegen_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan || SelectedDay is not int day) return;
        var read = PlanFormat.Read(RegenBox.Text, new PlanRequest(plan.Name, 1, 0, plan.Level, "", plan.Name));
        var group = read.Plan?.Words.GroupBy(w => w.Day).OrderBy(g => g.Key).FirstOrDefault();
        if (group is null) { Status(read.Errors.FirstOrDefault() ?? "Không đọc được từ nào trong câu trả lời."); return; }
        var words = group.ToList();
        var title = read.Plan!.DayTitle(group.Key);
        var lost = plan.Words.Count(w => w.Day == day && w.Review is not null);
        if (lost > 0 && System.Windows.MessageBox.Show(this, $"{lost} từ của ngày này đã có tiến độ học. Thay thế sẽ mất tiến độ của các từ đó. Tiếp tục?",
                "Voca", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.Update(_ =>
        {
            var index = plan.Words.FindIndex(w => w.Day == day);
            plan.Words.RemoveAll(w => w.Day == day);
            foreach (var w in words) w.Day = day;
            plan.Words.InsertRange(index < 0 ? plan.Words.Count : index, words);
            if (title.Length > 0) plan.DayTitles[day] = title;
        });
        RegenPanel.Visibility = Visibility.Collapsed;
        RegenBox.Clear();
        RefreshDays(day);
        Status($"Đã thay {words.Count} từ cho Ngày {day}.");
    }

    private void CancelRegen_Click(object sender, RoutedEventArgs e)
    {
        RegenPanel.Visibility = Visibility.Collapsed;
        RegenBox.Clear();
    }

    private void ExportPlan_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName(plan.Name) + ".md", Filter = "Markdown (*.md)|*.md|Tất cả (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, PlanFormat.ToMarkdown(plan));
        Status($"Đã xuất ra {dialog.FileName}. File này nhập lại được ở tab “Tạo chủ đề mới”.");
    }

    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        if (EditedPlan is not { } plan) return;
        if (System.Windows.MessageBox.Show(this, $"Xóa hẳn “{plan.Name}” cùng {plan.Words.Count} từ và toàn bộ tiến độ của chúng?\n\nNên “Xuất ra file…” trước nếu muốn giữ bản sao.",
                "Xóa lộ trình", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _store.Update(d => CourseEngine.DeletePlan(d, plan.Id));
        RefreshPlanBox();
        Status($"Đã xóa “{plan.Name}”.");
    }

    // =============================== create ===============================

    private PlanRequest CurrentRequest()
    {
        var topic = TopicBox.Text.Trim();
        var days = int.TryParse(DaysBox.Text, out var d) ? Math.Clamp(d, 1, 60) : 7;
        var perDay = int.TryParse(PerDayBox.Text, out var p) ? Math.Clamp(p, 5, 50) : 20;
        var level = (LevelBox.SelectedItem as ComboBoxItem)?.Content as string ?? "B1–B2";
        var name = _nameEdited && NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : $"{topic} — {days} ngày × {perDay} từ";
        return new PlanRequest(topic, days, perDay, level, ExtraBox.Text.Trim(), name);
    }

    private void CreateInput_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == NameBox && !_loading) _nameEdited = NameBox.Text.Trim().Length > 0;
        _promptCopied = false;
        RefreshPrompt();
        UpdateGuide();
    }

    private void RefreshPrompt()
    {
        if (PromptBox is null || CopyPromptButton is null) return;
        var request = CurrentRequest();
        PromptBox.Text = request.Topic.Length > 0 ? PlanFormat.BuildPrompt(request) : "Nhập chủ đề ở bước 1 để tạo prompt.";
        CopyPromptButton.IsEnabled = request.Topic.Length > 0;
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCopy(PromptBox.Text)) { PromptBox.Focus(); PromptBox.SelectAll(); }
        Status(TryCopyStatus);
        _promptCopied = true;
        UpdateGuide();
    }

    // ---- guide ----

    private static readonly Brush GuideNow = Frozen(0x6C, 0x5C, 0xE7), GuideLine = Frozen(0xE6, 0xE3, 0xF5);

    private void ShowGuide(bool show)
    {
        GuidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowGuideButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HideGuide_Click(object sender, RoutedEventArgs e)
    {
        ShowGuide(false);
        _store.Update(d => d.Settings.ShowCreateGuide = false);
    }

    private void ShowGuide_Click(object sender, RoutedEventArgs e)
    {
        ShowGuide(true);
        _store.Update(d => d.Settings.ShowCreateGuide = true);
    }

    private void OpenAi_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Status($"Không mở được trình duyệt: {ex.Message}"); }
    }

    /// <summary>Marks finished steps with ✓ and highlights the step to do now.</summary>
    private void UpdateGuide()
    {
        if (_loading || GuideState1 is null) return;
        var hasTopic = TopicBox.Text.Trim().Length > 0;
        var pasted = ResultBox.Text.Trim().Length > 0;
        var ready = ImportButton.IsEnabled;
        // Pasting (an AI answer or plans copied from another computer) covers the earlier steps.
        var done = new[] { hasTopic || pasted, (hasTopic && _promptCopied) || pasted, pasted, false };
        var now = Array.IndexOf(done, false);
        var hints = new[]
        {
            "● Bắt đầu ở đây: nhập chủ đề ở ô 1",
            "● Bấm “Sao chép prompt” ở ô 2",
            "● Dán prompt vào AI, chờ AI trả lời xong",
            ready ? "● Bấm “Nhập vào khóa học”" : pasted ? "● Bấm “Xem trước” để kiểm tra" : "● Dán câu trả lời vào ô 3"
        };
        var cards = new[] { GuideCard1, GuideCard2, GuideCard3, GuideCard4 };
        var states = new[] { GuideState1, GuideState2, GuideState3, GuideState4 };
        for (var i = 0; i < cards.Length; i++)
        {
            var isNow = i == now;
            cards[i].BorderBrush = isNow ? GuideNow : GuideLine;
            cards[i].BorderThickness = new Thickness(isNow ? 2 : 1);
            states[i].Text = done[i] ? "✓ Xong" : isNow ? hints[i] : "";
            states[i].Foreground = done[i] ? DoneInk : GuideNow;
        }
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "voca-form-mau.md", Filter = "Markdown (*.md)|*.md" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, PlanFormat.Example);
        Status($"Đã lưu form mẫu: {dialog.FileName}");
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Form từ vựng (*.md;*.txt;*.json)|*.md;*.txt;*.json|Tất cả (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        ResultBox.Text = File.ReadAllText(dialog.FileName);
        Preview_Click(sender, e);
    }

    private void ResultBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ImportButton is not null) ImportButton.IsEnabled = false;
        UpdateGuide();
    }

    /// <summary>Count checks only apply when the pasted text answers the prompt on this page.</summary>
    private PlanRequest? ExpectedForPaste() => TopicBox.Text.Trim().Length > 0 ? CurrentRequest() : null;

    /// <summary>
    /// Reads the pasted text. Several plans (text copied from another computer) are imported together and
    /// plans already in the library are skipped; a single plan (an AI answer) is always added.
    /// </summary>
    private (List<FormReadResult> Results, bool Many) ReadPaste()
    {
        var results = Transfer.ReadMany(ResultBox.Text, ExpectedForPaste());
        return (results, results.Count > 1);
    }

    private bool AlreadyInLibrary(Plan plan) => CourseEngine.FindMatchingPlan(Data, plan.Name, plan.Words.Select(w => w.Text)) is not null;

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var (results, many) = ReadPaste();
        PreviewPanel.Visibility = Visibility.Visible;
        var plans = results.Select(r => r.Plan).OfType<Plan>().ToList();
        string Prefix(FormReadResult r) => many ? $"[{(r.Plan?.Name is { Length: > 0 } n ? n : "?")}] " : "";
        var errors = results.SelectMany(r => r.Errors.Select(x => Prefix(r) + x)).ToList();
        var warnings = results.SelectMany(r => r.Warnings.Select(x => Prefix(r) + x)).ToList();
        if (many)
        {
            var existing = plans.Where(AlreadyInLibrary).Select(p => p.Name).ToList();
            if (existing.Count > 0) warnings.Insert(0, $"Đã có trong thư viện, sẽ bỏ qua: {string.Join(", ", existing)}.");
        }
        else if (plans.Count == 1 && AlreadyInLibrary(plans[0]))
        {
            warnings.Insert(0, "Thư viện đã có lộ trình giống lộ trình này (cùng tên hoặc phần lớn từ trùng). Nhập vẫn tạo thêm một lộ trình mới.");
        }

        PreviewTitle.Text = plans.Count == 0 ? "Không đọc được nội dung"
            : many ? $"{plans.Count} lộ trình · {plans.Sum(p => p.Words.Count)} từ"
            : $"{(plans[0].Name.Length > 0 ? plans[0].Name : "(chưa có tên)")}  ·  {(plans[0].Level.Length > 0 ? plans[0].Level + " · " : "")}{plans[0].DayCount} ngày · {plans[0].Words.Count} từ";
        ErrorsBox.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorsText.Text = string.Join("\n", errors.Select(x => "• " + x));
        WarningsBox.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningsText.Text = string.Join("\n", warnings.Select(x => "• " + x));
        var perDay = many ? null : ExpectedForPaste()?.WordsPerDay;
        PreviewDays.ItemsSource = many
            ? plans.Select(p => new PreviewDay(p.Name + (AlreadyInLibrary(p) ? "  (đã có — bỏ qua)" : ""), $"{p.DayCount} ngày · {p.Words.Count} từ",
                AlreadyInLibrary(p) ? WarnInk : DoneInk, string.Join("   |   ", p.Words.Take(3).Select(w => $"{w.Text} · {w.Meaning}")))).ToList()
            : plans.SelectMany(plan => plan.Words.GroupBy(w => w.Day).OrderBy(g => g.Key).Select(g =>
            {
                var title = plan.DayTitle(g.Key);
                return new PreviewDay($"Ngày {g.Key}{(title.Length > 0 ? " · " + title : "")}", $"{g.Count()} từ",
                    perDay is int n && g.Count() != n ? WarnInk : DoneInk,
                    string.Join("   |   ", g.Take(3).Select(w => $"{w.Text} {w.Phonetic} · {w.Meaning}")));
            })).ToList();
        var canImport = results.All(r => r.CanImport) && (!many || plans.Any(p => !AlreadyInLibrary(p)));
        ImportButton.IsEnabled = canImport;
        UpdateGuide();
        Status(canImport
            ? warnings.Count > 0 ? "Có cảnh báo nhưng vẫn nhập được. Sửa trong ô trên rồi xem trước lại nếu muốn." : "Mọi thứ ổn. Bấm “Nhập vào khóa học”."
            : errors.Count > 0 ? "Còn lỗi, sửa rồi bấm “Xem trước” lại." : "Không có lộ trình mới để nhập.");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var (results, many) = ReadPaste();
        if (!results.All(r => r.CanImport)) { Preview_Click(sender, e); return; }
        var plans = results.Select(r => r.Plan!).Where(p => !many || !AlreadyInLibrary(p)).ToList();
        if (plans.Count == 0) { Preview_Click(sender, e); return; }
        _store.Update(d =>
        {
            foreach (var plan in plans)
            {
                d.Plans.Add(plan);
                CourseEngine.AddToCourse(d, plan.Id);
            }
        });
        ResultBox.Clear();
        PreviewPanel.Visibility = Visibility.Collapsed;
        RefreshPlanBox(select: plans[0]);
        Tabs.SelectedItem = CourseTab;
        Status(plans.Count == 1
            ? $"Đã thêm “{plans[0].Name}” ({plans[0].DayCount} ngày, {plans[0].Words.Count} từ) vào cuối khóa học."
            : $"Đã thêm {plans.Count} lộ trình ({plans.Sum(p => p.Words.Count)} từ) vào cuối khóa học: {string.Join(", ", plans.Select(p => p.Name))}.");
    }

    private void Export_Click(object sender, RoutedEventArgs e) => new ExportWindow(_store) { Owner = this }.ShowDialog();

    private void ImportOther_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"File Voca (*{Transfer.Extension};*.md;*.json;*.txt)|*{Transfer.Extension};*.md;*.json;*.txt|Tất cả (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!dialog.FileName.EndsWith(Transfer.Extension, StringComparison.OrdinalIgnoreCase))
        {
            // Plain text forms go through the normal preview so the learner sees what will be added.
            ResultBox.Text = File.ReadAllText(dialog.FileName);
            Tabs.SelectedItem = CreateTab;
            Preview_Click(sender, e);
            return;
        }
        VocaPackage package;
        try { package = Transfer.Load(dialog.FileName); }
        catch (Exception ex) { Status($"Không mở được file: {ex.Message}"); return; }

        var fresh = package.Plans.Count(p => CourseEngine.FindMatchingPlan(Data, p.Name, p.Words.Select(w => w.Text)) is null);
        var message = $"File có {package.Plans.Count} lộ trình ({package.Plans.Sum(p => p.Words.Count)} từ), xuất lúc {package.ExportedAt:HH:mm dd/MM/yyyy}.\n" +
                      $"• {fresh} lộ trình mới sẽ được thêm.\n• {package.Plans.Count - fresh} lộ trình đã có sẽ không bị trùng" +
                      (package.IncludesProgress ? " (chỉ cập nhật tiến độ nếu tiến độ trong file mới hơn)." : ".");
        var restore = false;
        if (package.IncludesProgress && package.Position is not null)
        {
            var answer = System.Windows.MessageBox.Show(this, message + "\n\nFile có kèm vị trí đang học. Chuyển vị trí học theo máy kia?\n(Yes: theo file · No: giữ vị trí hiện tại)",
                "Nhập từ máy khác", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            restore = answer == MessageBoxResult.Yes;
        }
        else if (System.Windows.MessageBox.Show(this, message + "\n\nNhập?", "Nhập từ máy khác", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        TransferResult? result = null;
        _store.Update(d => result = Transfer.Import(d, package, restore));
        RefreshPlanBox(select: CourseEngine.Current(Data)?.Plan);
        Status($"Đã nhập: thêm {result!.PlansAdded} lộ trình{(result.AddedNames.Length > 0 ? $" ({result.AddedNames})" : "")}, " +
               $"{result.PlansMatched} lộ trình đã có, cập nhật tiến độ {result.WordsUpdated} từ" + (result.PositionRestored ? ", vị trí học theo file." : "."));
    }

    // =============================== settings ===============================

    private void LoadSettings()
    {
        var s = Data.Settings;
        DisplayModeBox.SelectedItem = DisplayModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.DisplayMode) ?? DisplayModeBox.Items[0];
        RotationBox.Text = s.RotationSeconds.ToString();
        RevealBox.Text = s.RevealSeconds.ToString();
        MaxReviewsBox.Text = s.MaxReviewsPerDay.ToString();
        MorningReminderBox.IsChecked = s.MorningReminder;
        ReminderHourBox.ItemsSource = new[] { "Tắt" }.Concat(Enumerable.Range(16, 8).Select(h => $"{h}:00")).ToList();
        ReminderHourBox.SelectedItem = s.ReminderHour is >= 16 and <= 23 ? $"{s.ReminderHour}:00" : "Tắt";
        StartWithWindowsBox.IsChecked = WindowsStartupService.IsEnabled();
        LoadPillStyle(s.Pill);
        VersionText.Text = $"Đang dùng: Voca {Updater.Current.ToString(3)}";
        DataPathText.Text =$"Toàn bộ dữ liệu (lộ trình, tiến độ, cài đặt) nằm trong {Path.Combine(_store.Folder, "voca.json")}. Mỗi lần lưu giữ một bản .bak.";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RotationBox.Text, out var rotation) || rotation < 5) { Status("Thời gian đổi từ tối thiểu là 5 giây."); return; }
        if (!int.TryParse(RevealBox.Text, out var reveal) || reveal < 1 || reveal >= rotation) { Status("Thời gian hiện đáp án phải từ 1 giây và ngắn hơn thời gian đổi từ."); return; }
        if (!int.TryParse(MaxReviewsBox.Text, out var maxReviews) || maxReviews is < 0 or > 500) { Status("Số từ ôn tối đa mỗi ngày phải từ 0 đến 500."); return; }
        var hourText = ReminderHourBox.SelectedItem as string ?? "Tắt";
        var hour = hourText == "Tắt" ? -1 : int.Parse(hourText.Split(':')[0]);
        try
        {
            WindowsStartupService.SetEnabled(StartWithWindowsBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            Status($"Không đặt được chạy cùng Windows: {ex.Message}");
            return;
        }
        _store.Update(d =>
        {
            d.Settings.DisplayMode = (DisplayModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? DisplayModes.Word;
            d.Settings.RotationSeconds = rotation;
            d.Settings.RevealSeconds = reveal;
            d.Settings.MaxReviewsPerDay = maxReviews;
            d.Settings.MorningReminder = MorningReminderBox.IsChecked == true;
            d.Settings.ReminderHour = hour;
        });
        Status($"Đã lưu cài đặt lúc {DateTime.Now:HH:mm:ss}.");
    }

    // ---- look of the taskbar word ----

    private static readonly string[] TextPresets = ["#F7F7FA", "#FFE066", "#7CF0C5", "#8EC9FF", "#FFB3D1", "#FFA94D", "#D9D2FF", "#172033"];
    private static readonly string[] BackgroundPresets = ["#000000", "#1E2130", "#6C5CE7", "#0F766E", "#FFFFFF"];
    private bool _pillReady;

    private void LoadPillStyle(PillStyle style)
    {
        _pillReady = false;
        if (FontBox.ItemsSource is null)
        {
            FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            AddSwatches(TextColorSwatches, TextColorBox, TextPresets);
            AddSwatches(RevealColorSwatches, RevealColorBox, TextPresets);
            AddSwatches(BackgroundSwatches, BackgroundColorBox, BackgroundPresets);
        }
        FontBox.SelectedItem = ((IEnumerable<string>)FontBox.ItemsSource).FirstOrDefault(n => string.Equals(n, style.FontFamily, StringComparison.OrdinalIgnoreCase))
                               ?? "Segoe UI";
        SizeSlider.Value = PillPainter.Size(style);
        WeightBox.SelectedItem = WeightBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == style.FontWeight) ?? WeightBox.Items[1];
        ItalicBox.IsChecked = style.Italic;
        ShadowBox.IsChecked = style.Shadow;
        TextColorBox.Text = style.TextColor;
        RevealColorBox.Text = style.RevealColor;
        BackgroundOnBox.IsChecked = PillPainter.TryParse(style.Background, out _);
        BackgroundColorBox.Text = PillPainter.Normalize(style.Background) ?? "#000000";
        OpacitySlider.Value = Math.Clamp(style.BackgroundOpacity, 10, 100);
        WidthSlider.Value = PillPainter.Width(style);
        _pillReady = true;
        UpdatePillPreview();
    }

    private void AddSwatches(WrapPanel host, System.Windows.Controls.TextBox target, IEnumerable<string> colors)
    {
        foreach (var hex in colors)
        {
            PillPainter.TryParse(hex, out var color);
            var swatch = new System.Windows.Controls.Button
            {
                Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(2),
                Background = new SolidColorBrush(color), BorderBrush = Frozen(0xC9, 0xCC, 0xD8), BorderThickness = new Thickness(1),
                ToolTip = hex
            };
            swatch.Click += (_, _) => target.Text = hex;
            host.Children.Add(swatch);
        }
    }

    /// <summary>The style currently set in the controls; colours that are not valid keep the saved value.</summary>
    private PillStyle ReadPillStyle()
    {
        var saved = Data.Settings.Pill;
        return new PillStyle
        {
            FontFamily = FontBox.SelectedItem as string ?? saved.FontFamily,
            FontSize = SizeSlider.Value,
            FontWeight = (WeightBox.SelectedItem as ComboBoxItem)?.Tag as string ?? saved.FontWeight,
            Italic = ItalicBox.IsChecked == true,
            Shadow = ShadowBox.IsChecked == true,
            TextColor = PillPainter.Normalize(TextColorBox.Text) ?? saved.TextColor,
            RevealColor = PillPainter.Normalize(RevealColorBox.Text) ?? saved.RevealColor,
            Background = BackgroundOnBox.IsChecked == true ? PillPainter.Normalize(BackgroundColorBox.Text) ?? "#000000" : "",
            BackgroundOpacity = (int)OpacitySlider.Value,
            MaxWidth = WidthSlider.Value
        };
    }

    private void PillStyle_Changed(object sender, RoutedEventArgs e) => UpdatePillPreview();

    private void UpdatePillPreview()
    {
        if (!_pillReady) return;
        var style = ReadPillStyle();
        SizeLabel.Text = $"{style.FontSize:0} pt";
        OpacityLabel.Text = $"{style.BackgroundOpacity}%";
        WidthLabel.Text = $"{style.MaxWidth:0} px";
        BackgroundColorBox.IsEnabled = OpacitySlider.IsEnabled = BackgroundSwatches.IsEnabled = BackgroundOnBox.IsChecked == true;
        foreach (var box in new[] { TextColorBox, RevealColorBox, BackgroundColorBox })
            box.BorderBrush = PillPainter.TryParse(box.Text, out _) ? Frozen(0xAB, 0xAD, 0xB3) : WarnInk;

        var word = CourseEngine.Current(Data)?.Plan.Words.FirstOrDefault(w => w.Day == Data.Position.Day);
        var showBack = PreviewBackBox.IsChecked == true;
        PreviewText.Text = showBack
            ? word is { Meaning.Length: > 0 } ? word.Meaning : "chương trình giảng dạy"
            : word?.Text ?? "curriculum";
        PillPainter.Apply(style, PreviewPill, PreviewText, showBack);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not System.Windows.Controls.TextBox box) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (PillPainter.TryParse(box.Text, out var current))
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        box.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void SavePillStyle_Click(object sender, RoutedEventArgs e)
    {
        var invalid = new List<string>();
        if (!PillPainter.TryParse(TextColorBox.Text, out _)) invalid.Add("Màu chữ");
        if (!PillPainter.TryParse(RevealColorBox.Text, out _)) invalid.Add("Màu đáp án");
        if (BackgroundOnBox.IsChecked == true && !PillPainter.TryParse(BackgroundColorBox.Text, out _)) invalid.Add("Màu nền");
        if (invalid.Count > 0) { Status($"{string.Join(", ", invalid)} chưa đúng dạng #RRGGBB (ví dụ #FFE066)."); return; }
        var style = ReadPillStyle();
        _store.Update(d => d.Settings.Pill = style);
        Status($"Đã lưu kiểu chữ lúc {DateTime.Now:HH:mm:ss}. Chữ trên taskbar đã đổi theo.");
    }

    private void ResetPillStyle_Click(object sender, RoutedEventArgs e)
    {
        LoadPillStyle(new PillStyle());
        Status("Đã đặt lại kiểu chữ mặc định trong phần xem trước. Bấm “Lưu kiểu chữ” để áp dụng.");
    }

    // ---- versions ----

    private sealed record VersionRow(UpdateInfo Info, string Name, string Meta, string Notes,
        string Badge, Brush BadgeBrush, Brush BadgeInk, Visibility BadgeVisibility);

    private bool _versionsLoaded;
    private bool _installing;

    private async Task LoadVersionsAsync()
    {
        RefreshVersionsButton.IsEnabled = false;
        UpdateStatusText.Text = "Đang tải danh sách phiên bản…";
        try
        {
            var releases = await Updater.ListAsync(Updater.Http);
            _versionsLoaded = true;
            var current = Updater.Current;
            var latest = releases.FirstOrDefault()?.Version;
            var rows = releases.Select(r => VersionRowFor(r, current, latest)).ToList();
            VersionsList.ItemsSource = rows;
            if (latest is not null && Updater.IsNewer(latest, current)) VersionsList.SelectedItem = rows[0];
            UpdateStatusText.Text = releases.Count == 0 ? "Chưa có bản phát hành nào trên GitHub."
                : latest is not null && Updater.IsNewer(latest, current) ? $"Có bản mới: Voca {latest.ToString(3)}."
                : "Bạn đang dùng bản mới nhất.";
            if (!Updater.Enabled())
                UpdateStatusText.Text += " (Bản này chạy từ thư mục build nên không cài được phiên bản khác — hãy dùng bản trong dist hoặc tải từ GitHub.)";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Không tải được danh sách phiên bản: {ex.Message}";
        }
        finally
        {
            RefreshVersionsButton.IsEnabled = true;
            UpdateInstallButton();
        }
    }

    private static VersionRow VersionRowFor(UpdateInfo release, Version current, Version? latest)
    {
        var isCurrent = release.Version == current;
        var isLatest = release.Version == latest;
        var badge = isCurrent && isLatest ? "Mới nhất · đang dùng" : isCurrent ? "Đang dùng" : isLatest ? "Mới nhất" : "";
        var meta = string.Join(" · ", new[]
        {
            release.PublishedAt?.ToString("dd/MM/yyyy") ?? "",
            release.Size > 0 ? $"{release.Size / 1048576.0:0.0} MB" : ""
        }.Where(t => t.Length > 0));
        var notes = string.Join("  ", release.Notes.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ')).Where(l => l.Length > 0));
        return new VersionRow(release, $"Voca {release.Version.ToString(3)}", meta.Length > 0 ? "   " + meta : "",
            notes, badge, isCurrent ? WaitWash : DoneWash, isCurrent ? WaitInk : DoneInk,
            badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallButton();

    private void UpdateInstallButton()
    {
        var current = Updater.Current;
        var version = (VersionsList.SelectedItem as VersionRow)?.Info.Version;
        InstallVersionButton.Content = version is null ? "Chọn một phiên bản"
            : version == current ? "Đang dùng bản này"
            : Updater.IsNewer(version, current) ? $"⬆ Cập nhật lên {version.ToString(3)}"
            : $"⬇ Chuyển về {version.ToString(3)}";
        InstallVersionButton.IsEnabled = !_installing && version is not null && version != current && Updater.Enabled();
        RefreshVersionsButton.IsEnabled = !_installing;
    }

    private void RefreshVersions_Click(object sender, RoutedEventArgs e) => _ = LoadVersionsAsync();

    /// <summary>Asks, downloads and verifies the chosen version, installs it over this exe and restarts.</summary>
    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not VersionRow row || _installing) return;
        var info = row.Info;
        var target = info.Version.ToString(3);
        var newer = Updater.IsNewer(info.Version, Updater.Current);
        var busy = System.Windows.Application.Current.Windows.OfType<Window>().Any(w => w is SessionWindow or TestWindow);
        var message = (newer ? $"Cập nhật lên Voca {target}?" : $"Chuyển về Voca {target} (bản cũ hơn)?") +
                      "\n\nApp sẽ tải, cài đè rồi tự mở lại sau vài giây. Lộ trình, tiến độ và cài đặt giữ nguyên." +
                      (newer ? "" : "\n\nBản cũ hơn không có các tính năng ra sau nó; dữ liệu riêng của các tính năng đó có thể mất khi bản cũ lưu lại.") +
                      (busy ? "\n\nĐang có phiên học hoặc bài kiểm tra mở — phần chưa xong sẽ mất." : "");
        if (System.Windows.MessageBox.Show(this, message, "Cập nhật Voca", MessageBoxButton.YesNo, MessageBoxImage.Question,
                newer ? MessageBoxResult.Yes : MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _installing = true;
        UpdateInstallButton();
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Đang tải Voca {target}…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value;
                UpdateStatusText.Text = $"Đang tải Voca {target}… {value:P0}";
            });
            var downloaded = await Updater.DownloadAsync(Updater.Http, info, progress);
            UpdateStatusText.Text = "Đang cài đặt, app sẽ tự mở lại…";
            Updater.InstallAndRestart(downloaded);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Không cập nhật được: {ex.Message}";
            _installing = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInstallButton();
        }
    }

    private void ReleasesPage_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) { Status($"Không mở được trình duyệt: {ex.Message}"); }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Folder) { UseShellExecute = true });

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"voca-backup-{DateTime.Now:yyyyMMdd}.json", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.Copy(Path.Combine(_store.Folder, "voca.json"), dialog.FileName, overwrite: true);
        Status($"Đã sao lưu ra {dialog.FileName}. Muốn khôi phục: thoát app, chép file này thành voca.json trong thư mục dữ liệu.");
    }

    private void Stats_Click(object sender, RoutedEventArgs e) => _openStats();

    private void ImportV1_Click(object sender, RoutedEventArgs e)
    {
        if (!VocaV1Import.Available()) { Status("Không tìm thấy dữ liệu Voca 1 trên máy này."); return; }
        if (System.Windows.MessageBox.Show(this, "Lấy lộ trình và tiến độ từ Voca 1? Lộ trình cùng tên chỉ được cập nhật tiến độ, vị trí đang học sẽ theo Voca 1.",
                "Nhập từ Voca 1", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            V1ImportResult? result = null;
            _store.Update(d => result = VocaV1Import.Import(d));
            RefreshPlanBox(select: CourseEngine.Current(Data)?.Plan);
            Status($"Đã nhập từ Voca 1: thêm {result!.PlansAdded} lộ trình{(result.AddedNames is null ? "" : $" ({result.AddedNames})")}, " +
                   $"cập nhật {result.PlansMatched} lộ trình có sẵn, {result.WordsWithProgress} từ có tiến độ, {result.StudyDays} ngày học" +
                   (result.PositionRestored ? ", vị trí đang học theo Voca 1." : "."));
        }
        catch (Exception ex)
        {
            Status($"Không đọc được dữ liệu Voca 1: {ex.Message}");
        }
    }

    // =============================== helpers ===============================

    private string TryCopyStatus = "";

    private bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            TryCopyStatus = "Đã sao chép prompt. Dán vào AI (Claude, ChatGPT, Gemini…), rồi dán câu trả lời vào ô bên dưới.";
            return true;
        }
        catch (Exception)
        {
            TryCopyStatus = "Không sao chép tự động được (clipboard đang bận). Prompt đã được bôi đen, hãy nhấn Ctrl+C.";
            return false;
        }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
