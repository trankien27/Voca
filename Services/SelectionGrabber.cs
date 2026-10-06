using System.Runtime.InteropServices;
using System.Text;

namespace Voca.Services;

/// <summary>
/// Reads the text selected in the app in front (for Ctrl+Alt+V): waits until the hotkey's keys are
/// released, sends Ctrl+C, and takes the new clipboard text only if the clipboard actually changed —
/// so an old copy (e.g. "v2.8.0") is never mistaken for the selection. The previous clipboard text is
/// put back. Consoles are skipped because Ctrl+C there stops the running program.
/// </summary>
public static class SelectionGrabber
{
    public enum Outcome { Selected, NothingSelected, Console }

    public static async Task<(Outcome Outcome, string? Text)> GrabAsync()
    {
        // The learner is still holding Ctrl+Alt+V; Ctrl+C sent now would arrive as Ctrl+Alt+C.
        for (var i = 0; i < 40 && (IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkShift) || IsDown(VkV)); i++)
            await Task.Delay(25);

        var foreground = GetForegroundWindow();
        if (IsConsole(foreground)) return (Outcome.Console, null);

        var before = GetClipboardSequenceNumber();
        var previous = TryGetText();
        SendCtrlC();
        for (var i = 0; i < 20 && GetClipboardSequenceNumber() == before; i++)
            await Task.Delay(25);
        if (GetClipboardSequenceNumber() == before) return (Outcome.NothingSelected, null);

        await Task.Delay(30);   // let the copying app finish writing
        var selected = TryGetText();
        if (previous is not null) TrySetText(previous);
        return string.IsNullOrWhiteSpace(selected) ? (Outcome.NothingSelected, null) : (Outcome.Selected, selected);
    }

    /// <summary>Clipboard text, retried briefly because another app may hold the clipboard.</summary>
    public static string? TryGetText()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(40); }
        }
        return null;
    }

    private static void TrySetText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { System.Windows.Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(40); }
        }
    }

    private static bool IsConsole(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "PuTTY";
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            Key(VkControl, down: true), Key(VkC, down: true), Key(VkC, down: false), Key(VkControl, down: false)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(int key, bool down) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = down ? 0u : KeyEventKeyUp } }
    };

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkC = 0x43, VkV = 0x56;
    private const uint InputKeyboard = 1, KeyEventKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // Sized for the largest member (MOUSEINPUT) so Marshal.SizeOf matches what SendInput expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int max);
}
