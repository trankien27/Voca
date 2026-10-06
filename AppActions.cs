using Voca.Models;

namespace Voca;

/// <summary>
/// What other windows may ask the taskbar window to do: speak a word, open a window, or start a
/// practice session over some words. Created by <see cref="MainWindow"/> and passed down.
/// </summary>
public sealed record AppActions(
    Action<string> Speak,
    bool CanListen,
    Action OpenStats,
    Action OpenMistakes,
    Action<IReadOnlyList<Word>, string, SessionMode> Practice,
    Func<bool> QuickAddHotkeyReady);

/// <summary>What a session is for; changes the labels and the closing note, not the steps.</summary>
public enum SessionMode { Course, MistakeDay, Practice, NewWords }
