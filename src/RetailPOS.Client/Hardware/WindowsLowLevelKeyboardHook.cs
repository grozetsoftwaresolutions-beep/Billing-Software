using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RetailPOS.Client.Hardware;

public interface IWindowsKeyboardHook : IDisposable
{
    bool IsHookActive { get; }
    void StartHook();
    void StopHook();
}

/// <summary>
/// Captures system-wide keyboard events using Win32 WH_KEYBOARD_LL low-level hook.
/// Operates focus-independently so USB keyboard-wedge barcode scanners can be captured
/// even when the POS application window or billing textbox is not active or focused.
/// </summary>
public sealed class WindowsLowLevelKeyboardHook : IWindowsKeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly KeyboardWedgeListener _listener;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int ToAscii(uint uVirtKey, uint uScanCode, byte[] lpKeyState, [Out] byte[] lpChar, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardState(byte[] lpKeyState);

    public bool IsHookActive => _hookId != IntPtr.Zero;

    public WindowsLowLevelKeyboardHook(KeyboardWedgeListener listener)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _proc = HookCallback;
    }

    public void StartHook()
    {
        if (_disposed || IsHookActive) return;

        if (!OperatingSystem.IsWindows())
            return;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
    }

    public void StopHook()
    {
        if (IsHookActive && OperatingSystem.IsWindows())
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Test helper to simulate hook callback execution deterministically in unit tests
    /// without requiring an active Windows interactive desktop message pump.
    /// </summary>
    public void SimulateLowLevelKeyEvent(uint vkCode, uint scanCode = 0)
    {
        var ch = MapVirtualKeyToChar(vkCode, scanCode);
        if (ch.HasValue)
        {
            _listener.ProcessKey(ch.Value);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var ch = MapVirtualKeyToChar(hookStruct.vkCode, hookStruct.scanCode);
                if (ch.HasValue)
                {
                    _listener.ProcessKey(ch.Value);
                }
            }
            catch
            {
                // Never allow unhandled exceptions in low-level hook to destabilize OS message pump
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static char? MapVirtualKeyToChar(uint vkCode, uint scanCode)
    {
        // Common control keys
        if (vkCode == 0x0D) return '\r'; // Enter / Return
        if (vkCode == 0x0A) return '\n'; // Line feed
        if (vkCode == 0x09) return '\t'; // Tab

        // Digits 0-9
        if (vkCode >= 0x30 && vkCode <= 0x39)
            return (char)('0' + (vkCode - 0x30));

        // Numpad 0-9
        if (vkCode >= 0x60 && vkCode <= 0x69)
            return (char)('0' + (vkCode - 0x60));

        // Letters A-Z
        if (vkCode >= 0x41 && vkCode <= 0x5A)
            return (char)('A' + (vkCode - 0x41));

        // Fallback to ToAscii Win32 translation if available
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var keyState = new byte[256];
                if (GetKeyboardState(keyState) != 0)
                {
                    var chars = new byte[2];
                    var result = ToAscii(vkCode, scanCode, keyState, chars, 0);
                    if (result == 1 && chars[0] >= 32)
                    {
                        return (char)chars[0];
                    }
                }
            }
            catch
            {
                // Fallback translation
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHook();
    }
}
