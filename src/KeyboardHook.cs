using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WebWallpaper;

/// <summary>
/// 全系统键盘监听：只用来判断「这个键该不该交给网页」，本身不往网页里写任何东西。
///
/// 三条硬规矩，违反任意一条都会把整台电脑的键盘拖死：
/// 1. 回调必须很快返回（系统默认只等 300ms，超时会静默摘钩且无法感知），
///    所以这里只做几件事：读结构体、算修饰位、比对前台、PostMessage。不写日志、不加锁、不碰网页。
/// 2. 钩子装在独立线程（系统把回调送到安装它的那个线程），与鼠标钩子分开，
///    否则鼠标路径卡一下就会把键盘的预算吃掉。
/// 3. 钩子在输入法之前看到按键。所以不带修饰键的字母、数字必须原样放行，
///    吞掉它们等于直接废掉拼音组字。
///
/// 吞与不吞按「一次物理按下」锁存到松手为止：中途心跳抖动、队列变忙都不会改变这一轮的决定，
/// 否则会出现同一次按键的按下与松手分属两条通道，Shift/Ctrl 卡在按下状态。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>投递给界面线程的消息号（wParam = 虚拟键，lParam = 修饰位与按下标志）。</summary>
    public const int WM_APP_KEY = 0x8000 + 0x21;
    /// <summary>编辑态下按了 Esc：只用来结束编辑，其余键一律放行。</summary>
    public const int WM_APP_ESCAPE = 0x8000 + 0x24;
    /// <summary>编辑态下按了 F5 / Ctrl+R：刷新页面（浏览器自己的刷新是关掉的）。</summary>
    public const int WM_APP_RELOAD = 0x8000 + 0x25;

    private const int WM_APP_INSTALL = 0x8000 + 0x22;
    private const int WM_APP_UNINSTALL = 0x8000 + 0x23;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // 修饰位：与 CDP 的 modifiers 保持一致（Alt=1 / Ctrl=2 / Meta=4 / Shift=8）
    public const int ModAlt = 1;
    public const int ModCtrl = 2;
    public const int ModWin = 4;
    public const int ModShift = 8;

    /// <summary>已吞下但界面线程还没执行完的按键上限。超过就放行，宁可不增强也不排队。</summary>
    private const int MaxPending = 6;

    /// <summary>一条被吞的键最多等这么久；超过说明界面线程跟不上，之后一律放行。</summary>
    private const int PendingBudgetMs = 400;

    // 自测开关：只有 dist 目录里放了 .test-injected 才让注入键走真实路径，默认关闭。
    // 用文件而不是环境变量，是因为从命令行启动程序时环境未必传得进子进程。
    private static readonly bool AllowInjected =
        File.Exists(Path.Combine(AppContext.BaseDirectory, ".test-injected"));

    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly bool[] _held = new bool[256];      // 本轮物理按下是否被吞
    private readonly bool[] _down = new bool[256];      // 物理按下状态（用于识别自动重复）

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private volatile bool _running;

    // 界面线程写入、钩子线程只读
    private volatile bool _armed;
    private volatile bool _escapeWatch;
    private volatile IntPtr _escapeTarget;
    private volatile IntPtr _proxy;
    private volatile IntPtr _sink;

    private int _mods;
    private int _pending;
    private long _firstPendingTicks;

    private long _seq;
    private long _swallowed;
    private long _passed;
    private long _postFailed;
    private long _expired;
    private long _maxCallbackUs;

    public KeyboardHook()
    {
        // 委托必须由字段托住：交给系统的只是函数地址，被回收后回调就崩了
        _proc = HookCallback;
    }

    // 诊断用：最近几条按键的判定过程，由界面线程读出来写日志（回调里不写盘）
    public volatile int Diag_vk = -1;
    public volatile int Diag_mods;
    public volatile int Diag_why = -1;
    public volatile int Diag_escapeHits;
    public volatile int Diag_escapeMiss;
    private readonly int[] _trace = new int[32];
    private int _tracePos;

    public string TraceText()
    {
        var sb = new System.Text.StringBuilder();
        int pos = _tracePos;
        for (int i = 0; i < 8; i++)
        {
            int slot = (pos - 8 + i + 32) & 31;
            int v = _trace[slot];
            if (v == 0) continue;
            string sw = (v & 0x10000) != 0 ? "吞" : "放";
            string dn = (v & 0x20000) != 0 ? "↓" : "↑";
            sb.Append($" vk={v & 0xFF} m={(v >> 8) & 0xF} {sw}{dn}");
        }
        return sb.ToString();
    }

    private void Trace(int vk, int mods, bool swallow, bool down)
    {
        int v = (vk & 0xFF) | ((mods & 0xF) << 8) | (swallow ? 0x10000 : 0) | (down ? 0x20000 : 0);
        int slot = _tracePos & 31;
        _trace[slot] = v;
        _tracePos = (_tracePos + 1) & 31;
    }

    public long Seq => Interlocked.Read(ref _seq);
    public long Swallowed => Interlocked.Read(ref _swallowed);
    public long Passed => Interlocked.Read(ref _passed);
    public long PostFailed => Interlocked.Read(ref _postFailed);
    public long Expired => Interlocked.Read(ref _expired);
    public long MaxCallbackMicros => Interlocked.Read(ref _maxCallbackUs);
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>按键被吞下后必须回调一次，否则预算会被耗尽。</summary>
    public void Ack()
    {
        if (Interlocked.Decrement(ref _pending) <= 0)
        {
            Interlocked.Exchange(ref _pending, 0);
            Interlocked.Exchange(ref _firstPendingTicks, 0);
        }
    }

    /// <summary>开始/结束接管：只在这段时间里才可能吞键，其余时刻一律放行。</summary>
    /// <summary>
    /// 编辑态：网页窗口是真窗口、输入法直接工作，我们只盯着 Esc 用来结束编辑。
    /// 这个模式下所有键都放行，不碰输入法。
    /// </summary>
    public void SetEscapeWatch(bool on, IntPtr target)
    {
        _escapeTarget = target;
        _escapeWatch = on;
    }

    public void SetSession(bool armed, IntPtr proxy, IntPtr sink)
    {
        _proxy = proxy;
        _sink = sink;
        _armed = armed;
        if (!armed)
        {
            // 交还键盘：把残留的按下状态清干净，避免下次接管时把首次按键误判成自动重复
            Array.Clear(_down);
            Array.Clear(_held);
            _mods = 0;
        }
    }

    public void Install()
    {
        if (!_running)
        {
            _running = true;
            _thread = new Thread(ThreadProc) { IsBackground = true, Name = "KeyboardHook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            return;
        }
        Post(WM_APP_INSTALL);   // 已经在跑：让它在自己的线程上（重新）安装
    }

    public void Uninstall()
    {
        if (!_running) return;
        Post(WM_APP_UNINSTALL);
    }

    private void Post(int msg)
    {
        uint id = _threadId;
        if (id != 0) Native.PostThreadMessage(id, (uint)msg, IntPtr.Zero, IntPtr.Zero);
    }

    private void ThreadProc()
    {
        _threadId = Native.GetCurrentThreadId();
        // 线程一起来就把钩子装上：Install() 的首次调用只负责把线程拉起来
        InstallOnThisThread();
        // 线程消息（hwnd = NULL）不会被 DispatchMessage 处理，必须自己认消息号，
        // 否则「重装钩子」的指令会被静默丢掉，摘钩之后永远装不回来。
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_APP_INSTALL) InstallOnThisThread();
            else if (msg.message == WM_APP_UNINSTALL) RemoveOnThisThread();
            else
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        RemoveOnThisThread();
    }

    private void InstallOnThisThread()
    {
        // 先摘旧的再装：重复安装会叠成两个钩子，同一次按键被转发两遍
        RemoveOnThisThread();
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Write("[kbhook] 安装失败 err=" + Marshal.GetLastWin32Error());
        }
        else
        {
            Log.Write($"[kbhook] 已安装 自测注入={AllowInjected} 基目录={AppContext.BaseDirectory}");
        }
    }

    private void RemoveOnThisThread()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Write("[kbhook] 已卸载");
    }

    private IntPtr Next(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Increment(ref _passed);
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 计数无条件自增：界面线程拿它判断「钩子还活着」，漏掉放行的键会误判成已摘钩
        Interlocked.Increment(ref _seq);

        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            if (vk < 0 || vk > 255) return Next(nCode, wParam, lParam);

            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            TrackModifiers(vk, down);

            // 编辑态：只认 Esc，别的键原样放行（输入法自己处理）
            if (_escapeWatch)
            {
                if (down && vk == Native.VK_ESCAPE && Native.GetForegroundWindow() == _escapeTarget)
                {
                    var sink0 = _sink;
                    if (sink0 != IntPtr.Zero) Native.PostMessage(sink0, WM_APP_ESCAPE, IntPtr.Zero, IntPtr.Zero);
                    Interlocked.Increment(ref _swallowed);
                    Interlocked.Increment(ref Diag_escapeHits);
                    return (IntPtr)1;
                }
                // F5 / Ctrl+R：浏览器层的刷新被关掉了，编辑态下也得由我们来做
                if (down && (vk == 0x74 || (vk == 0x52 && (_mods & ModCtrl) != 0)))
                {
                    var sinkR = _sink;
                    if (sinkR != IntPtr.Zero) Native.PostMessage(sinkR, WM_APP_RELOAD, IntPtr.Zero, IntPtr.Zero);
                    Interlocked.Increment(ref _swallowed);
                    return (IntPtr)1;
                }
                if (down && vk == Native.VK_ESCAPE)
                {
                    Diag_escapeMiss = (int)(Native.GetForegroundWindow().ToInt64() & 0x7FFFFFFF);
                    Diag_why = (int)(_escapeTarget.ToInt64() & 0x7FFFFFFF);
                    Interlocked.Increment(ref _passed);
                }
                return Next(nCode, wParam, lParam);
            }

            // 别的程序往里注入的键一律不碰：屏幕键盘、密码管理器自动填充都走这条路
            // （自测开关：只有手工验证时才让注入键走真实路径，默认关闭）
            Diag_vk = vk;
            Diag_mods = _mods;

            if ((data.flags & Native.LLKHF_INJECTED) != 0 && !AllowInjected)
            { Diag_why = 2; return Next(nCode, wParam, lParam); }

            if (!_armed || _proxy == IntPtr.Zero) { Forget(vk); Diag_why = 0; return Next(nCode, wParam, lParam); }

            // 前台不是那个隐形窗口，就说明用户已经不在网页上了：放行，并且清掉残留状态
            if (Native.GetForegroundWindow() != _proxy) { Forget(vk); Diag_why = 1; return Next(nCode, wParam, lParam); }

            bool swallow = Decide(vk, down);
            Diag_why = swallow ? 6 : 3;
            Trace(vk, _mods, swallow, down);
            if (!swallow) return Next(nCode, wParam, lParam);

            // 界面线程已经跟不上：放行，宁可这一次不转发，也不把键盘排成一列
            if (!HasBudget()) { Forget(vk); return Next(nCode, wParam, lParam); }

            if (!Send(vk, down)) { Interlocked.Increment(ref _postFailed); Forget(vk); return Next(nCode, wParam, lParam); }

            Interlocked.Increment(ref _swallowed);
            return (IntPtr)1;
        }
        catch
        {
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        finally
        {
            long us = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
            if (us > Interlocked.Read(ref _maxCallbackUs)) Interlocked.Exchange(ref _maxCallbackUs, us);
        }
    }

    private bool HasBudget()
    {
        int pending = Interlocked.CompareExchange(ref _pending, 0, 0);
        if (pending <= 0) return true;
        if (pending >= MaxPending) return false;
        long first = Interlocked.Read(ref _firstPendingTicks);
        return first == 0 || Environment.TickCount64 - first <= PendingBudgetMs;
    }

    private bool Send(int vk, bool down)
    {
        var sink = _sink;
        if (sink == IntPtr.Zero) return false;

        int pending = Interlocked.Increment(ref _pending);
        if (pending == 1) Interlocked.Exchange(ref _firstPendingTicks, Environment.TickCount64);

        long payload = (long)(_mods & 0xF) << 1 | (down ? 1L : 0L);
        if (!Native.PostMessage(sink, WM_APP_KEY, (IntPtr)vk, (IntPtr)payload))
        {
            Ack();
            return false;
        }
        return true;
    }

    /// <summary>吞 / 不吞。按一次物理按下锁存，自动重复沿用第一次的决定。</summary>
    private bool Decide(int vk, bool down)
    {
        if (!down)
        {
            bool held = _held[vk];
            _held[vk] = false;
            _down[vk] = false;
            return held;
        }

        if (_down[vk]) return _held[vk];   // 自动重复

        _down[vk] = true;
        bool swallow = ShouldSwallow(vk);
        _held[vk] = swallow;
        return swallow;
    }

    private bool ShouldSwallow(int vk)
    {
        int mods = _mods;
        bool alt = (mods & ModAlt) != 0;
        bool ctrl = (mods & ModCtrl) != 0;
        bool win = (mods & ModWin) != 0;
        bool shift = (mods & ModShift) != 0;

        // Win 整类放行：shell 的领地
        if (win) return false;

        // Alt：窗口管理那几个（Tab/F4/Space/Esc/方向）必须留给系统，否则切不走；
        // AltGr（Ctrl+Alt）也放行，否则德语波兰语打不出 @ €。
        // 其余的 Alt+键（比如聊天框的 Alt+Enter 提交）交给网页——浏览器自己的菜单不会再抢。
        if (alt)
        {
            if (ctrl) return false;
            if (vk is Native.VK_TAB or 0x73 or Native.VK_SPACE or Native.VK_ESCAPE
                or Native.VK_LEFT or Native.VK_RIGHT or Native.VK_UP or Native.VK_DOWN) return false;
            if (IsModifierKey(vk) || IsNeverSwallow(vk)) return false;
            return true;
        }

        if (IsModifierKey(vk)) return false;
        if (IsNeverSwallow(vk)) return false;

        // 保命通道与输入法保留组合
        if (vk == Native.VK_ESCAPE && (ctrl || shift)) return false;
        if (vk == Native.VK_SPACE && ctrl) return false;               // 中英切换
        if (vk == Native.VK_ESCAPE) return false;                      // 结束接管

        // 用户要的：F1~F12 交给网页自己的脚本
        if (vk >= 0x70 && vk <= 0x87) return true;

        // 不带修饰的键一律留在原来的通道（只有那里看得见输入法吃没吃掉这个键）
        if (!ctrl && !shift) return false;

        // 纯 Shift + 可打印字符：这是大写字母和符号，必须留下
        if (!ctrl && shift && !IsFunctionKey(vk)) return false;

        return true;
    }

    private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12    // Shift / Ctrl / Alt
        or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5                         // 左右 Shift / Ctrl / Alt
        or 0x5B or 0x5C or 0xA6 or 0xA7;                                        // Win

    private static bool IsFunctionKey(int vk) => vk is Native.VK_BACK or Native.VK_TAB or Native.VK_RETURN
        or Native.VK_PRIOR or Native.VK_NEXT or Native.VK_END or Native.VK_HOME
        or Native.VK_LEFT or Native.VK_UP or Native.VK_RIGHT or Native.VK_DOWN
        or Native.VK_INSERT or Native.VK_DELETE or Native.VK_ESCAPE;

    private static bool IsNeverSwallow(int vk)
    {
        // 锁键：吞掉会让按键灯和系统状态对不上
        if (vk is 0x14 or 0x90 or 0x91) return true;
        // 媒体、音量、浏览器键、右键菜单键、休眠
        if (vk is >= 0xA6 and <= 0xB7) return true;
        if (vk is >= 0xAD and <= 0xAF) return true;
        if (vk is 0x5D or 0x5F or 0x13 or 0x2C) return true;
        // 输入法专用键与「输入法已处理」标记
        if (vk is >= 0x15 and <= 0x1F) return true;
        if (vk == 0xE5) return true;
        return false;
    }

    private void TrackModifiers(int vk, bool down)
    {
        // 0x10/0x11/0x12 是通用修饰键，0xA0~0xA5 是左右分开的那一套：
        // A0/A1 = 左右 Shift、A2/A3 = 左右 Ctrl、A4/A5 = 左右 Alt。写错一个就会「Ctrl 认不出来」。
        int bit = vk switch
        {
            0x12 or 0xA4 or 0xA5 => ModAlt,
            0x11 or 0xA2 or 0xA3 => ModCtrl,
            0x5B or 0x5C or 0xA6 or 0xA7 => ModWin,
            0x10 or 0xA0 or 0xA1 => ModShift,
            _ => 0
        };
        if (bit == 0) return;
        if (down) _mods |= bit;
        else _mods &= ~bit;
    }

    private void Forget(int vk)
    {
        _down[vk] = false;
        _held[vk] = false;
    }

    public void Dispose()
    {
        SetSession(false, IntPtr.Zero, IntPtr.Zero);
        Uninstall();
        _running = false;
        Post(0x0012 /* WM_QUIT */);
    }
}
