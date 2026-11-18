using System.Runtime.InteropServices;
using System.Windows;

namespace VoiceInput;

internal readonly record struct InputTarget(nint Window, nint Focus, uint Thread);

internal static class DesktopInput
{
    internal const int HotkeyMessage = 0x0312;
    internal const int HotkeyId = 1;

    public static bool Register(nint window) => RegisterHotKey(window, HotkeyId, 0x4006, 0x78);
    public static void Unregister(nint window) => UnregisterHotKey(window, HotkeyId);

    public static InputTarget Capture()
    {
        nint window = GetForegroundWindow();
        if (window == 0) return default;
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == 0)
            return default;
        return new(window, info.Focus, thread);
    }

    public static bool TryType(InputTarget target, string text)
    {
        if (target.Window == 0 || target != Capture()) return false;
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if (GetAsyncKeyState(key) < 0) return false;

        string normalized = NormalizeText(text);
        if (normalized.Length == 0) return false;
        if (TryPaste(target, normalized)) return true;

        return SendUnicode(target, normalized);
    }

    internal static string NormalizeText(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    private static bool TryPaste(InputTarget target, string text)
    {
        if (target != Capture()) return false;

        IDataObject? original = null;
        bool clipboardOwned = false;
        uint clipboardSequence = 0;
        try
        {
            original = Clipboard.GetDataObject();
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            clipboardOwned = true;
            clipboardSequence = GetClipboardSequenceNumber();
            if (target != Capture() || !SendPaste()) return false;
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (clipboardOwned) RestoreClipboard(original, text, clipboardSequence);
        }
    }

    private static void RestoreClipboard(IDataObject? original, string insertedText, uint ownedSequence)
    {
        try
        {
            if (ownedSequence == 0 || GetClipboardSequenceNumber() != ownedSequence ||
                !Clipboard.ContainsText(TextDataFormat.UnicodeText) ||
                Clipboard.GetText(TextDataFormat.UnicodeText) != insertedText)
                return;

            if (original is null)
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(original, true);
        }
        catch (ExternalException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool SendPaste()
    {
        var events = new[]
        {
            CreateKeyEvent(0x11, 0),
            CreateKeyEvent(0x56, 0),
            CreateKeyEvent(0x56, 2),
            CreateKeyEvent(0x11, 2),
        };
        return SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) == events.Length;
    }

    private static bool SendUnicode(InputTarget target, string normalized)
    {
        var events = new Input[normalized.Length * 2];
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            var key = new KeyInput { Scan = c, Flags = 4 };
            events[2 * i] = new Input { Type = 1, Data = new InputData { Key = key } };
            key.Flags |= 2;
            events[2 * i + 1] = new Input { Type = 1, Data = new InputData { Key = key } };
        }
        return target == Capture() && SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) == events.Length;
    }

    private static Input CreateKeyEvent(ushort virtualKey, uint flags) =>
        new() { Type = 1, Data = new InputData { Key = new KeyInput { VirtualKey = virtualKey, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyInput Key;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
