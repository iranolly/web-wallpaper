using System.Text;

namespace WebWallpaper;

/// <summary>
/// 看不见的焦点窗口（2x2 像素、几乎全透明、鼠标穿透）。
/// 桌面层里的网页永远拿不到系统焦点，所以让它替网页接键盘：
/// 输入法的候选框会出现在鼠标点击的位置，组好的字通过 TextTyped 交给网页。
/// </summary>
internal sealed class FocusProxy : Form
{
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    private readonly System.Windows.Forms.Timer _flush = new() { Interval = 60 };
    private readonly StringBuilder _pending = new();

    private bool _shiftDown;
    private bool _shiftOtherKey;
    private long _lastImeLog;
    private long _lastTypedTicks;
    private int _lastKnownConv = -1;
    private int _savedConv = -1;

    /// <summary>
    /// 一个 1 像素宽的真文本框。它存在的唯一理由是：输入法只给「有文本框的窗口」画候选框，
    /// 并且靠文本框的光标位置算候选框该出现在哪。窗口一挪，光标跟着挪，候选框自己就跟上了。
    /// </summary>
    private sealed class ShadowBox : TextBox
    {
        public event Action<string>? CompositionChanged;
        public event Action<string>? CompositionCommitted;
        public event Action<int, int>? EditKey;
        /// <summary>组字中：这时退格/方向键是给输入法用的，不能抢。</summary>
        public bool Composing;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000020;          // WS_EX_TRANSPARENT：不挡鼠标
                cp.ExStyle |= Native.WS_EX_LAYERED; // 自己带一层透明度，父窗口的透明管不到子窗口
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 完全透明：输入法要的「光标位置」它照样算得出来，但屏幕上一点都看不到
            Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == Native.WM_IME_STARTCOMPOSITION)
            {
                Composing = true;
                CompositionChanged?.Invoke("");
            }
            else if (m.Msg == Native.WM_IME_COMPOSITION)
            {
                var imc = Native.ImmGetContext(Handle);
                if (imc != IntPtr.Zero)
                {
                    try
                    {
                        if ((m.LParam.ToInt64() & Native.GCS_COMPSTR) != 0)
                            CompositionChanged?.Invoke(ReadString(imc, Native.GCS_COMPSTR));
                        if ((m.LParam.ToInt64() & Native.GCS_RESULTSTR) != 0)
                            CompositionCommitted?.Invoke(ReadString(imc, Native.GCS_RESULTSTR));
                    }
                    finally { Native.ImmReleaseContext(Handle, imc); }
                }
            }
            else if (m.Msg == Native.WM_IME_ENDCOMPOSITION)
            {
                Composing = false;
                CompositionChanged?.Invoke("");
            }
        }

        private static string ReadString(IntPtr imc, int index)
        {
            int len = Native.ImmGetCompositionString(imc, index, null, 0);
            if (len <= 0) return "";
            var sb = new System.Text.StringBuilder(len / 2 + 1);
            Native.ImmGetCompositionString(imc, index, sb, len);
            return sb.ToString();
        }
    }

    /// <summary>Shift 切换输入法中/英文（输入法自己不响应时由本程序代劳）。</summary>
    public bool ShiftTogglesIme = true;

    /// <summary>一段可直接插入网页的文字（已包含输入法组好的字）。</summary>
    public event Action<string>? TextTyped;
    /// <summary>需要交给网页处理的按键：按下与松开一次发完，附带修饰位（Alt=1/Ctrl=2/Shift=8）。</summary>
    public event Action<int, int>? RawKey;
    public event Action? Released;
    /// <summary>失焦：用户已经离开网页，界面线程据此把键盘还回去。</summary>
    public event Action? FocusLost;
    /// <summary>组字中（拼音还没上屏）：把这段拼音同步给网页，让它在文本框里内联显示。</summary>
    public event Action<string>? CompositionChanged;
    /// <summary>组字结束、上屏：这段中文要代替刚才的拼音落进网页。</summary>
    public event Action<string>? CompositionCommitted;

    /// <summary>
    /// 影子文本框：输入法需要「有个真文本框」才知道光标在哪一行，
    /// 否则它拿不到位置，候选框要么不画、要么画到屏幕角落。
    /// 它只有 1 像素宽、几乎透明，你看不见，但输入法认得。
    /// </summary>
    private readonly ShadowBox _shadow = new();
    /// <summary>清空影子框时不要把它当成"用户打了字"再发一遍。</summary>
    private bool _muteShadow;

    private bool _grabbing;
    private bool _composing;
    private long _lastImeCommitTicks;

    public FocusProxy()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // 不要置顶：它自己看不见，置顶只会让旁边的程序操作变得难受
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(240, 28);
        BackColor = Color.Black;
        Opacity = 0.01;
        // 影子框自己带透明度，父窗口的透明对子窗口不生效

        // 诊断用：正常的输入框尺寸，位置留给 Form 决定
        _shadow.Bounds = new Rectangle(0, 0, 200, 24);
        _shadow.Multiline = true;
        _shadow.BorderStyle = BorderStyle.None;
        _shadow.BackColor = Color.Black;
        _shadow.ForeColor = Color.FromArgb(28, 28, 28);
        _shadow.ScrollBars = ScrollBars.None;
        _shadow.TextChanged += OnShadowText;
        _shadow.KeyDown += OnShadowKeyDown;
        _shadow.CompositionChanged += s2 =>
        {
            _composing = !string.IsNullOrEmpty(s2);
            CompositionChanged?.Invoke(s2);
        };
        _shadow.CompositionCommitted += s2 => CompositionCommitted?.Invoke(s2);
        Controls.Add(_shadow);
        _flush.Tick += (_, _) => Flush();
        Deactivate += (_, _) =>
        {
            // Grab 期间会冒出杂散失焦消息，那时窗口正在建立，不能当成用户离开
            if (_grabbing) return;
            _flush.Stop();
            Flush();          // 先提交还没落字的文本：以前这里直接 Clear，最后几个字会静默消失
            // 不 Hide，只移出屏幕：Hide/Show 会让窗口句柄与输入法上下文的绑定断开，
            // 下次点回来输入法就退回默认的英文状态。
            Location = new Point(-32000, -32000);
            FocusLost?.Invoke();
        };
    }

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT（鼠标穿透）
            return cp;
        }
    }

    public void Grab(int screenX, int screenY)
    {
        _grabbing = true;
        try
        {
            // 先看一眼用户此刻在原窗口里的中英状态，接管后照原样保持——不是「记住上次」，是「跟着系统现在的状态」
            int inherited = ReadForegroundConversion();
            Location = new Point(screenX, screenY);
            _shiftDown = false;
            _shiftOtherKey = false;
            _lastTypedTicks = 0;
            _pending.Clear();
            Show();
            ForceForeground();

            // 把键盘焦点交给影子文本框：输入法认它，才拿得到光标位置
            _muteShadow = true;
            try { _shadow.Clear(); } finally { _muteShadow = false; }
            _shadow.Focus();

        string mode = "";
        if (TryGetConversion(out int conv))
            mode = (conv & Native.IME_CMODE_NATIVE) != 0 ? "（输入法 中文）" : "（输入法 英文）";

        // 系统没给这个窗口挂输入法上下文时，自己挂一个：否则读不到中/英状态，Shift 也切不动。
        ReadConv();
        if (inherited >= 0) RestoreConv(inherited);
        else if (_savedConv >= 0) RestoreConv(_savedConv);
        Log.Write($"键盘接管于 {screenX},{screenY}{mode} 代理位置={Bounds}");
        }
        finally
        {
            _grabbing = false;
        }
    }

    /// <summary>
    /// 把输入法的组字状态同步给网页：拼音阶段把拼音写进文本框（内联组字，和原生一样），
    /// 上屏时提交真正的中文。没有这一步，拼音只会画在输入法自己的小窗里，看着就不像原生。
    /// </summary>
    private void HandleComposition(Message m)
    {
        var imc = Native.ImmGetContext(Handle);
        if (imc == IntPtr.Zero) return;
        try
        {
            int flags = m.LParam.ToInt32();
            if ((flags & Native.GCS_RESULTSTR) != 0)
            {
                var s = ReadCompositionString(imc, Native.GCS_RESULTSTR);
                _composing = false;
                _lastImeCommitTicks = Environment.TickCount64;
                if (!string.IsNullOrEmpty(s)) CompositionCommitted?.Invoke(s);
                else CompositionChanged?.Invoke("");
                return;
            }
            if ((flags & Native.GCS_COMPSTR) != 0)
            {
                var s = ReadCompositionString(imc, Native.GCS_COMPSTR);
                CompositionChanged?.Invoke(s);
            }
        }
        finally
        {
            Native.ImmReleaseContext(Handle, imc);
        }
    }

    /// <summary>影子框里出现新文字：那就是输入法上屏的内容，原样交给网页，然后清空。</summary>
    private void OnShadowText(object? sender, EventArgs e)
    {
        if (_muteShadow) return;
        var text = _shadow.Text;
        if (string.IsNullOrEmpty(text)) return;

        _muteShadow = true;
        try { _shadow.Clear(); }
        finally { _muteShadow = false; }

        // 文本框里的换行不能当成"文字"写进网页：那样只会换行，发不出去。
        // 回车是一个按键动作，得让页面自己决定是发送还是换行。
        if (text.IndexOf((char)13) >= 0 || text.IndexOf((char)10) >= 0) { RawKey?.Invoke(0x0D, 0); return; }

        _lastTypedTicks = Environment.TickCount64;
        _lastImeCommitTicks = _lastTypedTicks;
        TextTyped?.Invoke(text);
    }

    /// <summary>影子框也要服从"组合键交给网页"的分工。</summary>
    private void OnShadowKeyDown(object? sender, KeyEventArgs e)
    {
        int vk = (int)e.KeyCode;

        // 退格、删除、方向键、Home/End：这些是编辑动作，得交给网页去改文本。
        // 不转发的话，影子框自己把它们吃掉了，网页里的字删不掉也动不了。
        if (!_shadow.Composing &&
            (vk == 0x08 || vk == 0x2E || (vk >= 0x23 && vk <= 0x28)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _lastTypedTicks = Environment.TickCount64;
            RawKey?.Invoke(vk, 0);
            return;
        }
        int mods = 0;
        if (e.Control) mods |= 2;
        if (e.Alt) mods |= 1;
        if (e.Shift) mods |= 8;

        // Shift+Enter：页面要的是"换行"，键本身得带修饰位送过去
        if (e.Shift && vk == 0x0D)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            RawKey?.Invoke(0x0D, 8);
            return;
        }

        // 没有 Ctrl/Alt 的按键全部交给文本框自己处理：
        // 它会按当前输入法状态产生正确的字符（Shift+= 出 +、Shift+1 出 !），
        // 抢过来自己发反而发不出符号。
        if (!e.Control && !e.Alt) return;

        e.Handled = true;
        e.SuppressKeyPress = true;
        _lastTypedTicks = Environment.TickCount64;
        RawKey?.Invoke(vk, mods);
    }

    private static string ReadCompositionString(IntPtr imc, int index)
    {
        int len = Native.ImmGetCompositionString(imc, index, null, 0);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len / 2 + 1);
        Native.ImmGetCompositionString(imc, index, sb, len);
        return sb.ToString();
    }

    /// <summary>
    /// 把输入法的组字窗与候选框钉在本窗口原点。本窗口本身就摆在鼠标点击的位置，
    /// 所以候选框就会出现在你点的那个输入框旁边。
    /// </summary>
    private void PinImeWindow()
    {
        // 焦点在影子文本框上：输入法上下文必须问它拿，问外层窗口是拿不到的
        var target = _shadow.IsHandleCreated && _shadow.Focused ? _shadow.Handle : Handle;
        var imc = Native.ImmGetContext(target);
        if (imc == IntPtr.Zero) return;
        try
        {
            // 组字窗丢到窗口上方的屏幕外：拼音已经内联进网页了，
            // 输入法再画一遍就会和网页里的字重叠成两份
            var comp = new Native.COMPOSITIONFORM
            {
                dwStyle = Native.CFS_RECT,
                ptCurrentPos = new Native.POINT(0, -3000),
                rcArea = new Native.RECT { Left = 0, Top = -3000, Right = 640, Bottom = -2980 }
            };
            Native.ImmSetCompositionWindow(imc, ref comp);

            // 候选框贴在光标那一行的正下方
            var cand = new Native.CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = Native.CFS_CANDIDATEPOS,
                ptCurrentPos = new Native.POINT(0, 20)
            };
            Native.ImmSetCandidateWindow(imc, ref cand);
        }
        finally
        {
            Native.ImmReleaseContext(target, imc);
        }
    }

    /// <summary>
    /// 结束正在进行的组字。提交（正常软件里点别处、切框、跳光标都是这个行为），
    /// 提交后文字会经 TextChanged 正常落进网页，候选框也随之消失。
    /// </summary>
    public void FinishComposition(bool cancel = true)
    {
        if (!_composing) return;


        var box = _shadow.IsHandleCreated ? _shadow.Handle : Handle;

        // 先按老接口试一次（有些输入法认）
        var imc = Native.ImmGetContext(box);
        if (imc != IntPtr.Zero)
        {
            try
            {
                Native.ImmNotifyIME(imc, Native.NI_COMPOSITIONSTR,
                    cancel ? Native.CPS_CANCEL : Native.CPS_COMPLETE, 0);
            }
            finally { Native.ImmReleaseContext(box, imc); }
        }

        // 微软拼音这类新式输入法不认上面的指令，只认「焦点离开了」：
        // 把焦点从影子框拿走，它就会立刻收掉组字和候选框，光标才能安全挪走。
        Native.SetFocus(Handle);
        _composing = false;
    }

    /// <summary>把隐形窗口挪到新位置（输入框的真实位置），候选框会跟着过来。</summary>
    public void MoveTo(int screenX, int screenY)
    {
        if (!Visible) return;
        if (Left == screenX && Top == screenY) return;
        Location = new Point(screenX, screenY);
    }

    /// <summary>把还没交给网页的文字立刻写进去：钩子吞掉组合键之前必须先把文字落地，否则顺序会倒。</summary>
    public void FlushNow() => Flush();

    /// <summary>
    /// 告诉本窗口「刚才那一下是组合键」。Shift/方向键这类键被钩子接走后，
    /// 这里看不到它，会误以为是「Shift 按一下又松开」，顺手把中英文切走。
    /// </summary>
    public void MarkComboTyping()
    {
        _shiftOtherKey = true;
        _lastTypedTicks = Environment.TickCount64;
    }

    public void Release()
    {
        // 离开网页时记下中/英文状态，下次点回来原样恢复，不要平白无故变英文
        if (TryGetConversion(out int leaving)) _savedConv = leaving;
        _flush.Stop();
        Flush();
        _shiftDown = false;
        _shiftOtherKey = false;
        if (Visible) Log.Write("键盘接管结束");
        Location = new Point(-32000, -32000);
        GiveBackFocus();
    }

    /// <summary>隐藏后把焦点交回桌面，否则按键会掉进一个看不见的窗口。</summary>
    private void GiveBackFocus()
    {
        try
        {
            var shell = Native.GetShellWindow();
            if (shell == IntPtr.Zero) return;
            var foreground = Native.GetForegroundWindow();
            if (foreground == Handle)
            {
                uint fgThread = Native.GetWindowThreadProcessId(foreground, out _);
                uint ourThread = Native.GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != ourThread &&
                                Native.AttachThreadInput(ourThread, fgThread, true);
                Native.SetForegroundWindow(shell);
                if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
            }
        }
        catch { }
    }

    private void ForceForeground()
    {
        try
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == Handle) return;

            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
            uint ourThread = Native.GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != ourThread &&
                            Native.AttachThreadInput(ourThread, foregroundThread, true);
            bool ok = Native.SetForegroundWindow(Handle);
            Native.SetFocus(Handle);
            if (attached) Native.AttachThreadInput(ourThread, foregroundThread, false);
            // 抢前台可能被系统拒绝（另一个程序刚拿到前台）。拒绝时键盘其实还在别人那里，
            // 记一条日志，免得只看到「点了没反应」却查不出原因。
            if (!ok) Log.Write("[input] 抢前台被拒，键盘可能仍在原程序");
        }
        catch (Exception ex)
        {
            Log.Write("键盘接管失败：" + ex.Message);
        }
    }

    private long _lastKeyLog;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_KEYUP or 0x0104 or 0x0105)
        {
            int v = m.WParam.ToInt32();
            if (v is 0x10 or 0xA0 or 0xA1 or 0xE5)
            {
                long t = Environment.TickCount64;
                if (t - _lastKeyLog > 200)
                {
                    _lastKeyLog = t;
                    Log.Write($"[key] msg=0x{m.Msg:X4} vk=0x{v:X}");
                }
            }
        }

        if (m.Msg == WM_KEYDOWN)
        {
            int vk = m.WParam.ToInt32();
            if (IsShiftKey(vk))
            {
                _shiftDown = true;
                _shiftOtherKey = false;
                ReadConv();
            }
            else
            {
                if (_shiftDown) _shiftOtherKey = true;
                _lastTypedTicks = Environment.TickCount64;   // 输入法吃掉的键会以 VK_PROCESSKEY 出现，也算“在打字”
            }
        }
        else if (m.Msg == WM_KEYUP && IsShiftKey(m.WParam.ToInt32()))
        {
            HandleShiftUp();
        }

        // 输入法要画候选框了：明确告诉它「就在这里」，否则它会按自己的默认位置画，
        // 常见的默认位置就是屏幕左上角——因为本窗口没有编辑控件，也没有插入符。
        if (m.Msg == Native.WM_IME_STARTCOMPOSITION)
        {
            _composing = true;
            PinImeWindow();
        }
        else if (m.Msg == Native.WM_IME_COMPOSITION)
        {
            PinImeWindow();
            HandleComposition(m);
        }
        else if (m.Msg == Native.WM_IME_ENDCOMPOSITION)
        {
            if (_composing) CompositionChanged?.Invoke("");
            _composing = false;
        }

        if (m.Msg == WM_CHAR)
        {
            // 走组字（拼音/日文）时，文字由 CompositionCommitted 统一提交，
            // 这里再插一遍就会出现「一个字打两次」。
            if (_composing) return;
            if (Environment.TickCount64 - _lastImeCommitTicks < 150) return;

            _lastTypedTicks = Environment.TickCount64;
            char c = (char)m.WParam.ToInt32();
            if (c == '\b' || c == '\r' || c == '\n' || c == '\t' || c == 27) return;
            if (c < 0x20) return;
            if (c > 0x7E) Log.Write($"WM_CHAR {c} U+{(int)c:X4}");
            _pending.Append(c);
            _flush.Stop();
            _flush.Start();
            return;
        }

        if (m.Msg == WM_KEYDOWN)
        {
            var key = (Keys)m.WParam.ToInt32();
            string? name = key switch
            {
                Keys.Back => "Backspace",
                Keys.Enter => "Enter",
                Keys.Tab => "Tab",
                Keys.Left => "ArrowLeft",
                Keys.Right => "ArrowRight",
                Keys.Up => "ArrowUp",
                Keys.Down => "ArrowDown",
                Keys.Delete => "Delete",
                Keys.Home => "Home",
                Keys.End => "End",
                Keys.Escape => null,
                _ => null
            };

            if (key == Keys.Escape)
            {
                Log.Write("键盘接管结束（Esc）");
                Released?.Invoke();
                return;
            }

            bool ctrl = Control.ModifierKeys.HasFlag(Keys.Control);
            int mods = CurrentMods();

            // 粘贴单独留一条兜底：钩子没接管时至少还能粘纯文字
            if (key == Keys.V && ctrl)
            {
                Flush();
                try
                {
                    if (Clipboard.ContainsText()) TextTyped?.Invoke(Clipboard.GetText());
                }
                catch { }
                return;
            }

            // 带 Ctrl 的键走同一条发送通路：以前只认 A/C/X/Z，
            // 其余带 Ctrl 的键会被当成裸键发出去（Ctrl+方向键变成普通方向键）。
            if (ctrl && key is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
            {
                Flush();
                RawKey?.Invoke((int)key, mods);
                return;
            }

            if (name != null)
            {
                Flush();
                RawKey?.Invoke((int)key, mods);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private static bool IsShiftKey(int vk) => vk is 0x10 or 0xA0 or 0xA1;   // VK_SHIFT / VK_LSHIFT / VK_RSHIFT

    private static int CurrentMods()
    {
        int m = 0;
        var k = Control.ModifierKeys;
        if (k.HasFlag(Keys.Alt)) m |= 1;
        if (k.HasFlag(Keys.Control)) m |= 2;
        if (k.HasFlag(Keys.Shift)) m |= 8;
        return m;
    }

    private IntPtr _imcOwner = IntPtr.Zero;

    /// <summary>拿输入法上下文：先问本窗口，再问当前线程（有的窗口没挂上下文）。</summary>
    private IntPtr AcquireImc()
    {
        var imc = Native.ImmGetContext(Handle);
        if (imc != IntPtr.Zero) { _imcOwner = Handle; return imc; }
        imc = Native.ImmGetContext(IntPtr.Zero);
        if (imc != IntPtr.Zero) { _imcOwner = IntPtr.Zero; return imc; }
        _imcOwner = IntPtr.Zero;
        return IntPtr.Zero;
    }

    private void ReleaseImc(IntPtr imc)
    {
        if (imc != IntPtr.Zero) Native.ImmReleaseContext(_imcOwner, imc);
    }

    private bool TryGetConversion(out int conversion)
    {
        conversion = 0;
        var imc = AcquireImc();
        if (imc == IntPtr.Zero) return false;
        try
        {
            return Native.ImmGetConversionStatus(imc, out conversion, out _);
        }
        finally
        {
            ReleaseImc(imc);
        }
    }

    /// <summary>
    /// Shift 按一下又松开（中间没打别的键）：先看输入法自己有没有反应，
    /// 中/英模式没变就替它切一下——桌面层窗口不是普通应用窗口，
    /// 输入法自带的 Shift 快捷键在这里不会触发。
    /// </summary>
    private int ReadConv()
    {
        if (!TryGetConversion(out int conv)) return -1;
        _lastKnownConv = conv;
        return conv;
    }

    private void HandleShiftUp()
    {
        bool wasDown = _shiftDown;
        bool hadOther = _shiftOtherKey;
        _shiftDown = false;
        _shiftOtherKey = false;

        int before = _lastKnownConv;
        int now = ReadConv();
        // 单按 = 按住 Shift 这段时间里没跟别的键。
        // 组合键被钩子接走时，由 MarkComboTyping 补上「刚才在打组合键」这一笔，
        // 所以 Shift+Tab 之后松手不会被误判成单按。
        bool lone = !hadOther && (wasDown || Environment.TickCount64 - _lastTypedTicks > 350);

        long t = Environment.TickCount64;
        bool canLog = t - _lastImeLog > 400;
        if (canLog) _lastImeLog = t;
        if (canLog) Log.Write($"[ime] Shift 松开 wasDown={wasDown} 单按={lone} before={before} now={now}");

        if (!ShiftTogglesIme || !lone) return;

        // 输入法自己换过状态（before != now）就不插手，否则会把它的切换又切回去
        bool changed = before >= 0 && now >= 0 && ((before ^ now) & Native.IME_CMODE_NATIVE) != 0;
        if (changed) return;

        // 读不到状态（TSF 输入法常见）时，先请输入法自己执行一次「中英切换」热键；
        // 它不认再退回到直接改转换状态。
        if (Native.ImmSimulateHotKey(Handle, Native.IME_CHOTKEY_IME_NONIME_TOGGLE))
        {
            _lastKnownConv = ReadConv();
            Log.Write("[ime] 已请输入法执行中英切换热键");
            return;
        }
        ToggleImeNative();
    }

    /// <summary>
    /// 桌面层窗口常常没有输入法上下文（ImmGetContext 返回 0），这时读不到也改不了中/英状态。
    /// 只在真要用到的时候补挂一个，避免动到平时正常的中文输入。
    /// </summary>
    private void EnsureImc()
    {
        if (Native.ImmGetContext(Handle) != IntPtr.Zero) return;
        var imc = Native.ImmCreateContext();
        if (imc == IntPtr.Zero)
        {
            Log.Write("[ime] ImmCreateContext 失败");
            return;
        }
        var old = Native.ImmAssociateContext(Handle, imc);
        Log.Write($"[ime] 补挂输入法上下文 imc={imc} 原有={old}");
    }

    /// <summary>读当前前台窗口的输入法中英状态（用户点进来之前那一瞬间的状态）。</summary>
    private static int ReadForegroundConversion()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return -1;
        var imc = Native.ImmGetContext(fg);
        if (imc == IntPtr.Zero) return -1;
        try
        {
            return Native.ImmGetConversionStatus(imc, out int conv, out _) ? conv : -1;
        }
        finally
        {
            Native.ImmReleaseContext(fg, imc);
        }
    }

    /// <summary>把中/英文状态恢复回去（新建的输入法上下文默认是英文，会让人以为被强制切了）。</summary>
    private void RestoreConv(int conv)
    {
        var imc = AcquireImc();
        if (imc == IntPtr.Zero) return;
        try
        {
            if (Native.ImmSetConversionStatus(imc, conv, 0)) _lastKnownConv = conv;
        }
        finally
        {
            ReleaseImc(imc);
        }
    }

    private void ToggleImeNative()
    {
        EnsureImc();
        var imc = AcquireImc();
        if (imc == IntPtr.Zero)
        {
            Log.Write("[ime] 拿不到输入法上下文，无法代为切换");
            return;
        }
        try
        {
            if (!Native.ImmGetConversionStatus(imc, out int conv, out int sentence)) return;
            int next = (conv & Native.IME_CMODE_NATIVE) != 0
                ? conv & ~Native.IME_CMODE_NATIVE
                : conv | Native.IME_CMODE_NATIVE;
            bool ok = Native.ImmSetConversionStatus(imc, next, sentence);
            _lastKnownConv = next;
            Log.Write($"[ime] 代为切换中/英 ok={ok} -> {((next & Native.IME_CMODE_NATIVE) != 0 ? "中文" : "英文")}");
        }
        finally
        {
            ReleaseImc(imc);
        }
    }

    private void Flush()
    {
        _flush.Stop();
        if (_pending.Length == 0) return;
        var text = _pending.ToString();
        _pending.Clear();
        Log.Write("flush: " + text);
        TextTyped?.Invoke(text);
    }
}
