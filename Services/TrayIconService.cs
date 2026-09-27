using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 通知区域（托盘）驻留。
///
/// Windows App SDK 1.8 没有提供 NotifyIcon / Taskbar 之类的托管 API
/// （已在 SDK 的 winmd 与投影 dll 里扫过，零命中），所以这里按微软官方
/// WinUI 3 示例的路子走原生 Shell_NotifyIcon：
/// 自建一个 message-only 窗口接收托盘回调，右键用 Win32 弹出菜单。
/// 弹出菜单只能是系统主题渲染——托盘菜单在 Explorer 里本来就是 Win32 的，
/// 这不是对本应用主题的偏离，而是这一层的既有规范。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint WmTray = 0x0400 + 1;          // WM_APP + 1
    private const uint WmDestroy = 0x0002;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const int IdOpen = 1;
    private const int IdExit = 2;

    /// <summary>WndProc 是静态入口，靠这个字段找回实例；同一进程只驻留一枚托盘图标。</summary>
    private static TrayIconService? _current;

    private readonly WndProc _wndProc;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly IntPtr _ownerHwnd;
    private IntPtr _hwnd = IntPtr.Zero;
    private NotifyIconData _nid;
    private bool _added;
    private bool _disposed;

    private TrayIconService(IntPtr ownerHwnd, string tooltip, Action onOpen, Action onExit)
    {
        _ownerHwnd = ownerHwnd;
        _onOpen = onOpen;
        _onExit = onExit;
        _wndProc = HandleMessage;
        _nid = new NotifyIconData
        {
            hWnd = IntPtr.Zero,
            uID = 1,
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        };

        try
        {
            RegisterAndCreateWindow(tooltip);
        }
        catch (Exception ex)
        {
            App.Log($"TrayIconService: 初始化失败，应用将不驻留托盘：{ex}");
            _current = null;
        }
    }

    /// <summary>成功建好并挂上图标才为真；失败时调用方必须放弃"关窗即隐藏"，否则应用会关不掉。</summary>
    public bool IsAvailable => _added;

    /// <summary>建服务；任何一步失败都返回不可用实例而不是抛异常。</summary>
    public static TrayIconService TryCreate(IntPtr ownerHwnd, string tooltip, Action onOpen, Action onExit)
    {
        if (_current is not null)
        {
            return _current;
        }
        var service = new TrayIconService(ownerHwnd, tooltip, onOpen, onExit);
        if (!service.IsAvailable)
        {
            service.Dispose();
        }
        return service;
    }

    private void RegisterAndCreateWindow(string tooltip)
    {
        var instance = GetModuleHandle(null);
        _wndProcHandle = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var cls = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = _wndProcHandle,
            hInstance = instance,
            lpszClassName = ClassName,
        };
        var atom = RegisterClassEx(ref cls);
        if (atom == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx 失败：{Marshal.GetLastWin32Error()}");
        }

        _hwnd = CreateWindowEx(0, ClassName, "SekaiSync Tray", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx 失败：{Marshal.GetLastWin32Error()}");
        }

        _nid.hWnd = _hwnd;
        _nid.uFlags = NifMessage | NifIcon | NifTip;
        _nid.uCallbackMessage = WmTray;
        // 与 MainWindow 里 AppWindow.SetIcon 用的是同一个路径，图标只有一份来源。
        _nid.hIcon = LoadTrayIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        _nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (!Shell_NotifyIcon(NimAdd, ref _nid))
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) 失败");
        }

        _added = true;
        _current = this;
    }

    /// <summary>
    /// 托盘图标直接用 <c>Assets/app.ico</c>——也就是 <c>AppWindow.SetIcon</c> 给窗口用的
    /// 同一个文件，两者不可能再长得不一样；之前从 exe 的 PE 资源取，图标缓存一滞后就会
    /// 出现"窗口是新图标、托盘是旧图标"。按系统小图标尺寸取帧，取不到再退回 exe，
    /// 再退不回才用系统通用图标。
    /// </summary>
    private static IntPtr LoadTrayIcon(string iconPath)
    {
        var cx = GetSystemMetrics(SM_CXSMICON);
        var cy = GetSystemMetrics(SM_CYSMICON);
        if (cx <= 0)
        {
            cx = 16;
            cy = 16;
        }
        foreach (var candidate in new[] { iconPath, Environment.ProcessPath ?? string.Empty })
        {
            if (candidate.Length == 0 || !System.IO.File.Exists(candidate))
            {
                continue;
            }
            // LR_SHARED：句柄归系统缓存，我们不 DestroyIcon。
            var handle = LoadImage(IntPtr.Zero, candidate, IMAGE_ICON, cx, cy, LR_LOADFROMFILE | LR_SHARED);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTray:
                OnTrayCallback((uint)(long)lParam & 0xFFFF);
                return IntPtr.Zero;
            case WmDestroy:
                if (_added)
                {
                    Shell_NotifyIcon(NimDelete, ref _nid);
                    _added = false;
                }
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void OnTrayCallback(uint mouseMessage)
    {
        switch (mouseMessage)
        {
            // 左键单击 / 双击 / Vista 之后的 NIN_SELECT 都算"打开面板"。
            case WmLButtonUp:
            case WmLButtonDblClk:
            case NinSelect:
            case NinKeySelect:
                _onOpen();
                break;
            case WmRButtonUp:
            case WmRButtonDown:
            case WmContextMenu:
                ShowMenu();
                break;
        }
    }

    /// <summary>
    /// 只有两项：打开面板、退出。用 TPM_RETURNCMD 同步取返回值，
    /// 这样不必给 message-only 窗口接 WM_COMMAND，也不必依赖它当前台。
    /// </summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }
        try
        {
            AppendMenu(menu, MF_STRING, IdOpen, "打开面板");
            AppendMenu(menu, MF_STRING, IdExit, "退出");
            if (!GetCursorPos(out var point))
            {
                point = new Point { X = 0, Y = 0 };
            }
            // 菜单归属 message-only 窗口，它永远拿不到前台；先把真正的 app 窗口提到前面，
            // 否则点菜单外区域时弹出菜单不会自行消失。
            SetForegroundWindow(_ownerHwnd);
            var chosen = TrackPopupMenu(menu,
                TPM_RIGHTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
                point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (chosen)
            {
                case IdOpen:
                    _onOpen();
                    break;
                case IdExit:
                    _onExit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            Shell_NotifyIcon(NimDelete, ref _nid);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────

    private const string ClassName = "SekaiSyncTrayMessageWindow";
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private IntPtr _wndProcHandle;

    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonUp = 0x0402;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint WM_NULL = 0x0000;
    private const uint MF_STRING = 0x0000;
    private const uint TPM_RIGHTALIGN = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RETURNCMD = 0x0100;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_SHARED = 0x0080;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx clazz);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr id);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
}
