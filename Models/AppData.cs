using System.Text.Json.Serialization;

namespace Voca.Models;

/// <summary>Everything Voca stores, in one JSON file. Only <see cref="Services.Store"/> reads or writes it.</summary>
public sealed class AppData
{
    public int DataVersion { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    /// <summary>All plans, in or out of the course.</summary>
    public List<Plan> Plans { get; set; } = [];
    /// <summary>Plan ids in study order.</summary>
    public List<Guid> Course { get; set; } = [];
    public CourseState Position { get; set; } = new();
    /// <summary>Answers per day (key yyyy-MM-dd), used for streaks and statistics.</summary>
    public Dictionary<string, DayLog> StudyLog { get; set; } = [];
    /// <summary>True once the bundled plans have been offered (so deleting them does not bring them back).</summary>
    public bool Seeded { get; set; }
    /// <summary>True once data from Voca 1 has been imported automatically (the manual button still works).</summary>
    public bool ImportedFromV1 { get; set; }
    /// <summary>Words answered wrong (tests and sessions) that are waiting for a mistake day.</summary>
    public List<MistakeEntry> Mistakes { get; set; } = [];
    /// <summary>Wrong words grouped by where they came from (one list per test, one per day of sessions).</summary>
    public List<MistakeList> MistakeLists { get; set; } = [];
    /// <summary>The scheduled mistake day, if any; on that day it replaces the course lesson.</summary>
    public MistakeDay? MistakeDay { get; set; }
}

public sealed class Settings
{
    public int RotationSeconds { get; set; } = 20;
    /// <summary>"word", "flash" (word, then meaning) or "reverse" (meaning, then word).</summary>
    public string DisplayMode { get; set; } = DisplayModes.Word;
    public int RevealSeconds { get; set; } = 6;
    /// <summary>Maximum due review words added to each day; 0 turns reviews off.</summary>
    public int MaxReviewsPerDay { get; set; } = 30;
    /// <summary>Hour (0–23) of the evening reminder when today's session is not done; -1 turns it off.</summary>
    public int ReminderHour { get; set; } = 20;
    public bool MorningReminder { get; set; } = true;
    public double? PillLeft { get; set; }
    public double? PillTop { get; set; }
    /// <summary>The version that last ran, to say "updated to …" once after an update.</summary>
    public string LastVersion { get; set; } = "";
    /// <summary>Show the how-to guide at the top of "Tạo chủ đề mới".</summary>
    public bool ShowCreateGuide { get; set; } = true;
    /// <summary>How the word on the taskbar looks.</summary>
    public PillStyle Pill { get; set; } = new();
}

/// <summary>Look of the taskbar word. Colours are "#RRGGBB"; an invalid value falls back to the default.</summary>
public sealed class PillStyle
{
    public const double MinFontSize = 10, MaxFontSize = 32, NarrowestWidth = 160, WidestWidth = 700;

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 16;
    /// <summary>"Normal", "SemiBold" or "Bold".</summary>
    public string FontWeight { get; set; } = "SemiBold";
    public bool Italic { get; set; }
    /// <summary>Colour of the word (front of the card).</summary>
    public string TextColor { get; set; } = "#F7F7FA";
    /// <summary>Colour of the answer in flashcard modes.</summary>
    public string RevealColor { get; set; } = "#D9D2FF";
    public bool Shadow { get; set; } = true;
    /// <summary>Background behind the word; empty = transparent (just the taskbar).</summary>
    public string Background { get; set; } = "";
    /// <summary>Background opacity 0–100.</summary>
    public int BackgroundOpacity { get; set; } = 70;
    /// <summary>Widest the word may get before "…" (px).</summary>
    public double MaxWidth { get; set; } = 340;

    public PillStyle Copy() => (PillStyle)MemberwiseClone();
}

public static class DisplayModes
{
    public const string Word = "word";
    public const string Flash = "flash";
    public const string Reverse = "reverse";
}

/// <summary>A learning plan: words grouped into Day 1..N, each day optionally titled.</summary>
public sealed class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Dictionary<int, string> DayTitles { get; set; } = [];
    /// <summary>Words in study order; <see cref="Word.Day"/> groups them into days.</summary>
    public List<Word> Words { get; set; } = [];

    [JsonIgnore] public int DayCount => Words.Count == 0 ? 0 : Words.Max(w => w.Day);
    public string DayTitle(int day) => DayTitles.GetValueOrDefault(day, "");
}

public sealed class Word
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Day { get; set; } = 1;
    public string Text { get; set; } = "";
    public string Phonetic { get; set; } = "";
    public string PartOfSpeech { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Example { get; set; } = "";
    /// <summary>When the word was first studied in a session.</summary>
    public DateTime? IntroducedOn { get; set; }
    /// <summary>Spaced-repetition state; null until the word is answered in a quiz.</summary>
    public ReviewState? Review { get; set; }
    /// <summary>State before today's answer, so answering again the same day replaces it.</summary>
    public ReviewState? ReviewBeforeToday { get; set; }
    public int ForgotCount { get; set; }
    public int CorrectCount { get; set; }
}

public sealed class ReviewState
{
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public double Ease { get; set; } = 2.5;
    public double IntervalDays { get; set; }
    public DateTime Due { get; set; }
    public DateTime LastReviewed { get; set; }
    public bool LastKnown { get; set; }

    public ReviewState Clone() => (ReviewState)MemberwiseClone();
}

/// <summary>Where the learner is in the course.</summary>
public sealed class CourseState
{
    public Guid? PlanId { get; set; }
    public int Day { get; set; } = 1;
    /// <summary>Today's session was finished; the next calendar day moves on to the next day.</summary>
    public bool DayCompleted { get; set; }
    public DateTime? DayCompletedOn { get; set; }
    /// <summary>A plan just finished whose summary has not been shown yet.</summary>
    public Guid? PendingSummaryPlanId { get; set; }
    public bool Finished { get; set; }
}

/// <summary>A word answered wrong, and how often, until it is answered right on a mistake day.</summary>
public sealed class MistakeEntry
{
    public Guid WordId { get; set; }
    public int Times { get; set; }
    public DateTime LastWrong { get; set; }
}

/// <summary>The words answered wrong in one test (or in one day's sessions).</summary>
public sealed class MistakeList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<Guid> WordIds { get; set; } = [];
}

/// <summary>A day of studying wrong words, taken on <see cref="Date"/> (or the first day after, if missed).</summary>
public sealed class MistakeDay
{
    public DateTime Date { get; set; }
    public List<Guid> WordIds { get; set; } = [];
    public bool Done { get; set; }
    public DateTime? DoneOn { get; set; }
}

public sealed class DayLog
{
    public int Known { get; set; }
    public int Forgot { get; set; }
    public bool SessionDone { get; set; }
    [JsonIgnore] public int Total => Known + Forgot;
}
