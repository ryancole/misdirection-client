namespace Misdirection.Client;

/// <summary>
/// USB HID keyboard/keypad usage codes (usage page 0x07). These are what go on the wire — the
/// firmware does no translation from ASCII or virtual-key codes. Named after the US-layout keycap.
/// </summary>
public static class HidUsage
{
    public const byte A = 0x04, B = 0x05, C = 0x06, D = 0x07, E = 0x08, F = 0x09, G = 0x0A, H = 0x0B,
        I = 0x0C, J = 0x0D, K = 0x0E, L = 0x0F, M = 0x10, N = 0x11, O = 0x12, P = 0x13, Q = 0x14,
        R = 0x15, S = 0x16, T = 0x17, U = 0x18, V = 0x19, W = 0x1A, X = 0x1B, Y = 0x1C, Z = 0x1D;

    public const byte Digit1 = 0x1E, Digit2 = 0x1F, Digit3 = 0x20, Digit4 = 0x21, Digit5 = 0x22,
        Digit6 = 0x23, Digit7 = 0x24, Digit8 = 0x25, Digit9 = 0x26, Digit0 = 0x27;

    public const byte Enter = 0x28, Escape = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C,
        Minus = 0x2D, Equal = 0x2E, LeftBracket = 0x2F, RightBracket = 0x30, Backslash = 0x31,
        NonUsHash = 0x32, Semicolon = 0x33, Quote = 0x34, Grave = 0x35, Comma = 0x36, Period = 0x37,
        Slash = 0x38, CapsLock = 0x39;

    public const byte F1 = 0x3A, F2 = 0x3B, F3 = 0x3C, F4 = 0x3D, F5 = 0x3E, F6 = 0x3F, F7 = 0x40,
        F8 = 0x41, F9 = 0x42, F10 = 0x43, F11 = 0x44, F12 = 0x45;

    public const byte PrintScreen = 0x46, ScrollLock = 0x47, Pause = 0x48, Insert = 0x49, Home = 0x4A,
        PageUp = 0x4B, Delete = 0x4C, End = 0x4D, PageDown = 0x4E, RightArrow = 0x4F, LeftArrow = 0x50,
        DownArrow = 0x51, UpArrow = 0x52;

    public const byte NumLock = 0x53, KeypadDivide = 0x54, KeypadMultiply = 0x55, KeypadMinus = 0x56,
        KeypadPlus = 0x57, KeypadEnter = 0x58, Keypad1 = 0x59, Keypad2 = 0x5A, Keypad3 = 0x5B,
        Keypad4 = 0x5C, Keypad5 = 0x5D, Keypad6 = 0x5E, Keypad7 = 0x5F, Keypad8 = 0x60, Keypad9 = 0x61,
        Keypad0 = 0x62, KeypadPeriod = 0x63, NonUsBackslash = 0x64, Application = 0x65;

    public const byte F13 = 0x68, F14 = 0x69, F15 = 0x6A, F16 = 0x6B, F17 = 0x6C, F18 = 0x6D,
        F19 = 0x6E, F20 = 0x6F, F21 = 0x70, F22 = 0x71, F23 = 0x72, F24 = 0x73;

    public const byte LeftControl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3,
        RightControl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7;

    /// <summary>True for 0xE0..0xE7, which the firmware folds into the modifier byte instead of a key slot.</summary>
    public static bool IsModifier(byte usage) => usage is >= LeftControl and <= RightGui;

    /// <summary>Number of simultaneous non-modifier keys the firmware can hold before NACK(5).</summary>
    public const int MaxRollover = 6;
}
