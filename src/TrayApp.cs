using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace WebWallpaper;

internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WebWallpaper";

    private readonly AppConfig _config;
    private CoreWebView2Environment? _environment;
    private readonly List<WallpaperWindow> _windows = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly InputRouter _input;
    private readonly FocusProxy _proxy;
    private readonly System.Windows.Forms.Timer _watchdog;
    private WallpaperWindow? _typingWindow;

    // 键盘：钩子只负责判定并投递，真正写进网页的活全部由界面线程排队做
    private readonly KeyboardHook _kbHook = new();
    private readonly ConcurrentQueue<KeyOp> _keys = new();
    private KeySink? _keySink;
    private System.Windows.Forms.Timer? _keyPump;
    private bool _keyBusy;
    private bool _caretBusy;
    private System.Windows.Forms.Timer? _caretTimer;
    private int _caretX = int.MinValue, _caretY = int.MinValue;
    private long _lastKeyStatTicks;
    private long _expiredKeys;

    private string _monitorSignature = "";
    private string _workSignature = "";
    private IntPtr _progmanAtBuild;
    private bool _busy;
    private long _lastZLogTicks;
    private long _lastWheelLog;

    public TrayApp()
    {
        _config = AppConfig.LoadOrCreate();

        _autostartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutostart()) { Checked = IsAutostartEnabled() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("网页壁纸已运行") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("点网页输入框就能直接打字，Esc 结束") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("重新加载配置", null, (_, _) => ReloadConfig()));
        menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenConfig()));
        menu.Items.Add(new ToolStripMenuItem("结束编辑（退回桌面图标）", null, (_, _) => LeaveAllEditModes()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出（恢复原壁纸）", null, (_, _) => Shutdown()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "网页壁纸",
            Visible = true,
            ContextMenuStrip = menu
        };

        // 钩子里只入队，绝不做跨进程调用：否则按住左键时系统会被我们卡住，鼠标像被钉住
        _input = new InputRouter(
            WindowAt,
            (w, x, y, pressed) => QueueMove(w, x, y, pressed),
            (w, x, y) => QueueOp(() => w.PageMouseDown(x, y, NextClickCount(x, y))),
            (w, x, y) => QueueOp(() => w.PageMouseUp(x, y, _clickCount)),
            w => { _moveTarget = null; QueueOp(w.PageMouseLeave); },
            (w, x, y, dx, dy) => QueueOp(() => OnWheel(w, x, y, dx, dy)),
            (w, x, y) => QueueOp(() => OnForwardedClick(w, x, y)));
        _input.WheelForwarding = _config.WheelScroll;

        _mousePump = new System.Windows.Forms.Timer { Interval = 8 };
        _mousePump.Tick += (_, _) => PumpMouse();
        _mousePump.Start();

        _proxy = new FocusProxy();
        _proxy.ShiftTogglesIme = _config.ImeShiftToggle;
        _proxy.TextTyped += text => EnqueueKey(KeyOp.TextOp(text));
        _proxy.RawKey += (vk, mods) => SendPair(vk, mods);
        _proxy.Released += () =>
        {
            ReleaseTyping();
        };
        _proxy.FocusLost += () => ReleaseTyping();
        // 不再把拼音内联写进网页：输入法会在插入点自己画组字（和正常软件一样），
        // 两边都画就会出现两份拼音叠在一起。
        _proxy.CompositionChanged += _ => { };
        // 上屏文字已经由 TextTyped 送进网页了，这里再送一遍就会看到「打一遍出现两遍」
        _proxy.CompositionCommitted += _ => { };

        _keySink = new KeySink(_keys, () => ReleaseTyping(), () => LeaveAllEditModes(), () => ReloadAll());
        _keyPump = new System.Windows.Forms.Timer { Interval = 8 };
        _keyPump.Tick += (_, _) => PumpKeys();
        _keyPump.Start();

        // 焦点可能在网页内部自己变（Tab、脚本设焦点、点链接），
        // 所以接管期间每隔一会儿问一句「现在在编辑哪里」，变了就把隐形窗口挪过去。
        _caretTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _caretTimer.Tick += (_, _) => TrackCaret();

        try
        {
            Native.RegisterHotKey(_keySink.Handle, 1,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, (uint)Keys.K);
        }
        catch { }

        _watchdog = new System.Windows.Forms.Timer { Interval = 700 };
        _watchdog.Tick += (_, _) => Watchdog();
        _watchdog.Start();

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebWallpaper", "WebView2");
            Directory.CreateDirectory(dataDir);
            var options = new CoreWebView2EnvironmentOptions
            {
                // 窗口被桌面图标盖住时，Chromium 会误判“被遮挡”而停绘，表现为偶尔闪回原桌面
                AdditionalBrowserArguments =
                    // 窗口被桌面图标盖住时，Chromium 会误判“被遮挡”而停绘，表现为偶尔闪回原桌面
                    "--disable-features=CalculateNativeWinOcclusion " +
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding"
            };
            _environment = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        }
        catch (Exception ex)
        {
            Log.Write("创建 WebView2 环境失败：" + ex.Message);
            _tray.ShowBalloonTip(5000, "网页壁纸", "网页组件启动失败：" + ex.Message, ToolTipIcon.Error);
            return;
        }

        BuildWindows();
        ShowBalloon("网页壁纸已铺好。点桌面上的网页按钮、输入框可直接用；输入框点亮后敲字即可，Esc 结束输入。");;
    }

    private void BuildWindows()
    {
        LeaveAllEditModes();
        ReleaseTyping();
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();

        DesktopHost.SpawnWallpaperLayer();

        var screens = Screen.AllScreens;
        var virtualScreen = SystemInformation.VirtualScreen;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var cfg = _config.ForScreen(screen, i);
            if (cfg == null || !cfg.Enabled) continue;

            var url = string.IsNullOrWhiteSpace(cfg.Url) ? _config.DefaultUrl : cfg.Url.Trim();
            if (!AppConfig.IsWebUrl(url)) continue;

            var bounds = ScreenBounds(screen, virtualScreen);

            var window = new WallpaperWindow(screen, bounds, url, cfg.Zoom, _environment!);
            window.ExitRequested += LeaveEditMode;
            _windows.Add(window);
            window.Form.Show();
            window.RefreshMetrics();
        }

        _monitorSignature = MonitorSignature();
        _workSignature = WorkAreaSignature();
        _progmanAtBuild = DesktopHost.Progman;
        Log.Write($"已铺 {_windows.Count} 块屏");
    }

    /// <summary>壁纸只铺到屏幕的工作区（扣掉任务栏），任务栏就不会压住网页底部。</summary>
    private static Rectangle ScreenBounds(Screen screen, Rectangle virtualScreen)
    {
        var work = screen.WorkingArea;
        if (work.Width <= 0 || work.Height <= 0) work = screen.Bounds;
        return new Rectangle(work.X - virtualScreen.X, work.Y - virtualScreen.Y, work.Width, work.Height);
    }

    private void RepositionWindows()
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        foreach (var w in _windows)
        {
            if (w.IsDisposed) continue;
            w.SetBounds(ScreenBounds(w.Screen, virtualScreen));
        }
        Log.Write("任务栏或工作区变化，重新贴合");
    }

    private string MonitorSignature() =>
        string.Join("|", Screen.AllScreens.Select(s => $"{s.DeviceName}:{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}"));

    private string WorkAreaSignature() =>
        string.Join("|", Screen.AllScreens.Select(s => $"{s.DeviceName}:{s.WorkingArea.X},{s.WorkingArea.Y},{s.WorkingArea.Width},{s.WorkingArea.Height}"));

    private WallpaperWindow? WindowAt(Point screenPoint)
    {
        foreach (var w in _windows)
        {
            if (!w.IsDisposed && w.Screen.Bounds.Contains(screenPoint)) return w;
        }
        return null;
    }

    private readonly Queue<Action> _mouseOps = new();
    private System.Windows.Forms.Timer? _mousePump;
    // 钩子在自己的线程上写这几个字段，界面线程靠 volatile 才能看见
    private volatile WallpaperWindow? _moveTarget;
    private volatile int _moveX;
    private volatile int _moveY;
    private volatile bool _movePressed;
    private volatile bool _moveDirty;

    private void QueueOp(Action op)
    {
        lock (_mouseOps)
        {
            if (_mouseOps.Count >= 256) return;
            _mouseOps.Enqueue(op);
        }
    }

    private void QueueMove(WallpaperWindow w, int x, int y, bool pressed)
    {
        _moveTarget = w;
        _moveX = x;
        _moveY = y;
        _movePressed = pressed;
        _moveDirty = true;
    }

    private void PumpMouse()
    {
        for (int i = 0; i < 32; i++)
        {
            Action? op = null;
            lock (_mouseOps)
            {
                if (_mouseOps.Count == 0) break;
                op = _mouseOps.Dequeue();
            }
            try { op(); } catch { }
        }

        if (!_moveDirty) return;
        _moveDirty = false;
        var target = _moveTarget;
        if (target == null || target.IsDisposed) return;
        try { target.PageMouseMove(_moveX, _moveY, _movePressed); } catch { }
    }

    private long _lastDownTicks = -1000;
    private int _lastDownX, _lastDownY;
    private int _clickCount = 1;

    /// <summary>双击判定：400ms 内、同一小片区域连按两次。</summary>
    private int NextClickCount(int x, int y)
    {
        long now = Environment.TickCount64;
        bool again = now - _lastDownTicks < 400 &&
                     Math.Abs(x - _lastDownX) < 6 && Math.Abs(y - _lastDownY) < 6;
        _clickCount = again ? 2 : 1;
        _lastDownTicks = now;
        _lastDownX = x;
        _lastDownY = y;
        return _clickCount;
    }

    private void OnWheel(WallpaperWindow window, int clientX, int clientY, int deltaX, int deltaY)
    {
        if (!_config.WheelScroll) return;
        if (window.IsDisposed || window.View.CoreWebView2 == null) return;
        if (Environment.TickCount64 - _lastWheelLog > 1000)
        {
            _lastWheelLog = Environment.TickCount64;
            Log.Write($"[wheel] {window.Screen.DeviceName} {clientX},{clientY} dy={deltaY}");
        }
        double scale = Native.GetDpiForWindow(window.View.Handle) / 96.0;
        if (scale <= 0) scale = 1.0;
        double zoom = window.View.ZoomFactor <= 0 ? 1.0 : window.View.ZoomFactor;
        double x = clientX / scale / zoom;
        double y = clientY / scale / zoom;
        double cdpX = deltaX * (100.0 / 120.0);
        double cdpY = -deltaY * (100.0 / 120.0);
        var json = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{{\"type\":\"mouseWheel\",\"x\":{0:0.##},\"y\":{1:0.##},\"deltaX\":{2:0.##},\"deltaY\":{3:0.##}}}",
            x, y, cdpX, cdpY);
        try
        {
            _ = window.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json);
        }
        catch { }
    }

    private async void OnForwardedClick(WallpaperWindow window, int clientX, int clientY)
    {
        if (window.IsDisposed) return;

        // 光标要挪走了：先让输入法把还没上屏的拼音提交掉。
        // 正常软件就是这个顺序，跳过它会看到候选框赖在原地、跟着鼠标乱跳。
        _proxy.FinishComposition();
        // 提交是键盘动作，输入法要几帧才处理完；这段没等够，组字会跟着窗口一起丢
        await Task.Delay(70);

        // 原生焦点路线：把网页窗口升成真窗口，让输入法直接作用在网页上。
        // 这时鼠标也由系统直接投递，转发必须停手，否则一次点击会变成两次。
        if (_config.NativeFocus)
        {
            try
            {
                _typingWindow = window;
                _input.Editing = _config.LiftWindow;
                if (!window.EnterEditMode(_config.LiftWindow))
                {
                    _input.Editing = false;
                    _typingWindow = null;
                    return;
                }
                // 编辑态只盯 Esc：其余键全部交给输入法和网页自己
                _kbHook.SetSession(false, IntPtr.Zero, _keySink?.Handle ?? IntPtr.Zero);
                _kbHook.SetEscapeWatch(true, window.Form.Handle);
                _kbHook.Install();
            }
            catch (Exception ex)
            {
                Log.Write("进入编辑态失败：" + ex.Message);
                _input.Editing = false;
            }
            return;
        }

        try
        {
            // 先把键盘接过来再查标签：查标签是一次跨进程往返，
            // 这几毫秒里敲的字以前会掉进桌面，用户看到的就是「点了却打不上字」
            _typingWindow = window;
            var p = new Native.POINT(clientX, clientY);
            Native.ClientToScreen(window.ChromeHandle, ref p);
            _proxy.Grab(p.X, p.Y);
            _kbHook.SetSession(true, _proxy.Handle, _keySink?.Handle ?? IntPtr.Zero);
            if (_config.KeyboardHook) _kbHook.Install();

            _caretX = _caretY = int.MinValue;
            _caretTimer?.Start();

            var tag = await window.TagAtAsync(clientX, clientY);
            Log.Write($"[click] {clientX},{clientY} tag={tag} chrome={window.CachedChrome}");

            // 网页可能把焦点给了别的元素（或者输入框左边还有内边距），
            // 以「当前焦点元素」为准把隐形窗口挪过去，候选框才不会偏到最后点击的位置。
            var caret = await window.FocusRectScreenPointAsync();
            if (caret.HasValue)
            {
                _proxy.MoveTo(caret.Value.X, caret.Value.Y);
                Log.Write($"[input] 候选框定位到 {caret.Value.X},{caret.Value.Y} tag={tag}");
            }
            else
            {
                Log.Write($"[input] 接管键盘 tag={tag} at {p.X},{p.Y}（未取到焦点元素位置）");
            }
        }
        catch (Exception ex)
        {
            Log.Write("点击后处理失败：" + ex.Message);
        }
    }

    private readonly record struct KeyOp(bool IsText, int Vk, int Mods, bool Down, string? Text, long Ticks)
    {
        public static KeyOp Key(int vk, int mods, bool down) =>
            new(false, vk, mods, down, null, Environment.TickCount64);
        public static KeyOp TextOp(string text) =>
            new(true, 0, 0, false, text, Environment.TickCount64);
    }

    /// <summary>只接收窗口消息（页面写入与按键投递），保证所有写入都排在界面线程的同一个队列里。</summary>
    private sealed class KeySink : NativeWindow
    {
        private readonly ConcurrentQueue<KeyOp> _queue;
        private readonly Action _escape;
        private readonly Action _exitEdit;
        private readonly Action _reload;

        public KeySink(ConcurrentQueue<KeyOp> queue, Action escape, Action exitEdit, Action reload)
        {
            _queue = queue;
            _escape = escape;
            _exitEdit = exitEdit;
            _reload = reload;
            CreateHandle(new CreateParams
            {
                Caption = "WebWallpaperKeySink",
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                Style = unchecked((int)0x80000000)   // WS_POPUP：不占桌面，也不出现在任务栏
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == KeyboardHook.WM_APP_KEY)
            {
                int vk = (int)m.WParam;
                long payload = m.LParam.ToInt64();
                bool down = (payload & 1) != 0;
                int mods = (int)((payload >> 1) & 0xF);
                _queue.Enqueue(KeyOp.Key(vk, mods, down));
                return;
            }
            if (m.Msg == KeyboardHook.WM_APP_RELOAD)
            {
                Log.Write("[edit] 编辑态 F5/Ctrl+R，刷新页面");
                _reload?.Invoke();
                return;
            }
            if (m.Msg == KeyboardHook.WM_APP_ESCAPE)
            {
                Log.Write("[edit] 编辑态按 Esc，退出");
                _exitEdit();
                return;
            }
            if (m.Msg == Native.WM_HOTKEY)
            {
                Log.Write("逃生热键：把键盘还给系统");
                _escape();
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>兜底路径（钩子未接管时）：一次把按下与松开都发完。</summary>
    private async void SendPair(int vk, int mods)
    {
        var w = _typingWindow;
        if (w == null || w.IsDisposed) return;
        _proxy.MarkComboTyping();
        try
        {
            await w.SendKeyAsync(vk, mods, true);
            await w.SendKeyAsync(vk, mods, false);
            if (vk == 0x09)
            {
                await Task.Delay(60);
                TrackCaret();
            }
        }
        catch (Exception ex) { Log.Write("[key] 兜底发送失败：" + ex.Message); }
    }

    private void EnqueueKey(KeyOp op)
    {
        if (_keys.Count > 64) { Interlocked.Increment(ref _expiredKeys); return; }
        _keys.Enqueue(op);
    }

    /// <summary>一次只发一条，按到达顺序执行；任何一条超时或前台变了就丢掉并交还键盘，不重试。</summary>
    private async void PumpKeys()
    {
        if (_keyBusy) return;
        if (!_keys.TryDequeue(out var op)) return;

        // 过期的键不能再往网页里写：用户可能已经切到别的程序了
        if (Environment.TickCount64 - op.Ticks > 300)
        {
            Interlocked.Increment(ref _expiredKeys);
            _kbHook.Ack();
            return;
        }

        var window = _typingWindow;
        var proxy = _proxy.Handle;
        if (window == null || window.IsDisposed || Native.GetForegroundWindow() != proxy)
        {
            _kbHook.Ack();
            ReleaseTyping();
            return;
        }

        _keyBusy = true;
        try
        {
            // 组合键之前必须先把还没落字的文字写进去，否则粘贴会跑到文字前面
            _proxy.FlushNow();

            if (op.IsText)
            {
                if (!string.IsNullOrEmpty(op.Text))
                {
                    await window.InsertTextAsync(op.Text!);
                    // 文字上屏后插入点右移了，候选框要跟着挪，否则会一直停在输入框左边
                    await TrackCaretNow(window);
                }
            }
            else
            {
                // F5 / Ctrl+R：浏览器层的刷新被关掉了，转发给网页不会刷新。
                // 这里直接重载页面，效果和浏览器里按 F5 一致。
                bool plainF5 = op.Vk == 0x74 && (op.Mods & (KeyboardHook.ModCtrl | KeyboardHook.ModAlt)) == 0;
                bool ctrlR = op.Vk == 0x52 && (op.Mods & KeyboardHook.ModCtrl) != 0;
                if (plainF5 || ctrlR)
                {
                    if (op.Down) ReloadAll();
                }
                else
                {
                    _proxy.MarkComboTyping();
                    await window.SendKeyAsync(op.Vk, op.Mods, op.Down);

                    // Tab 换焦点之后焦点元素变了，候选框要跟着走
                    if (op.Vk == 0x09)
                    {
                        await Task.Delay(60);
                        TrackCaret();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("[key] 转发失败：" + ex.Message);
        }
        finally
        {
            _keyBusy = false;
            _kbHook.Ack();
            LogKeyStats();
        }
    }

    private async void TrackCaret()
    {
        if (_caretBusy) return;
        var w = _typingWindow;
        if (w == null || w.IsDisposed) return;
        if (Native.GetForegroundWindow() != _proxy.Handle) return;

        _caretBusy = true;
        try
        {
            var caret = await w.FocusRectScreenPointAsync();
            if (!caret.HasValue) return;
            int x = caret.Value.X, y = caret.Value.Y;
            if (x == _caretX && y == _caretY) return;
            _caretX = x;
            _caretY = y;
            _proxy.MoveTo(x, y);
            Log.Write($"[input] 候选框跟随到 {x},{y}");
        }
        catch { }
        finally
        {
            _caretBusy = false;
        }
    }

    /// <summary>立刻按当前插入点位置重新摆放隐形窗口（不等下一轮轮询）。</summary>
    private async Task TrackCaretNow(WallpaperWindow w)
    {
        try
        {
            var caret = await w.FocusRectScreenPointAsync();
            if (!caret.HasValue) return;
            if (caret.Value.X == _caretX && caret.Value.Y == _caretY) return;
            _caretX = caret.Value.X;
            _caretY = caret.Value.Y;
            _proxy.MoveTo(_caretX, _caretY);
        }
        catch { }
    }

    private void LeaveEditMode(WallpaperWindow window)
    {
        if (!window.EditMode) return;
        _kbHook.SetEscapeWatch(false, IntPtr.Zero);
        _kbHook.Uninstall();
        window.ExitEditMode();
        _input.Editing = false;
        if (ReferenceEquals(_typingWindow, window)) _typingWindow = null;
    }

    private void LeaveAllEditModes()
    {
        foreach (var w in _windows)
        {
            if (w.EditMode) LeaveEditMode(w);
        }
        _input.Editing = false;
    }

    private void ReloadAll()
    {
        int n = 0;
        foreach (var w in _windows)
        {
            if (w.IsDisposed) continue;
            w.Reload();
            n++;
        }
        Log.Write($"[key] F5/Ctrl+R 重载 {n} 块屏");
    }

    private long _lastMouseLog;
    private long _lastMouseCount;

    private void LogMouseStats()
    {
        long now = Environment.TickCount64;
        if (now - _lastMouseLog < 3000) return;
        long c = _input.EventCount;
        if (c == _lastMouseCount) return;
        _lastMouseCount = c;
        _lastMouseLog = now;
        // 只在真的出问题时记：没有窗口 / 不是桌面 / 压在图标上
        if (_input.DiagReason == 1) Log.Write($"[mouse] 事件={c} 点击未转发原因={_input.DiagReason} {_input.DiagInfo}");
    }

    private long _lastGateLog;

    /// <summary>接管期间核对「前台到底是不是那个隐形窗口」：吞键的唯一许可就该是它。</summary>
    private void LogGate()
    {
        if (_typingWindow != null && _typingWindow.EditMode) return;
        if (_typingWindow == null) return;
        long now = Environment.TickCount64;
        if (now - _lastGateLog < 2000) return;
        _lastGateLog = now;
        var fg = Native.GetForegroundWindow();
        var proxy = _proxy.Handle;
        if (fg == proxy && _proxy.Visible) return;   // 正常情况不写，免得日志刷爆
        Log.Write($"[gate] 前台={fg} 代理={proxy} 一致={fg == proxy} 可见={_proxy.Visible} " +
                  $"诊断 vk={_kbHook.Diag_vk} mods={_kbHook.Diag_mods} 原因={_kbHook.Diag_why}{_kbHook.TraceText()}");
    }

    private long _lastKeySeq;

    private void LogKeyStats()
    {
        long now = Environment.TickCount64;
        if (now - _lastKeyStatTicks < 30_000) return;
        long seq = _kbHook.Seq;
        if (seq == _lastKeySeq) return;
        _lastKeySeq = seq;
        _lastKeyStatTicks = now;
        long expired = Interlocked.Read(ref _expiredKeys);
        Log.Write($"[kb] 事件={seq} 吞={_kbHook.Swallowed} 放={_kbHook.Passed} " +
                  $"投递失败={_kbHook.PostFailed} 过期={expired} " +
                  $"回调峰值={_kbHook.MaxCallbackMicros}us 安装={_kbHook.Installed}");
    }

    private bool _releasing;

    /// <summary>把键盘还给系统：先停止吞键，再卸钩子，最后释放焦点窗口。</summary>
    private void ReleaseTyping()
    {
        if (_releasing) return;
        _releasing = true;
        try
        {
            try { _caretTimer?.Stop(); } catch { }
            _caretX = _caretY = int.MinValue;
            _kbHook.SetSession(false, IntPtr.Zero, IntPtr.Zero);
            _kbHook.Uninstall();
            while (_keys.TryDequeue(out _)) _kbHook.Ack();
            _typingWindow = null;
            try { _proxy.Release(); } catch { }
        }
        finally
        {
            _releasing = false;
        }
    }

    private static readonly HashSet<string> TypingInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "search", "email", "password", "number", "tel", "url", "date", "datetime-local", "time", "month", "week"
    };

    private static bool IsTextInput(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (tag.StartsWith("TEXTAREA", StringComparison.Ordinal) ||
            tag.Contains("editable", StringComparison.Ordinal) ||
            tag.Contains("textbox", StringComparison.Ordinal)) return true;
        if (!tag.StartsWith("INPUT", StringComparison.Ordinal)) return false;

        int colon = tag.IndexOf(':');
        if (colon < 0) return true;                       // 老页面拿不到 type 时按文字框处理
        return TypingInputTypes.Contains(tag[(colon + 1)..]);
    }

    private void Watchdog()
    {
        if (_busy || _environment == null) return;
        _busy = true;
        try
        {
            LogKeyStats();
            LogGate();
            LogMouseStats();

            var progman = DesktopHost.Progman;
            if (progman == IntPtr.Zero) return;

            if (_progmanAtBuild != IntPtr.Zero && progman != _progmanAtBuild)
            {
                Log.Write("Progman 已重建，重新铺桌面");
                BuildWindows();
                return;
            }

            if (MonitorSignature() != _monitorSignature)
            {
                Log.Write("屏幕布局变化，重建");
                BuildWindows();
                return;
            }

            if (WorkAreaSignature() != _workSignature)
            {
                _workSignature = WorkAreaSignature();
                RepositionWindows();
            }

            var defView = DesktopHost.DefView(progman);
            var broken = new List<WallpaperWindow>();
            foreach (var w in _windows)
            {
                // 编辑态的窗口已经升成普通顶层窗口，本来就不该挂在桌面层下
                if (w.EditMode) continue;

                w.RefreshMetrics();
                if (!Native.IsWindow(w.Form.Handle))
                {
                    Log.Write("壁纸窗口已消失，重建");
                    BuildWindows();
                    return;
                }

                if (!DesktopHost.IsAttached(w.Form.Handle))
                {
                    Log.Write("窗口脱离桌面层，重新贴回");
                    w.Reattach();
                }
                else if (defView != IntPtr.Zero && !ZOrderOk(w, defView))
                {
                    broken.Add(w);
                }
            }

            // 多块屏的壁纸窗是同一层叠的：只要它们连着排在 DefView 下面就算正常，
            // 不再每轮都把窗口重插一次——反复 SetWindowPos 会让网页闪一帧。
            for (int i = broken.Count - 1; i >= 0; i--)
            {
                broken[i].ReapplyZOrder();
            }
            if (broken.Count > 0 && Environment.TickCount64 - _lastZLogTicks > 30_000)
            {
                _lastZLogTicks = Environment.TickCount64;
                Log.Write("修正桌面层顺序：" + string.Join(",", broken.Select(b => b.Screen.DeviceName)));
            }
        }
        catch (Exception ex)
        {
            Log.Write("守护检查失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 壁纸窗在桌面层里的位置是否正常：往上只允许压着其他壁纸窗，最终必须碰到 DefView。
    /// 多屏时它们本来就是互相叠着的，所以不能拿“上面必须正好是 DefView”来判断。
    /// </summary>
    private bool ZOrderOk(WallpaperWindow w, IntPtr defView)
    {
        var prev = Native.GetWindow(w.Form.Handle, Native.GW_HWNDPREV);
        for (int guard = 0; guard < 16; guard++)
        {
            if (prev == defView) return true;
            if (prev == IntPtr.Zero) return false;

            WallpaperWindow? above = null;
            foreach (var o in _windows)
            {
                if (!o.IsDisposed && o.Form.Handle == prev) { above = o; break; }
            }
            if (above == null) return false;
            prev = Native.GetWindow(prev, Native.GW_HWNDPREV);
        }
        return false;
    }

    private void ReloadConfig()
    {
        var fresh = AppConfig.LoadOrCreate();
        _config.DefaultUrl = fresh.DefaultUrl;
        _config.Screens = fresh.Screens;
        _config.WheelScroll = fresh.WheelScroll;
        _config.ImeShiftToggle = fresh.ImeShiftToggle;
        _config.KeyboardHook = fresh.KeyboardHook;
        _config.NativeFocus = fresh.NativeFocus;
        _input.WheelForwarding = _config.WheelScroll;
        _proxy.ShiftTogglesIme = _config.ImeShiftToggle;
        ReleaseTyping();          // 先把还没写进去的文字提交，再重建窗口
        BuildWindows();
        ShowBalloon("配置已重新加载");
    }

    private static void OpenConfig()
    {
        try
        {
            if (!File.Exists(AppConfig.FilePath)) AppConfig.LoadOrCreate();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + AppConfig.FilePath + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("打开配置失败：" + ex.Message);
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string s && s.Length > 0;
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (IsAutostartEnabled())
            {
                key.DeleteValue(RunValue, false);
                _autostartItem.Checked = false;
            }
            else
            {
                key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
                _autostartItem.Checked = true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("开机自启设置失败：" + ex.Message);
        }
    }

    private void ShowBalloon(string text)
    {
        try { _tray.ShowBalloonTip(4000, "网页壁纸", text, ToolTipIcon.Info); } catch { }
    }

    private void Shutdown()
    {
        Log.Write("退出");
        // 先交出键盘再拆窗口：顺序反了的话，退出那一瞬敲的键会被吞掉后无处可送
        LeaveAllEditModes();
        ReleaseTyping();
        try { Native.UnregisterHotKey(_keySink?.Handle ?? IntPtr.Zero, 1); } catch { }
        _keyPump?.Stop();
        _mousePump?.Stop();
        _watchdog.Stop();
        _input.Dispose();
        _kbHook.Dispose();
        try { _proxy.Dispose(); } catch { }
        try { _keySink?.DestroyHandle(); } catch { }
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();

        var prog = DesktopHost.Progman;
        if (prog != IntPtr.Zero) Native.InvalidateRect(prog, IntPtr.Zero, true);

        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
