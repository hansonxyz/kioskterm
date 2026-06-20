using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KioskTerm;

/// <summary>
/// The Win32 layer that makes the overlay behave: hide the taskbar, sink the
/// window to the rear of the z-order, and swallow the keys that would let a user
/// escape (Win, Ctrl+Esc, Alt+Tab, Alt+Esc, Alt+F4). Ctrl+Alt+Del is a Secure
/// Attention Sequence and cannot be intercepted by design — that is the one
/// deliberate escape hatch.
/// </summary>
internal static class Native
{
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_MOUSEACTIVATE     = 0x0021;
    public const int WM_SYSCOMMAND        = 0x0112;
    public const int SC_MINIMIZE          = 0xF020;
    public const int MA_NOACTIVATE        = 3;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int GWL_EXSTYLE        = -20;
    public const int WS_EX_NOACTIVATE   = 0x08000000;
    public const int WS_EX_TOOLWINDOW   = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---- taskbar -------------------------------------------------------------

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_COMMAND = 0x0111;
    private const int MIN_ALL = 419;   // Shell_TrayWnd command id for "Minimize all windows"

    /// <summary>
    /// Minimizes every other top-level window (the shell's "Show desktop" action).
    /// Call this BEFORE the overlay window is created so it isn't minimized too.
    /// </summary>
    public static void MinimizeAllWindows()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
            PostMessage(tray, WM_COMMAND, (IntPtr)MIN_ALL, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly List<IntPtr> _hiddenBars = new();

    public static void HideTaskbar()
    {
        _hiddenBars.Clear();

        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) { ShowWindow(primary, SW_HIDE); _hiddenBars.Add(primary); }

        // Secondary-monitor taskbars (there can be several).
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "Shell_SecondaryTrayWnd")
            {
                ShowWindow(hwnd, SW_HIDE);
                _hiddenBars.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
    }

    public static void RestoreTaskbar()
    {
        foreach (var h in _hiddenBars)
            ShowWindow(h, SW_SHOW);
        _hiddenBars.Clear();
    }

    // ---- power / display state ----------------------------------------------

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS       = 0x80000000,
        ES_SYSTEM_REQUIRED  = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    /// <summary>
    /// Keeps the machine awake and the display on for as long as the calling
    /// thread lives. The state auto-clears if the thread (process) dies, so it
    /// can never strand the machine in a no-sleep state.
    /// Must be called from the thread that stays alive for the app's lifetime.
    /// </summary>
    public static void PreventSleepAndDisplayOff()
    {
        SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS |
            EXECUTION_STATE.ES_SYSTEM_REQUIRED |
            EXECUTION_STATE.ES_DISPLAY_REQUIRED);
    }

    /// <summary>Clears the keep-awake/keep-display state, restoring normal power behaviour.</summary>
    public static void RestorePowerState()
    {
        SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }

    // ---- low-level keyboard hook --------------------------------------------

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;

    private const int VK_TAB    = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4     = 0x73;
    private const int VK_LWIN   = 0x5B;
    private const int VK_RWIN   = 0x5C;
    private const int VK_MENU    = 0x12; // Alt
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;

    // When true (input mode), also swallow every Ctrl+<key> combo (so Ctrl+C can't
    // kill the running script) and Alt+Tab.
    private static bool _blockCtrlCombos;

    private const uint LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept alive for the lifetime of the process so the GC never collects the
    // thunk while the hook is installed.
    private static LowLevelKeyboardProc? _proc;
    private static IntPtr _hook = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static void InstallKeyboardHook(bool blockCtrlCombos)
    {
        _blockCtrlCombos = blockCtrlCombos;
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    public static void RemoveKeyboardHook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            bool alt  = (info.flags & LLKHF_ALTDOWN) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            // Always block what lets a user escape the overlay: opening the Start
            // menu (Win / Ctrl+Esc) and closing the window (Alt+F4).
            bool block =
                vk == VK_LWIN || vk == VK_RWIN ||           // Start menu (Win key)
                (ctrl && vk == VK_ESCAPE) ||                // Start menu (Ctrl+Esc)
                (alt  && vk == VK_F4);                      // close window

            // Input mode also blocks every Ctrl+<key> combo (no Ctrl+C to kill the
            // script) and Alt+Tab, while leaving ordinary typing untouched.
            if (_blockCtrlCombos)
            {
                if (alt && vk == VK_TAB) block = true;
                if (ctrl && vk != VK_CONTROL && vk != VK_LCONTROL && vk != VK_RCONTROL) block = true;
            }

            if (block)
                return (IntPtr)1; // swallow
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ---- foreground grab + reclaim (input mode) -----------------------------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const int  OBJID_WINDOW            = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private static WinEventDelegate? _winEventProc;   // kept alive while hooked
    private static IntPtr _winEventHook = IntPtr.Zero;
    private static IntPtr _watchTarget = IntPtr.Zero;
    private static Action? _onReclaim;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>
    /// Pulls <paramref name="hwnd"/> to the foreground, working around the
    /// foreground-lock by briefly attaching to the current foreground thread's input.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);

        ShowWindow(hwnd, SW_SHOW);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);

        if (attached) AttachThreadInput(thisThread, fgThread, false);
    }

    /// <summary>
    /// Watches foreground changes; whenever focus falls to the shell/desktop
    /// (i.e. nothing "real" is in front — e.g. a spawned installer just closed),
    /// pulls our window back to the front and invokes <paramref name="onReclaim"/>.
    /// </summary>
    public static void StartForegroundWatch(IntPtr target, Action onReclaim)
    {
        _watchTarget = target;
        _onReclaim = onReclaim;
        if (_winEventHook != IntPtr.Zero) return;
        _winEventProc = WinEventCallback;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void StopForegroundWatch()
    {
        if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        _winEventProc = null;
        _watchTarget = IntPtr.Zero;
        _onReclaim = null;
    }

    private static void WinEventCallback(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_watchTarget == IntPtr.Zero || idObject != OBJID_WINDOW) return;
        if (hwnd == _watchTarget) return;          // we're already in front
        if (!IsShellOrDesktop(hwnd)) return;       // a real window has focus — leave it be

        ForceForeground(_watchTarget);
        _onReclaim?.Invoke();
    }

    private static bool IsShellOrDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        string c = sb.ToString();
        return c.Length == 0 || c == "Progman" || c == "WorkerW"
            || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
    }
}
