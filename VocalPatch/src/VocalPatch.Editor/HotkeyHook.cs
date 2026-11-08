using System.Runtime.InteropServices;

namespace VocalPatch.Editor;

/// <summary>
/// Process-wide keyboard hotkeys registered with a low-level hook, so they work even
/// when the editor owns the foreground. Default chord is Ctrl+Alt+&lt;key&gt;. Runs on its
/// own message-pumping thread so it can be installed from the injected runtime.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private readonly Dictionary<uint, Action> _hotkeys;
    private readonly Func<bool> _ownsForeground;
    private readonly LowLevelKeyboardProc _proc; // keep a reference so it is not collected
    private Thread? _thread;
    private IntPtr _hook = IntPtr.Zero;
    private uint _threadId;

    // Modifier state tracked from the hook stream itself. GetAsyncKeyState is NOT
    // reliable here: a low-level hook runs at the front of the input pipeline, before
    // the async key state that GetAsyncKeyState reports has been updated, so it can
    // read Ctrl/Alt as still-up while the combo is being typed.
    private bool _controlDown;
    private bool _menuDown;

    /// <summary>Optional sink for per-key diagnostics (enable with <c>hotkeydebug=true</c>).</summary>
    public Action<string>? Log { get; set; }

    public HotkeyHook(uint virtualKey, Action onHotkey)
        : this(new Dictionary<uint, Action> { [virtualKey] = onHotkey })
    {
    }

    /// <param name="hotkeys">Virtual key -> action, all on Ctrl+Alt.</param>
    /// <param name="ownsForeground">
    /// Decides whether this process is the one the user is looking at. Defaults to the real
    /// window check; a test supplies its own, because a test process cannot make itself the
    /// foreground window while somebody else's window owns the desktop.
    /// </param>
    public HotkeyHook(IReadOnlyDictionary<uint, Action> hotkeys, Func<bool>? ownsForeground = null)
    {
        // net472's Dictionary has no IEnumerable<KeyValuePair> constructor, so copy by hand.
        _hotkeys = new Dictionary<uint, Action>();
        foreach (var pair in hotkeys) _hotkeys[pair.Key] = pair.Value;
        _proc = HookCallback;
        _ownsForeground = ownsForeground ?? IsOwnWindowForeground;
    }

    /// <summary>The virtual keys this hook swallows, as Ctrl+Alt chords.</summary>
    public IReadOnlyCollection<uint> Keys => _hotkeys.Keys;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "vocalpatch-hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // Give the thread a moment to install the hook.
        for (int i = 0; i < 50 && _hook == IntPtr.Zero; i++) Thread.Sleep(10);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        uint id = _threadId;
        if (id != 0) PostThreadMessage(id, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int message = wParam.ToInt32();
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        switch (message)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                TrackModifier(info.vkCode, down: true);
                if (Log != null) Log($"key vk=0x{info.vkCode:X2} down ctrl={CtrlDown()} alt={AltDown()}");

                if (_hotkeys.TryGetValue(info.vkCode, out var action) && CtrlDown() && AltDown())
                {
                    if (!_ownsForeground())
                    {
                        // Several editors can have the runtime injected at once and each one
                        // installs its own hook. Only the editor the user is looking at may
                        // act on the chord, and only it may swallow it.
                        if (Log != null) Log($"hotkey vk=0x{info.vkCode:X2} ignored (not the foreground editor)");
                        break;
                    }

                    if (Log != null) Log($"hotkey fired vk=0x{info.vkCode:X2}");
                    try { action(); } catch { /* never let an action break the hook chain */ }
                    return (IntPtr)1; // swallow so the editor does not also see it
                }
                break;

            case WM_KEYUP:
            case WM_SYSKEYUP:
                TrackModifier(info.vkCode, down: false);
                break;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Keeps Ctrl/Alt state in sync straight off the hook stream. LL hooks report the
    /// sided keys (LCONTROL/RCONTROL/LMENU/RMENU), so both the sided and generic codes
    /// are treated the same.
    /// </summary>
    private void TrackModifier(uint vkCode, bool down)
    {
        switch ((int)vkCode)
        {
            case VK_CONTROL:
            case VK_LCONTROL:
            case VK_RCONTROL:
                _controlDown = down;
                break;
            case VK_MENU:
            case VK_LMENU:
            case VK_RMENU:
                _menuDown = down;
                break;
        }
    }

    // Tracked state is authoritative; the async state is only a backstop in case a
    // modifier key-down was missed (e.g. held down while the hook was being installed).
    private bool CtrlDown() => _controlDown || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    private bool AltDown() => _menuDown || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    /// <summary>
    /// True when this process owns the foreground window, i.e. the user is looking at this
    /// editor. Without this check every injected editor would answer the same chord and the
    /// first hook in the chain would swallow it, so the action would land on an arbitrary
    /// one of them. No foreground window (desktop, lock screen) counts as ours so a chord
    /// is never silently dropped.
    /// </summary>
    private static bool IsOwnWindowForeground()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return true;

        uint owner;
        GetWindowThreadProcessId(window, out owner);
        return owner == OwnProcessId;
    }

    private static readonly uint OwnProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
