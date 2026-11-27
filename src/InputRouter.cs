using System.Runtime.InteropServices;

namespace WebWallpaper;

/// <summary>
/// 桌面层的鼠标转发：只在鼠标位于「桌面空白处」时，把移动/点击/拖拽/滚轮送进对应屏幕的网页。
/// 右键、桌面图标、任务栏和其他程序一律放行。
/// 钩子回调里不做磁盘 I/O，所有异常都在本地吞掉，避免系统静默摘钩后留下半截状态。
/// </summary>
internal sealed class InputRouter : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    private IntPtr _hook;
    private readonly Native.LowLevelMouseProc _proc;
    private readonly Func<Point, WallpaperWindow?> _windowAt;
    private readonly Action<WallpaperWindow, int, int, bool> _onMove;
    private readonly Action<WallpaperWindow, int, int> _onDown;
    private readonly Action<WallpaperWindow, int, int> _onUp;
    private readonly Action<WallpaperWindow> _onLeave;
    private readonly Action<WallpaperWindow, int, int, int, int> _onWheel;
    private readonly Action<WallpaperWindow, int, int> _onClickFinished;

    private bool _leftDown;
    private WallpaperWindow? _dragTarget;
    private WallpaperWindow? _hoverWindow;
    private long _lastHover;
    private WallpaperWindow? _pendingClick;
    private int _pendingX, _pendingY;

    /// <summary>滚轮是否转给网页。关掉时必须放行给系统，否则滚轮会凭空消失。</summary>
    public volatile bool WheelForwarding = true;

    /// <summary>
    /// 编辑态：网页窗口已经升成真窗口、盖在图标上面，鼠标由系统直接投递。
    /// 这时再转发一次就会「点一下当两下」，所以整条转发必须停手。
    /// </summary>
    public volatile bool Editing;

    /// <summary>最近一次点击没转过去的原因（0=正常 1=没有窗口 2=窗口取不到 3=不是桌面 4=压在图标上）</summary>
    public volatile int DiagReason = -1;
    /// <summary>最近一次没转过去的现场（类名/句柄），只在出问题时写，避免拖慢钩子。</summary>
    public volatile string DiagInfo = "";
    /// <summary>钩子收到的鼠标事件计数，用来判断钩子还活着</summary>
    public long EventCount;

    public InputRouter(Func<Point, WallpaperWindow?> windowAt,
        Action<WallpaperWindow, int, int, bool> onMove,
        Action<WallpaperWindow, int, int> onDown,
        Action<WallpaperWindow, int, int> onUp,
        Action<WallpaperWindow> onLeave,
        Action<WallpaperWindow, int, int, int, int> onWheel,
        Action<WallpaperWindow, int, int> onClickFinished)
    {
        _windowAt = windowAt;
        _onMove = onMove;
        _onDown = onDown;
        _onUp = onUp;
        _onLeave = onLeave;
        _onWheel = onWheel;
        _onClickFinished = onClickFinished;
        _proc = HookCallback;

        // 钩子必须跑在自己的线程上：系统是同步等回调返回的，
        // 挂在界面线程上时，只要界面线程忙一下（比如和浏览器通信），整个鼠标都会被卡住。
        _hookThread = new Thread(HookThreadProc) { IsBackground = true, Name = "MouseHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private readonly Thread _hookThread;
    private uint _hookThreadId;

    private void HookThreadProc()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("安装鼠标钩子失败 err=" + Marshal.GetLastWin32Error());
            return;
        }

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            return Handle(wParam, lParam);
        }
        catch
        {
            ResetDrag();
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        }
    }

    private IntPtr Handle(IntPtr wParam, IntPtr lParam)
    {
        if (Editing)
        {
            ResetDrag();
            return Native.CallNextHookEx(_hook, 0, wParam, lParam);
        }

        Interlocked.Increment(ref EventCount);

        int msg = (int)wParam;
        var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        var pt = new Point(data.pt.X, data.pt.Y);

        // 已经按下、正在拖拽：无论指针飘到哪，都继续把事件交给同一个网页
        if (_leftDown && (msg == Native.WM_MOUSEMOVE || msg == Native.WM_LBUTTONUP))
        {
            var drag = _dragTarget;
            if (drag == null || drag.IsDisposed)
            {
                ResetDrag();
                return Native.CallNextHookEx(_hook, 0, wParam, lParam);
            }

            var c = ToClient(drag, pt);
            if (msg == Native.WM_MOUSEMOVE)
            {
                _onMove(drag, c.X, c.Y, true);
                return Native.CallNextHookEx(_hook, 0, wParam, lParam);
            }

            _onUp(drag, c.X, c.Y);
            _leftDown = false;
            _dragTarget = null;
            NotifyClickFinished();
            return (IntPtr)1;
        }

        if (msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
        {
            if (!WheelForwarding) return Native.CallNextHookEx(_hook, 0, wParam, lParam);

            var win = ResolveTarget(pt, true);
            if (win == null) return Native.CallNextHookEx(_hook, 0, wParam, lParam);

            var c = ToClient(win, pt);
            int delta = (short)(data.mouseData >> 16);
            int dy = msg == Native.WM_MOUSEWHEEL ? delta : 0;
            int dx = msg == Native.WM_MOUSEHWHEEL ? delta : 0;
            _onWheel(win, c.X, c.Y, dx, dy);
            return (IntPtr)1;
        }

        if (msg == Native.WM_MOUSEMOVE)
        {
            var win = ResolveTarget(pt, false);
            if (win == null)
            {
                var leaving = _hoverWindow;
                _hoverWindow = null;
                if (leaving != null && !leaving.IsDisposed) _onLeave(leaving);
                return Native.CallNextHookEx(_hook, 0, wParam, lParam);
            }

            if (Environment.TickCount64 - _lastHover < 16) return Native.CallNextHookEx(_hook, 0, wParam, lParam);
            _lastHover = Environment.TickCount64;
            _hoverWindow = win;
            var c = ToClient(win, pt);
            _onMove(win, c.X, c.Y, false);
            return Native.CallNextHookEx(_hook, 0, wParam, lParam);
        }

        // 右键永远留给 Windows 桌面菜单
        if (msg == Native.WM_RBUTTONDOWN || msg == Native.WM_RBUTTONUP)
            return Native.CallNextHookEx(_hook, 0, wParam, lParam);

        if (msg != Native.WM_LBUTTONDOWN && msg != Native.WM_LBUTTONDBLCLK)
            return Native.CallNextHookEx(_hook, 0, wParam, lParam);

        var target = ResolveTarget(pt, true);
        if (target == null)
        {
            ResetDrag();
            return Native.CallNextHookEx(_hook, 0, wParam, lParam);
        }

        var cp = ToClient(target, pt);
        _onDown(target, cp.X, cp.Y);
        _leftDown = true;
        _dragTarget = target;
        _pendingClick = target;
        _pendingX = cp.X;
        _pendingY = cp.Y;
        return (IntPtr)1;
    }

    /// <summary>转发之后调用（UI 线程）：判断是不是点到了输入框。</summary>
    public void NotifyClickFinished()
    {
        var target = _pendingClick;
        _pendingClick = null;
        if (target == null || target.IsDisposed) return;
        _onClickFinished(target, _pendingX, _pendingY);
    }

    private static Native.POINT ToClient(WallpaperWindow window, Point screenPoint)
    {
        var c = new Native.POINT(screenPoint.X, screenPoint.Y);
        var chrome = window.CachedChrome;
        if (chrome != IntPtr.Zero) Native.ScreenToClient(chrome, ref c);
        return c;
    }

    private void ResetDrag()
    {
        _leftDown = false;
        _dragTarget = null;
        _pendingClick = null;
    }

    private WallpaperWindow? ResolveTarget(Point screenPoint, bool checkIcons)
    {
        var win = _windowAt(screenPoint);
        if (win == null || win.IsDisposed) { if (checkIcons) DiagReason = 1; return null; }

        var under = Native.WindowFromPoint(new Native.POINT(screenPoint.X, screenPoint.Y));
        if (under == IntPtr.Zero) { if (checkIcons) DiagReason = 2; return null; }

        // 网页自己弹出的窗口（比如原生下拉框列表）：交给系统去点，转发会打在网页正文上
        var chrome = win.CachedChrome;
        if (chrome != IntPtr.Zero && under != chrome)
        {
            Native.GetWindowThreadProcessId(under, out uint underPid);
            Native.GetWindowThreadProcessId(chrome, out uint webPid);
            if (underPid != 0 && underPid == webPid) return null;
        }
        if (!DesktopHost.IsDesktopWindow(under))
        {
            // WebView2 的窗口树比较深（宿主 → Chrome_WidgetWin → RenderWidgetHost …），
            // 层数给少了会把网页上的点击判成「不是桌面」直接丢掉
            // 判断标准只有一条：这个窗口是不是挂在桌面（Progman）下面。
            // 我们的壁纸是桌面的子窗口，浏览器、微信这类普通程序不是。
            // 之前按窗口类名猜（Chrome_*），结果把 Edge 也当成了壁纸，点击全被吞掉。
            var root = Native.GetAncestor(under, Native.GA_ROOT);
            var progman = DesktopHost.Progman;
            if (root == progman && progman != IntPtr.Zero)
            {
                if (checkIcons) DiagReason = 0;
                return win;
            }

            // 不是挂在桌面下面的窗口：那是浏览器、微信这类正常程序，一律不碰
            if (checkIcons) DiagReason = 3;
            return null;
        }

        if (checkIcons && DesktopHost.IsOverIcon(under, screenPoint)) { DiagReason = 4; return null; }
        if (checkIcons) DiagReason = 0;
        return win;
    }

    public void Dispose()
    {
        try
        {
            if (_hookThreadId != 0)
                Native.PostThreadMessage(_hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);  // WM_QUIT
            _hookThread.Join(500);
        }
        catch { }
        try { if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
    }
}
