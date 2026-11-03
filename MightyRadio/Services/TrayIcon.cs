using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MightyRadio.Services;

/// <summary>
/// Notification-area icon using Shell_NotifyIcon directly (no WinForms: that would add ~9 MB to the download).
/// Create, use and dispose on the UI thread; events are raised on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_NULL = 0;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    private const int CmdShow = 1, CmdPlayStop = 2, CmdExit = 3;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _hIcon;
    private readonly bool _ownsIcon;
    private string _tip = "Mighty Radio";
    private bool _playing;
    private bool _visible;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? PlayStopRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        // An invisible top-level window (not message-only, so it receives the TaskbarCreated broadcast after Explorer restarts).
        _window = new HwndSource(new HwndSourceParameters("Mighty Radio tray") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && ExtractIconEx(exe, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
            {
                _hIcon = small;
                _ownsIcon = true;
            }
        }
        catch (Exception ex) { Storage.Log(ex, "Tray icon image"); }
        if (_hIcon == IntPtr.Zero) _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed || value == _visible) return;
            _visible = value;
            if (value) Add(); else Delete();
        }
    }

    public void Update(bool playing, string? tooltip)
    {
        _playing = playing;
        var t = string.IsNullOrWhiteSpace(tooltip) ? "Mighty Radio" : "Mighty Radio - " + tooltip;
        _tip = t.Length > 127 ? t[..127] : t;
        if (_visible)
        {
            var d = NewData(NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
    }

    public void Notify(string title, string text)
    {
        if (!_visible || _disposed) return;
        var d = NewData(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = NIIF_INFO;
        d.uTimeoutOrVersion = 4000;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private void Add()
    {
        var d = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref d)) Storage.Log(null, "Tray icon could not be added");
    }

    private void Delete()
    {
        var d = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
    }

    private NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_TRAY)
        {
            uint mouse = (uint)((long)lParam & 0xFFFF);
            if (mouse == WM_LBUTTONUP) ShowRequested?.Invoke();
            else if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            handled = true;
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated && _visible)
        {
            Add(); // Explorer restarted
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, CmdShow, "Show Mighty Radio");
            AppendMenu(menu, MF_STRING, CmdPlayStop, _playing ? "Stop" : "Play");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, "Exit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case CmdShow: ShowRequested?.Invoke(); break;
                case CmdPlayStop: PlayStopRequested?.Invoke(); break;
                case CmdExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Visible = false;
        _disposed = true;
        try { _window.RemoveHook(WndProc); _window.Dispose(); } catch { }
        if (_ownsIcon) DestroyIcon(_hIcon);
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
