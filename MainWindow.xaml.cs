using System.Drawing;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Windows.Input;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using Voca.Models;
using Voca.Services;

namespace Voca;

/// <summary>The word on the taskbar, its detail popup, reminders, and the entry point to every other window.</summary>
public partial class MainWindow : Window
{
    private readonly Store _store;
    private readonly SpeechSynthesizer _speaker = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _revealTimer = new();
    private readonly NotifyIcon _tray;
    private readonly HashSet<string> _notified = [];
    private List<Word> _today = [];
    private HashSet<Word> _fresh = [];
    private int _index;
    private DateTime _loadedDate = DateTime.MinValue;
    private bool _revealed;
    private bool _reloading;
    private bool _summaryOpen;
    private LibraryWindow? _libraryWindow;
    private StatsWindow? _statsWindow;
    private SessionWindow? _sessionWindow;
    /// <summary>What clicking the last tray balloon does (start the session, or install an update).</summary>
    private Action _balloonAction;
    private System.Windows.Point _dragStart;
    private DateTime _mouseDownAt;
    private bool _dragging;

    private AppData _data => _store.Data;

    public MainWindow(Store store)
    {
        _store = store;
        InitializeComponent();
        _tray = new NotifyIcon { Icon = LoadAppIcon(), Text = "Voca", Visible = true };
        _tray.DoubleClick += (_, _) => OpenLibrary();
        _balloonAction = OpenSession;
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(_balloonAction);
        _tray.ContextMenuStrip = BuildTrayMenu();
        _hasEnglishVoice = SelectEnglishVoice();
        _timer.Tick += (_, _) => RotateWord();
        _revealTimer.Tick += (_, _) => { _revealTimer.Stop(); RevealAnswer(); };
        _topmostTimer.Tick += (_, _) => { RefreshForNewDay(); UpdateVisibilityForFullScreen(); KeepAboveTaskbar(); MaybeNotify(); };
        // Any change saved by any window (library, session, stats) refreshes the taskbar word.
        _store.Changed += () => Dispatcher.Invoke(Reload);
        Loaded += (_, _) => { Reload(); RestoreOrSetDefaultPosition(); KeepAboveTaskbar(); _topmostTimer.Start(); SayIfUpdated(); };
        // Pause rotation while the detail popup is open so the word being read stays put.
        DetailPopup.Opened += (_, _) => _timer.Stop();
        DetailPopup.Closed += (_, _) => _timer.Start();
        Closed += (_, _) => { _topmostTimer.Stop(); _tray.Dispose(); _speaker.Dispose(); };
    }

    private readonly bool _hasEnglishVoice;
    private bool _hiddenForFullScreen;

    private static System.Drawing.Icon LoadAppIcon()
    {
        var path = Environment.ProcessPath;
        return (path is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(path)) ?? SystemIcons.Application;
    }

    /// <summary>
    /// On Vietnamese Windows the default voice may be vi-VN, which mangles English words.
    /// Prefer an installed en-US voice, then any English voice.
    /// </summary>
    private bool SelectEnglishVoice()
    {
        var voices = _speaker.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        var english = voices.FirstOrDefault(v => v.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                      ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "en");
        if (english is null) return false;
        _speaker.SelectVoice(english.Name);
        return true;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Bắt đầu phiên học", null, (_, _) => Dispatcher.Invoke(OpenSession));
        menu.Items.Add("Tạo bài kiểm tra", null, (_, _) => Dispatcher.Invoke(OpenTest));
        menu.Items.Add("Danh sách từ sai", null, (_, _) => Dispatcher.Invoke(OpenMistakes));
        menu.Items.Add("Cập nhật phiên bản…", null, (_, _) => Dispatcher.Invoke(OpenUpdates));
        menu.Items.Add("Thư viện và cài đặt", null, (_, _) => Dispatcher.Invoke(OpenLibrary));
        menu.Items.Add("Thống kê học tập", null, (_, _) => Dispatcher.Invoke(OpenStats));
        menu.Items.Add("Thoát", null, (_, _) => Dispatcher.Invoke(() => { _tray.Visible = false; Close(); System.Windows.Application.Current.Shutdown(); }));
        return menu;
    }

    public void Reload()
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            var today = DateTime.Today;
            var moved = CourseEngine.Advance(_data, today);
            var cleaned = MistakeDays.Cleanup(_data, today);
            var tidied = MistakeDays.Tidy(_data, DateTime.Now);
            if (moved || cleaned || tidied) _store.Save();
            var dateChanged = _loadedDate != today;
            var currentId = !dateChanged && _index < _today.Count ? _today[_index].Id : (Guid?)null;
            var list = CourseEngine.BuildToday(_data, today, out _fresh);
            // Words answered wrong today come first so they get the most exposure on the taskbar.
            _today = list.OrderByDescending(w => ReviewScheduler.IsWrongToday(w, today)).ToList();
            var sameIndex = currentId is Guid id ? _today.FindIndex(w => w.Id == id) : -1;
            _index = sameIndex >= 0 ? sameIndex : dateChanged ? 0 : Math.Clamp(_index, 0, Math.Max(0, _today.Count - 1));
            _loadedDate = today;
            var rotation = Math.Clamp(_data.Settings.RotationSeconds, 5, 3600);
            _timer.Interval = TimeSpan.FromSeconds(rotation);
            // The answer must appear before the next word does.
            _revealTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_data.Settings.RevealSeconds, 1, Math.Max(1, rotation - 2)));
            if (!DetailPopup.IsOpen) _timer.Start();
            ApplyPillSize();
            Render();
            _statsWindow?.Refresh();
            if (_data.Position.PendingSummaryPlanId is Guid finished)
                Dispatcher.BeginInvoke(() => ShowWeekSummary(finished));
            MaybeNotify();
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>Resizes the pill window for the chosen font size and width, keeping it on the taskbar.</summary>
    private void ApplyPillSize()
    {
        var style = _data.Settings.Pill;
        var height = PillPainter.WindowHeight(style);
        var width = PillPainter.Width(style);
        if (Math.Abs(Height - height) < 0.5 && Math.Abs(MaxWidth - width) < 0.5) return;
        var bottom = Top + Height;
        MaxWidth = width;
        Height = height;
        if (!IsLoaded) return;
        if (_data.Settings.PillLeft is null) PositionNearTaskbar();
        else Top = Math.Max(0, bottom - height);
    }

    private bool IsFlashcardMode => _data.Settings.DisplayMode is DisplayModes.Flash or DisplayModes.Reverse;

    /// <summary>Front and back of the taskbar card for the current display mode.</summary>
    private (string Front, string Back) CardSides(Word item)
    {
        var meaning = string.IsNullOrWhiteSpace(item.Meaning) ? item.Phonetic : item.Meaning;
        return _data.Settings.DisplayMode == DisplayModes.Reverse && !string.IsNullOrWhiteSpace(meaning)
            ? (meaning, item.Text)
            : (item.Text, meaning);
    }

    private void RenderPill()
    {
        if (_today.Count == 0) return;
        var (front, back) = CardSides(_today[_index]);
        var showBack = IsFlashcardMode && _revealed && !string.IsNullOrWhiteSpace(back);
        WordText.Text = showBack ? back : front;
        PillPainter.Apply(_data.Settings.Pill, WordPill, WordText, showBack);
        WordPill.ToolTip = IsFlashcardMode ? $"{front} — {back}" : null;
    }


    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void RevealAnswer()
    {
        if (_revealed || !IsFlashcardMode) return;
        _revealed = true;
        RenderPill();
    }

    private void WordPill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => RevealAnswer();


    private void RefreshForNewDay()
    {
        if (_loadedDate == DateTime.Today) return;
        _notified.Clear();
        Reload();
    }

    /// <summary>What to show when today has no words, depending on where the learner is in the course.</summary>
    private (string Pill, string Title, string Detail) EmptyState()
    {
        if (CourseEngine.Current(_data) is null)
            return ("Thêm chủ đề ⚙", "Khóa học đang trống", "Mở Thư viện (⚙) để tạo chủ đề mới hoặc thêm lộ trình vào khóa học.");
        if (_data.Position.Finished)
            return ("Hoàn thành 🎉", "Đã học hết khóa học", "Mở Thư viện (⚙) để tạo chủ đề mới. Từ cũ vẫn được ôn khi đến hạn.");
        return ("Nghỉ hôm nay", $"Ngày {_data.Position.Day} trống", "Ngày này chưa có từ. Thêm từ trong Thư viện.");
    }

    /// <summary>Reminds once when the day starts and once at the evening hour if today's session is not done.</summary>
    private void MaybeNotify()
    {
        if (_data.Position is not { Finished: false, DayCompleted: false } position || CourseEngine.Current(_data) is not var (plan, _)) return;
        if (_today.Count == 0) return;
        var now = DateTime.Now;
        var settings = _data.Settings;
        var evening = settings.ReminderHour is >= 0 and <= 23 && now.Hour >= settings.ReminderHour;
        if (!evening && !settings.MorningReminder) return;
        if (!_notified.Add($"{now:yyyy-MM-dd}-{(evening ? "evening" : "day")}")) return;
        var reviews = _today.Count - _fresh.Count;
        var mistakeDay = MistakeDays.Active(_data, DateTime.Today) is not null;
        _balloonAction = OpenSession;
        _tray.ShowBalloonTip(15000, mistakeDay ? "Voca · Ngày học từ sai" : $"Voca · {plan.Name} · Ngày {position.Day}/{plan.DayCount}",
            $"{(evening ? "Bạn chưa học hôm nay. " : "")}{_fresh.Count} {(mistakeDay ? "từ sai" : "từ mới")} · {reviews} từ cần ôn. Bấm để bắt đầu.", ToolTipIcon.None);
    }

    private void ShowWeekSummary(Guid finishedPlanId)
    {
        if (_summaryOpen) return;
        _summaryOpen = true;
        var window = new WeekSummaryWindow(_data, finishedPlanId);
        window.Closed += (_, _) =>
        {
            _summaryOpen = false;
            _store.Update(d => { if (d.Position.PendingSummaryPlanId == finishedPlanId) d.Position.PendingSummaryPlanId = null; });
        };
        window.Show();
        window.Activate();
    }

    private void RotateWord()
    {
        if (_loadedDate != DateTime.Today)
        {
            RefreshForNewDay();
            return;
        }
        // Automatic rotation skips words already answered correctly today, so attention goes to the rest.
        Move(1, skipKnownToday: true);
    }

    private void Render()
    {
        _revealTimer.Stop();
        _revealed = false;
        RenderCourse();
        if (_today.Count == 0)
        {
            var empty = EmptyState();
            WordText.Text = empty.Pill;
            PillPainter.Apply(_data.Settings.Pill, WordPill, WordText, showBack: false);
            WordPill.ToolTip = null;
            DetailWord.Text = empty.Title;
            KindBadge.Visibility = Visibility.Collapsed;
            PhoneticText.Text = "";
            PartOfSpeechText.Text = "";
            MeaningText.Text = empty.Detail;
            ExamplePanel.Visibility = Visibility.Collapsed;
            ReviewInfoText.Text = "";
            ProgressText.Text = StreakText();
            return;
        }
        var item = _today[_index];
        RenderPill();
        if (IsFlashcardMode) _revealTimer.Start();

        DetailWord.Text = item.Text;
        var isReview = !_fresh.Contains(item);
        var mistakeDay = !isReview && MistakeDays.Active(_data, DateTime.Today) is not null;
        var dayTitle = !isReview && CourseEngine.Current(_data) is var (plan, _) ? plan.DayTitle(item.Day) : "";
        KindBadge.Visibility = Visibility.Visible;
        KindText.Text = isReview ? "ÔN TẬP"
            : mistakeDay ? $"TỪ SAI · SAI {_data.Mistakes.FirstOrDefault(m => m.WordId == item.Id)?.Times ?? 1} LẦN"
            : dayTitle.Length == 0 ? "TỪ MỚI" : $"TỪ MỚI · {dayTitle}";
        KindBadge.Background = isReview ? ReviewBadgeBrush : mistakeDay ? MistakeBadgeBrush : NewBadgeBrush;
        PhoneticText.Text = item.Phonetic;
        PartOfSpeechText.Text = item.PartOfSpeech;
        MeaningText.Text = item.Meaning;
        ExampleText.Text = item.Example;
        ExamplePanel.Visibility = string.IsNullOrWhiteSpace(item.Example) ? Visibility.Collapsed : Visibility.Visible;
        RenderReviewInfo(item);

        var parts = new List<string> { $"{_index + 1} / {_today.Count}" };
        var streak = StreakText();
        if (streak.Length > 0) parts.Add(streak);
        ProgressText.Text = string.Join("  ·  ", parts);
    }

    private static readonly System.Windows.Media.Brush NewBadgeBrush = Frozen(0xEE, 0xF4, 0xFF);
    private static readonly System.Windows.Media.Brush ReviewBadgeBrush = Frozen(0xFF, 0xF4, 0xE5);
    private static readonly System.Windows.Media.Brush MistakeBadgeBrush = Frozen(0xFD, 0xEC, 0xEA);
    private static readonly System.Windows.Media.Brush DotDone = Frozen(0x06, 0x76, 0x47);
    private static readonly System.Windows.Media.Brush DotToday = Frozen(0x6C, 0x5C, 0xE7);
    private static readonly System.Windows.Media.Brush DotLater = Frozen(0xE3, 0xE1, 0xF0);

    /// <summary>Where the learner is in the course, today's workload and the session button.</summary>
    private void RenderCourse()
    {
        RenderMistakePanel();
        if (MistakeDays.Active(_data, DateTime.Today) is { } mistakeDay && _fresh.Count > 0)
        {
            RenderMistakeDay(mistakeDay);
            return;
        }
        if (CourseEngine.Current(_data) is not var (plan, _))
        {
            CoursePanel.Visibility = Visibility.Collapsed;
            return;
        }
        WeekDots.Visibility = Visibility.Visible;
        var position = _data.Position;
        CoursePanel.Visibility = Visibility.Visible;
        CourseEyebrow.Text = $"{plan.Name} · Ngày {position.Day}/{plan.DayCount}".ToUpperInvariant();
        WeekDots.Children.Clear();
        WeekDots.Columns = Math.Max(1, plan.DayCount);
        for (var day = 1; day <= plan.DayCount; day++)
        {
            var done = day < position.Day || (day == position.Day && (position.DayCompleted || position.Finished));
            WeekDots.Children.Add(new System.Windows.Controls.Border
            {
                Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0),
                Background = done ? DotDone : day == position.Day ? DotToday : DotLater
            });
        }
        var title = plan.DayTitle(position.Day);
        var reviews = _today.Count - _fresh.Count;
        var status = position.Finished ? "Đã học hết khóa học. Từ cũ vẫn được ôn khi đến hạn."
            : position.DayCompleted ? "Đã học xong hôm nay ✓ · ngày mai sang ngày tiếp theo"
            : $"Hôm nay: {_fresh.Count} từ mới · {reviews} từ cần ôn";
        CourseStatusText.Text = title.Length > 0 && !position.Finished ? $"{title}\n{status}" : status;
        SessionButton.Content = position.DayCompleted || position.Finished ? "Học lại / ôn tập" : "Bắt đầu phiên học";
    }

    /// <summary>Today is a mistake day: the course panel shows it instead of the course day.</summary>
    private void RenderMistakeDay(MistakeDay day)
    {
        CoursePanel.Visibility = Visibility.Visible;
        WeekDots.Visibility = Visibility.Collapsed;
        CourseEyebrow.Text = $"NGÀY HỌC TỪ SAI · {_fresh.Count} TỪ";
        var reviews = _today.Count - _fresh.Count;
        var course = CourseEngine.Current(_data) is var (plan, _) && !_data.Position.Finished
            ? $"{plan.Name} tạm nghỉ hôm nay, mai học tiếp."
            : "";
        CourseStatusText.Text = (day.Done
            ? $"Đã học xong ngày từ sai ✓ · còn {MistakeDays.Waiting(_data).Count} từ trong danh sách từ sai\n{course}"
            : $"Hôm nay: {_fresh.Count} từ sai · {reviews} từ cần ôn\n{course}").TrimEnd();
        SessionButton.Content = day.Done ? "Học lại / ôn tập" : "Bắt đầu học từ sai";
    }

    /// <summary>Suggests a mistake day, or shows the one scheduled, with today / tomorrow / cancel.</summary>
    private void RenderMistakePanel()
    {
        var today = DateTime.Today;
        var active = MistakeDays.Active(_data, today);
        var upcoming = MistakeDays.Upcoming(_data, today);
        var waiting = MistakeDays.Waiting(_data).Count;
        MistakesLink.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        MistakesLink.Content = $"📋 {waiting} từ sai · Xem danh sách và học lại";
        void Buttons(string? todayText, string? tomorrowText, bool cancel)
        {
            MistakeTodayButton.Visibility = todayText is null ? Visibility.Collapsed : Visibility.Visible;
            MistakeTodayButton.Content = todayText ?? "";
            MistakeTomorrowButton.Visibility = tomorrowText is null ? Visibility.Collapsed : Visibility.Visible;
            MistakeTomorrowButton.Content = tomorrowText ?? "";
            MistakeCancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        }
        MistakePanel.Visibility = Visibility.Visible;
        if (active is { Done: false } && _fresh.Count > 0)
        {
            MistakeText.Text = "📌 Hôm nay là ngày học từ sai. Muốn học lộ trình như bình thường thì dời hoặc hủy.";
            Buttons(null, "Dời sang ngày mai", cancel: true);
        }
        else if (upcoming is not null)
        {
            var when = upcoming.Date.Date == today.AddDays(1) ? "ngày mai" : $"ngày {upcoming.Date:dd/MM}";
            MistakeText.Text = $"📌 Đã hẹn học {MistakeDays.WordsOf(_data, upcoming).Count} từ sai vào {when}. Lộ trình nghỉ ngày đó, hôm sau học tiếp.";
            Buttons("Học ngay hôm nay", null, cancel: true);
        }
        else
        {
            MistakePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void MistakesLink_Click(object sender, RoutedEventArgs e) => OpenMistakes();

    private void MistakeToday_Click(object sender, RoutedEventArgs e) =>
        _store.Update(d => MistakeDays.Schedule(d, DateTime.Today, DateTime.Today));

    private void MistakeTomorrow_Click(object sender, RoutedEventArgs e) =>
        _store.Update(d => MistakeDays.Schedule(d, DateTime.Today.AddDays(1), DateTime.Today));

    private void MistakeCancel_Click(object sender, RoutedEventArgs e) => _store.Update(MistakeDays.Cancel);

    private void RenderReviewInfo(Word item)
    {
        var today = DateTime.Today;
        if (ReviewScheduler.IsRatedToday(item, today))
        {
            var days = (item.Review!.Due.Date - today).Days;
            ReviewInfoText.Text = (item.Review.LastKnown ? "Hôm nay trả lời đúng · " : "Hôm nay trả lời sai · ") +
                                  (days <= 1 ? "ôn lại ngày mai" : $"ôn lại sau {days} ngày");
        }
        else if (item.Review is { } state)
        {
            ReviewInfoText.Text = $"Đúng {state.Reps} lần liên tiếp · sai {item.ForgotCount} lần";
        }
        else
        {
            ReviewInfoText.Text = "Từ mới · sẽ được kiểm tra trong phiên học";
        }
    }

    private string StreakText()
    {
        var stats = StatsService.Compute(_data, DateTime.Today, days: 1);
        return stats.CurrentStreak > 0 ? $"🔥 {stats.CurrentStreak} ngày liên tiếp" : "";
    }

    private void Session_Click(object sender, RoutedEventArgs e) { DetailPopup.IsOpen = false; OpenSession(); }

    private void OpenSession()
    {
        DetailPopup.IsOpen = false;
        if (_sessionWindow is not null)
        {
            if (_sessionWindow.WindowState == WindowState.Minimized) _sessionWindow.WindowState = WindowState.Normal;
            _sessionWindow.Activate();
            return;
        }
        Reload();
        var fresh = _today.Where(_fresh.Contains).ToList();
        var reviews = _today.Where(w => !_fresh.Contains(w)).ToList();
        var mistakeDay = MistakeDays.Active(_data, DateTime.Today) is not null && fresh.Count > 0;
        var courseText = mistakeDay ? $"Ngày học từ sai · {fresh.Count} từ"
            : CourseEngine.Current(_data) is var (plan, _) ? $"{plan.Name} · Ngày {_data.Position.Day}/{plan.DayCount}" : "";
        _sessionWindow = new SessionWindow(_data, fresh, reviews, courseText, FinishSession, Speak,
            mistakeDay ? SessionMode.MistakeDay : SessionMode.Course);
        _sessionWindow.Closed += (_, _) => _sessionWindow = null;
        _sessionWindow.Show();
        _sessionWindow.Activate();
    }

    private void FinishSession(IReadOnlyList<(Word Word, bool Correct)> answers, IReadOnlyCollection<Word> newWords)
    {
        try
        {
            _store.Update(d => CourseEngine.ApplySession(d, answers, newWords, DateTime.Now));
        }
        catch (Exception ex)
        {
            _balloonAction = () => { };
            _tray.ShowBalloonTip(8000, "Voca", $"Không lưu được kết quả: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void Stats_Click(object sender, RoutedEventArgs e) { DetailPopup.IsOpen = false; OpenStats(); }

    private void RestoreOrSetDefaultPosition()
    {
        if (_data.Settings.PillLeft is double left && _data.Settings.PillTop is double top &&
            IsVisibleOnScreen(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        PositionNearTaskbar();
    }

    /// <summary>The window sizes itself to the text, so use the measured width once it is known.</summary>
    private double PillWidth => ActualWidth > 0 ? ActualWidth : MinWidth;

    private bool IsVisibleOnScreen(double left, double top)
    {
        const double visibleEdge = 24;
        return left + PillWidth >= SystemParameters.VirtualScreenLeft + visibleEdge &&
               left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - visibleEdge &&
               top + Height >= SystemParameters.VirtualScreenTop + visibleEdge &&
               top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - visibleEdge;
    }

    private void PositionNearTaskbar()
    {
        var area = SystemParameters.WorkArea;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        // Taskbar rectangles from Win32 are in physical pixels; convert to WPF device-independent pixels.
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var taskbar = TryGetTaskbar(out var edge, out var rect)
            ? new Rect(rect.Left * toDip, rect.Top * toDip, (rect.Right - rect.Left) * toDip, (rect.Bottom - rect.Top) * toDip)
            : Rect.Empty;

        // An auto-hidden taskbar reserves no space, so the work area spans the whole screen.
        var autoHidden = area.Width >= screenWidth - 1 && area.Height >= screenHeight - 1;
        if (taskbar.IsEmpty || autoHidden || edge is AbeLeft or AbeRight || taskbar.Height < Height * 0.75)
        {
            // Vertical or auto-hidden taskbar: no horizontal strip to sit in, so float above the
            // bottom-right corner of the work area instead.
            Left = area.Right - PillWidth - 16;
            Top = area.Bottom - Height - 16;
            return;
        }

        Top = taskbar.Top + Math.Max(0, (taskbar.Height - Height) / 2);
        if (IsWindows11CenteredTaskbar())
        {
            // Empty area between Widgets (far left) and the centered Start button.
            Left = taskbar.Left + 140;
        }
        else
        {
            // Left-aligned icons (Windows 10, or Windows 11 with left alignment): the free space
            // is just left of the notification area.
            var trayLeft = TryGetTrayNotifyLeft(out var trayLeftPx) ? trayLeftPx * toDip : taskbar.Right - 300;
            // Reserve the maximum width: the text grows to the right and must never cover the tray.
            Left = Math.Max(taskbar.Left, trayLeft - MaxWidth - 12);
        }
        Left = Math.Clamp(Left, 0, Math.Max(0, screenWidth - PillWidth));
        Top = Math.Clamp(Top, 0, Math.Max(0, screenHeight - Height));
    }

    private static bool IsWindows11CenteredTaskbar()
    {
        if (Environment.OSVersion.Version.Build < 22000) return false;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        // TaskbarAl: 0 = left, 1 or missing = center (Windows 11 default).
        return key?.GetValue("TaskbarAl") is not int alignment || alignment != 0;
    }

    private static bool TryGetTaskbar(out uint edge, out NativeRect rect)
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        var ok = SHAppBarMessage(AbmGetTaskbarPos, ref data) != IntPtr.Zero;
        edge = data.uEdge;
        rect = data.rc;
        return ok;
    }

    private static bool TryGetTrayNotifyLeft(out double left)
    {
        left = 0;
        var tray = FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var rect)) return false;
        left = rect.Left;
        return true;
    }

    private void KeepAboveTaskbar()
    {
        if (_hiddenForFullScreen) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>
    /// Being forced topmost every 2 seconds would otherwise draw the word over full-screen
    /// videos, games and presentations. Hide while one is in front, show again afterwards.
    /// </summary>
    private void UpdateVisibilityForFullScreen()
    {
        var fullScreen = IsFullScreenAppInFront();
        if (fullScreen == _hiddenForFullScreen) return;
        _hiddenForFullScreen = fullScreen;
        if (fullScreen)
        {
            DetailPopup.IsOpen = false;
            Hide();
        }
        else
        {
            Show();
        }
    }

    private bool IsFullScreenAppInFront()
    {
        if (SHQueryUserNotificationState(out var state) == 0 &&
            state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode)
            return true;

        // Borderless "full screen" (e.g. F11 video in a browser) is not always reported above,
        // so also check whether the foreground window covers its whole monitor.
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == new WindowInteropHelper(this).Handle) return false;
        var className = new System.Text.StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;
        if (!GetWindowRect(foreground, out var window)) return false;
        // Only a window covering the monitor the word sits on matters; on a second monitor without a
        // taskbar, an ordinary maximized window also covers the whole screen.
        var monitor = Screen.FromHandle(foreground).Bounds;
        if (monitor != Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds) return false;
        return window.Left <= monitor.Left && window.Top <= monitor.Top &&
               window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbeLeft = 0, AbeRight = 2;
    private const int QunsBusy = 2, QunsRunningD3DFullScreen = 3, QunsPresentationMode = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    private void Move(int delta, bool skipKnownToday = false)
    {
        if (_today.Count == 0) return;
        var next = (_index + delta + _today.Count) % _today.Count;
        if (skipKnownToday)
        {
            var today = DateTime.Today;
            for (var step = 0; step < _today.Count && ReviewScheduler.IsKnownToday(_today[next], today); step++)
                next = (next + delta + _today.Count) % _today.Count;
            // If every word is known today, the loop ends where it started and simply keeps rotating.
        }
        _index = next;
        Render();
    }

    private void WordPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _mouseDownAt = DateTime.UtcNow;
        _dragging = false;
        WordPill.CaptureMouse();
        e.Handled = true;
    }

    private void WordPill_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !WordPill.IsMouseCaptured || _dragging)
            return;

        var current = e.GetPosition(this);
        var movedFarEnough = Math.Abs(current.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                             Math.Abs(current.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
        var heldLongEnough = DateTime.UtcNow - _mouseDownAt >= TimeSpan.FromMilliseconds(180);
        if (!movedFarEnough || !heldLongEnough)
            return;

        _dragging = true;
        WordPill.ReleaseMouseCapture();
        DetailPopup.IsOpen = false;

        try
        {
            DragMove();
            _store.Update(d => { d.Settings.PillLeft = Left; d.Settings.PillTop = Top; });
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released just before DragMove starts.
        }
    }

    private void WordPill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (WordPill.IsMouseCaptured)
            WordPill.ReleaseMouseCapture();

        if (!_dragging)
        {
            Render();
            // Center the 310px popup above the pill, whose width follows the text.
            DetailPopup.HorizontalOffset = (WordPill.ActualWidth - 310) / 2;
            DetailPopup.IsOpen = true;
        }

        _dragging = false;
        e.Handled = true;
    }

    private void WordPill_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenLibrary();
    private void Previous_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Settings_Click(object sender, RoutedEventArgs e) { DetailPopup.IsOpen = false; OpenLibrary(); }
    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (_today.Count > 0) Speak(_today[_index].Text);
    }

    private void Speak(string text)
    {
        if (!_hasEnglishVoice)
            ProgressText.Text = "Chưa có giọng đọc tiếng Anh — cài gói ngôn ngữ English (United States) kèm Speech trong Windows.";
        _speaker.SpeakAsyncCancelAll();
        _speaker.Rate = -1;
        _speaker.SpeakAsync(text);
    }

    private void OpenTest()
    {
        DetailPopup.IsOpen = false;
        new TestWindow(_store, null, null, null, Actions).Show();
    }

    private AppActions Actions => new(Speak, _hasEnglishVoice, OpenStats, OpenMistakes, OpenPractice);

    // ---------------------------------- updates ----------------------------------

    /// <summary>Once after switching version (update or going back): a short tray note.</summary>
    private void SayIfUpdated()
    {
        var current = Updater.Current.ToString(3);
        if (_data.Settings.LastVersion == current) return;
        var hadVersion = Version.TryParse(_data.Settings.LastVersion, out var last);
        _store.Update(d => d.Settings.LastVersion = current);
        if (!hadVersion) return;
        _balloonAction = () => { };
        _tray.ShowBalloonTip(8000, "Voca", Updater.IsNewer(Updater.Current, last!)
            ? $"Đã cập nhật lên Voca {current}." : $"Đã chuyển về Voca {current}.", ToolTipIcon.Info);
    }

    /// <summary>Opens the library on Settings → "Cập nhật phiên bản" (the version list).</summary>
    private void OpenUpdates()
    {
        OpenLibrary();
        _libraryWindow?.ShowUpdates();
    }

    private void Test_Click(object sender, RoutedEventArgs e) => OpenTest();

    /// <summary>Opens the library on its "Từ sai" tab.</summary>
    private void OpenMistakes()
    {
        OpenLibrary();
        _libraryWindow?.ShowMistakes();
    }

    /// <summary>
    /// A session over wrong words outside the course (flash cards, then the quiz). Right answers leave
    /// the mistake lists; the course day is not affected.
    /// </summary>
    private void OpenPractice(IReadOnlyList<Word> words, string title)
    {
        DetailPopup.IsOpen = false;
        if (words.Count == 0) return;
        if (_sessionWindow is not null)
        {
            _sessionWindow.Activate();
            return;
        }
        _sessionWindow = new SessionWindow(_data, words, [], title,
            (answers, _) => _store.Update(d => MistakeDays.ApplyPractice(d, answers, DateTime.Now)), Speak, SessionMode.Practice);
        _sessionWindow.Closed += (_, _) => _sessionWindow = null;
        _sessionWindow.Show();
        _sessionWindow.Activate();
    }

    private void OpenLibrary()
    {
        DetailPopup.IsOpen = false;
        if (_libraryWindow is not null)
        {
            if (_libraryWindow.WindowState == WindowState.Minimized) _libraryWindow.WindowState = WindowState.Normal;
            _libraryWindow.Activate();
            return;
        }
        _libraryWindow = new LibraryWindow(_store, Actions);
        _libraryWindow.Closed += (_, _) => _libraryWindow = null;
        _libraryWindow.Show();
        _libraryWindow.Activate();
    }

    private void OpenStats()
    {
        DetailPopup.IsOpen = false;
        if (_statsWindow is not null)
        {
            if (_statsWindow.WindowState == WindowState.Minimized) _statsWindow.WindowState = WindowState.Normal;
            _statsWindow.Refresh();
            _statsWindow.Activate();
            return;
        }
        _statsWindow = new StatsWindow(_store);
        _statsWindow.Closed += (_, _) => _statsWindow = null;
        _statsWindow.Show();
        _statsWindow.Activate();
    }
}
