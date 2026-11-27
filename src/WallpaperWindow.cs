using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebWallpaper;

internal sealed class WallpaperWindow : IDisposable
{
    private readonly CoreWebView2Environment _environment;
    private readonly double _zoom;
    private readonly string _url;
    private System.Windows.Forms.Timer? _retryTimer;
    private bool _errorPage;
    private bool _disposed;
    private IntPtr _chrome;

    public Screen Screen { get; }
    public Rectangle Bounds { get; private set; }
    public Form Form { get; }    public WebView2 View { get; }
    public bool IsDisposed => _disposed;

    public WallpaperWindow(Screen screen, Rectangle bounds, string url, double zoom, CoreWebView2Environment environment)
    {
        Screen = screen;
        Bounds = bounds;
        _url = url;
        _zoom = Math.Clamp(zoom <= 0 ? 1.0 : zoom, 0.25, 5.0);
        _environment = environment;

        var form = new WallpaperForm
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = screen.Bounds,
            BackColor = Color.FromArgb(15, 17, 22),
            Text = "WebWallpaper",
            Report = msg => Log.Write($"屏幕 {screen.DeviceName} {msg}")
        };
        Form = form;
        View = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(15, 17, 22)
        };
        Form.Controls.Add(View);
        Form.Shown += async (_, _) => await InitAsync();
        // 编辑态下按 Esc 直接退出（先兜住，网页里没拦住也不会卡住）
        Form.KeyPreview = true;
        Form.KeyDown += (_, e) =>
        {
            if (EditMode && e.KeyCode == Keys.Escape) ExitRequested?.Invoke(this);
        };

        // 编辑态下用户点了别的程序：立刻退出编辑，把图标还回去
        Form.Deactivate += (_, _) =>
        {
            if (!EditMode) return;
            if (Environment.TickCount64 - _enterTicks < 700) return;   // 正在升起来的瞬间不算
            ExitRequested?.Invoke(this);
        };
    }

    private async Task InitAsync()
    {
        if (_disposed) return;

        // 先贴进桌面层，再启动网页；这样不会有一块全屏窗口闪在桌面上。
        DesktopHost.Attach(Form.Handle, Bounds, false);

        try
        {
            await View.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 初始化失败：" + ex.Message);
            return;
        }
        if (_disposed || View.CoreWebView2 == null) return;

        var core = View.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;

        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += (_, e) =>
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { }
            if (msg == "ww-exit-edit") ExitRequested?.Invoke(this);
        };

        core.ProcessFailed += (_, e) =>
        {
            Log.Write("网页进程异常：" + e.ProcessFailedKind);
            try { View.Reload(); } catch { }
        };

        if (Math.Abs(_zoom - 1.0) > 0.001) View.ZoomFactor = _zoom;

        // 拦截浏览器发起的系统拖放（否则按住左键拖动会让 Windows 的拖放循环接管鼠标，指针像被钉住）
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Input.setInterceptDrags", "{\"enabled\":true}");
        }
        catch (Exception ex)
        {
            Log.Write("拦截拖放失败：" + ex.Message);
        }

        // 原生悬浮提示（title）是浏览器自己的小窗口，在桌面层里画不出来；改成由页面自己画
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageTooltip.Script);
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var json = e.WebMessageAsJson;
                    if (json.Contains("wpTip") && Environment.TickCount64 - _lastTipLog > 1500)
                    {
                        _lastTipLog = Environment.TickCount64;
                        Log.Write($"[tip] {Screen.DeviceName} {json}");
                    }
                }
                catch { }
            };
        }
        catch (Exception ex)
        {
            Log.Write("注入悬浮提示脚本失败：" + ex.Message);
        }

        NavigateHome();
        Log.Write($"屏幕 {Screen.DeviceName} 载入 {_url}");
    }

    private void NavigateHome()
    {
        if (_disposed || View.CoreWebView2 == null) return;
        try
        {
            _errorPage = false;
            View.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            Log.Write($"地址无效 {_url}：{ex.Message}");
            ShowErrorPage();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed) return;
        _chrome = IntPtr.Zero;
        _frameWorlds.Clear();   // 新的文档 = 新的上下文，旧的一律作废
        if (_errorPage) return;

        if (e.IsSuccess)
        {
            _primeNeeded = true;
            RefreshMetrics();
            StopRetry();
            VerifyTooltipScript();
            _ = InjectEscapeWatcherAsync();
            return;
        }

        Log.Write($"载入失败 {_url}：{e.WebErrorStatus}");
        ShowErrorPage();
    }

    private long _lastTipLog;
    private bool _tipChecked;

    /// <summary>确认注入的提示脚本真的装上了（装不上就没有悬浮提示）。</summary>
    private void VerifyTooltipScript()
    {
        if (_tipChecked || _disposed || View.CoreWebView2 == null) return;
        _tipChecked = true;
        try
        {
            // CoreWebView2 只能在界面线程上碰
            View.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_disposed || View.CoreWebView2 == null) return;
                    var ok = await View.CoreWebView2.ExecuteScriptAsync("window.__wpTipInstalled===true");
                    Log.Write($"悬浮提示脚本已装载：{ok}");
                }
                catch (Exception ex)
                {
                    Log.Write("悬浮提示脚本检查失败：" + ex.Message);
                }
            }));
        }
        catch (Exception ex)
        {
            Log.Write("悬浮提示脚本检查失败：" + ex.Message);
        }
    }

    private void ShowErrorPage()
    {
        if (_disposed || View.CoreWebView2 == null) return;
        _errorPage = true;
        try
        {
            View.CoreWebView2.NavigateToString(ErrorPage.Build(_url));
        }
        catch { }
        StartRetry();
    }

    private void StartRetry()
    {
        StopRetry();
        _retryTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _retryTimer.Tick += (_, _) => NavigateHome();
        _retryTimer.Start();
    }

    private void StopRetry()
    {
        if (_retryTimer == null) return;
        _retryTimer.Stop();
        _retryTimer.Dispose();
        _retryTimer = null;
    }

    public void Reattach()
    {
        if (_disposed) return;
        DesktopHost.Attach(Form.Handle, Bounds, false);
    }

    /// <summary>任务栏位置/大小变了：把窗口重新贴到新的工作区上。</summary>
    public void SetBounds(Rectangle bounds)
    {
        if (_disposed || bounds.Width <= 0 || bounds.Height <= 0) return;
        Bounds = bounds;
        DesktopHost.Reapply(Form.Handle, bounds, false);
    }

    public void ReapplyZOrder()
    {
        if (_disposed) return;
        DesktopHost.Reapply(Form.Handle, Bounds, false);
    }

    /// <summary>供钩子线程读取的缓存值（钩子线程不能碰 WinForms/WebView2 对象）。</summary>
    public IntPtr CachedChrome = IntPtr.Zero;
    public double CachedScale = 1.0;

    public void RefreshMetrics()
    {
        try
        {
            if (_disposed) return;
            CachedChrome = ChromeHandle;
            CachedScale = CssScale;
        }
        catch { }
    }

    public IntPtr ChromeHandle
    {
        get
        {
            if (_disposed || View.IsDisposed) return IntPtr.Zero;
            if (_chrome != IntPtr.Zero && Native.IsWindow(_chrome)) return _chrome;
            try
            {
                _chrome = FindChild(View.Handle, "Chrome_RenderWidgetHostHWND");
            }
            catch
            {
                _chrome = IntPtr.Zero;
            }
            return _chrome;
        }
    }

    private static IntPtr FindChild(IntPtr parent, string className)
    {
        var found = Native.FindWindowEx(parent, IntPtr.Zero, className, null);
        if (found != IntPtr.Zero) return found;
        var child = Native.FindWindowEx(parent, IntPtr.Zero, null, null);
        while (child != IntPtr.Zero)
        {
            found = FindChild(child, className);
            if (found != IntPtr.Zero) return found;
            child = Native.FindWindowEx(parent, child, null, null);
        }
        return IntPtr.Zero;
    }

    private double CssScale
    {
        get
        {
            double dpi = Native.GetDpiForWindow(View.Handle) / 96.0;
            if (dpi <= 0) dpi = 1.0;
            double zoom = View.ZoomFactor <= 0 ? 1.0 : View.ZoomFactor;
            return dpi * zoom;
        }
    }

    private (double X, double Y) ToCss(int clientX, int clientY)
    {
        double scale = CssScale;
        return (clientX / scale, clientY / scale);
    }

    private static string Num(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private long _lastMoveSent = -1000;
    private bool _primeNeeded = true;

    /// <summary>把指针移动送进网页：悬停高亮、拖动都靠它。</summary>
    public void PageMouseMove(int clientX, int clientY, bool pressed)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        long now = Environment.TickCount64;
        if (now - _lastMoveSent < 16) return;
        _lastMoveSent = now;
        var (x, y) = ToCss(clientX, clientY);
        if (_primeNeeded)
        {
            // 页面刚加载时先给一个角落位置，之后的第一下移动才能触发鼠标进入
            _primeNeeded = false;
            SendMouse("{\"type\":\"mouseMoved\",\"x\":0,\"y\":0,\"button\":\"none\",\"buttons\":0}");
        }
        SendMouse($"{{\"type\":\"mouseMoved\",\"x\":{Num(x)},\"y\":{Num(y)}," +
                  $"\"button\":\"{(pressed ? "left" : "none")}\",\"buttons\":{(pressed ? 1 : 0)}}}");
    }

    /// <summary>指针离开桌面网页时清掉悬停状态，否则按钮会一直亮着。</summary>
    public void PageMouseLeave()
    {
        if (_disposed || View.CoreWebView2 == null) return;
        _lastMoveSent = -1000;
        SendMouse("{\"type\":\"mouseMoved\",\"x\":-10,\"y\":-10,\"button\":\"none\",\"buttons\":0}");
    }

    public void PageMouseDown(int clientX, int clientY, int clickCount)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        _lastMoveSent = Environment.TickCount64;
        var (x, y) = ToCss(clientX, clientY);
        SendMouse($"{{\"type\":\"mousePressed\",\"x\":{Num(x)},\"y\":{Num(y)}," +
                  $"\"button\":\"left\",\"buttons\":1,\"clickCount\":{clickCount}}}");
    }

    public void PageMouseUp(int clientX, int clientY, int clickCount)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        _lastMoveSent = Environment.TickCount64;
        var (x, y) = ToCss(clientX, clientY);
        SendMouse($"{{\"type\":\"mouseReleased\",\"x\":{Num(x)},\"y\":{Num(y)}," +
                  $"\"button\":\"left\",\"buttons\":0,\"clickCount\":{clickCount}}}");
    }

    private void SendMouse(string json)
    {
        try
        {
            long t0 = Environment.TickCount64;
            _ = View.CoreWebView2!.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) Log.Write("[mouse] 失败 " + t.Exception?.GetBaseException().Message);
                }, TaskScheduler.Default);
            long dt = Environment.TickCount64 - t0;
            if (dt > 8) Log.Write($"[slow] dispatch {dt}ms");
        }
        catch (Exception ex)
        {
            Log.Write("鼠标转发失败：" + ex.Message);
        }
    }

    /// <summary>判断点击位置上是哪种元素。</summary>
    public async Task<string> TagAtAsync(int clientX, int clientY)
    {
        if (_disposed || View.CoreWebView2 == null) return "";
        try
        {
            double scale = CssScale;
            Log.Write($"[tag] client={clientX},{clientY} dpi={Native.GetDpiForWindow(View.Handle)} zoom={View.ZoomFactor} scale={scale}");
            var x = (clientX / scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var y = (clientY / scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var js = "(function(){var el=document.elementFromPoint(" + x + "," + y +
                     ");if(!el)return '';var t=el.tagName;if(t==='INPUT')t+=':'+(el.type||'text');" +
                     "if(el.isContentEditable)t+='+editable';" +
                     "if(el.getAttribute&&el.getAttribute('role')==='textbox')t+='+role';return t;})()";
            var raw = await View.CoreWebView2.ExecuteScriptAsync(js);
            return raw.Trim('"');
        }
        catch
        {
            return "";
        }
    }

    private long _lastDegradedLog;
    private System.Drawing.Point _lastCdpPoint = new(-1, -1);
    private readonly Dictionary<string, int> _frameWorlds = new();

    /// <summary>
    /// 从浏览器层面拿插入点：页面里的脚本被沙箱拦在外面时走这条路。
    /// 先问每个框架“你里面有没有真正的输入焦点”，选最深的那一帧取插入点，
    /// 再逐层加上它所在框架的位置与缩放——这些都不受同源限制，因为问的是浏览器本身。
    /// 任何一步不成就返回 null，由调用方退回原来的保守位置。
    /// </summary>
    private async Task<System.Drawing.Point?> CaretViaBrowserAsync()
    {
        if (_disposed || View.CoreWebView2 == null) return null;
        try
        {
            var treeRaw = await CdpAsync("Page.getFrameTree", "{}");
            using var treeDoc = JsonDocument.Parse(treeRaw);
            if (!treeDoc.RootElement.TryGetProperty("frameTree", out var root)) return null;

            var frames = new List<(string Id, string? Parent, int Depth)>();
            void Walk(JsonElement node, string? parent, int depth)
            {
                if (!node.TryGetProperty("frame", out var f)) return;
                var id = f.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) return;
                frames.Add((id, parent, depth));
                if (node.TryGetProperty("childFrames", out var kids) && kids.ValueKind == JsonValueKind.Array)
                    foreach (var k in kids.EnumerateArray()) Walk(k, id, depth + 1);
            }
            Walk(root, null, 0);
            if (frames.Count == 0) return null;

            string? leaf = null;
            int bestDepth = -1;
            foreach (var fr in frames)
            {
                var ctx = await FrameWorldAsync(fr.Id);
                if (ctx <= 0) continue;
                var probe = await EvalAsync(ctx,
                    "(()=>{const a=document.activeElement;if(!a)return '0';" +
                    "const t=a.tagName||'';return (t==='INPUT'||t==='TEXTAREA'||a.isContentEditable===true)?'1':'0';})()");
                if (probe == "1" && fr.Depth > bestDepth) { bestDepth = fr.Depth; leaf = fr.Id; }
            }
            if (leaf == null)
            {
                if (Environment.TickCount64 - _lastDegradedLog > 5000)
                {
                    _lastDegradedLog = Environment.TickCount64;
                    Log.Write($"[cdp] 框架 {frames.Count} 个，都没有输入焦点");
                }
                return null;
            }

            var leafCtx = await FrameWorldAsync(leaf);
            if (leafCtx <= 0) return null;
            var caret = await EvalAsync(leafCtx, LeafCaretJs);
            if (string.IsNullOrEmpty(caret) || caret == "null") { Log.Write("[cdp] 叶子帧取不到插入点：" + (caret ?? "null")); return null; }
            var nums = caret.Split(',');
            if (nums.Length < 2) return null;
            if (!double.TryParse(nums[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return null;
            if (!double.TryParse(nums[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return null;

            var map = frames.ToDictionary(f => f.Id, f => f);
            var cur = leaf;
            for (int guard = 0; guard < 12 && map.TryGetValue(cur, out var fr); guard++)
            {
                if (fr.Parent == null) break;
                var parentCtx = await FrameWorldAsync(fr.Parent);
                if (parentCtx <= 0) return null;

                var ownerRaw = await CdpAsync("DOM.getFrameOwner",
                    "{\"frameId\":" + JsonSerializer.Serialize(cur) + "}");
                using var ownerDoc = JsonDocument.Parse(ownerRaw);
                if (!ownerDoc.RootElement.TryGetProperty("backendNodeId", out var backend)) { Log.Write("[cdp] getFrameOwner 无 backend：" + ownerRaw); return null; }

                var resolved = await CdpAsync("DOM.resolveNode",
                    "{\"backendNodeId\":" + backend.GetInt32() + ",\"executionContextId\":" + parentCtx + "}");
                using var resolvedDoc = JsonDocument.Parse(resolved);
                if (!resolvedDoc.RootElement.TryGetProperty("object", out var obj) ||
                    !obj.TryGetProperty("objectId", out var objectId)) { Log.Write("[cdp] resolveNode 无 object：" + resolved); return null; }

                var rectRaw = await CdpAsync("Runtime.callFunctionOn",
                    "{\"objectId\":" + JsonSerializer.Serialize(objectId.GetString()) +
                    ",\"returnByValue\":true,\"functionDeclaration\":" +
                    JsonSerializer.Serialize(
                        "function(){var r=this.getBoundingClientRect(),cs=getComputedStyle(this);" +
                        "var bl=parseFloat(cs.borderLeftWidth)||0,bt=parseFloat(cs.borderTopWidth)||0;" +
                        "var pl=parseFloat(cs.paddingLeft)||0,pt=parseFloat(cs.paddingTop)||0;" +
                        "var sx=this.offsetWidth?r.width/this.offsetWidth:1,sy=this.offsetHeight?r.height/this.offsetHeight:1;" +
                        "return [r.left+(bl+pl)*sx,r.top+(bt+pt)*sy,sx,sy];}") + "}");
                using var rectDoc = JsonDocument.Parse(rectRaw);
                if (!rectDoc.RootElement.TryGetProperty("result", out var rr) ||
                    !rr.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) { Log.Write("[cdp] 取框架位置失败：" + rectRaw); return null; }
                var box = arr.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (box.Length < 4) return null;

                x = box[0] + x * box[2];
                y = box[1] + y * box[3];
                cur = fr.Parent;
            }

            var point = new System.Drawing.Point((int)Math.Round(x), (int)Math.Round(y));
            if (point != _lastCdpPoint)
            {
                _lastCdpPoint = point;
                Log.Write($"[cdp] 浏览器层面取到插入点 {point.X},{point.Y}（帧深 {bestDepth}）");
            }
            return point;
        }
        catch (Exception ex)
        {
            _frameWorlds.Clear();
            Log.Write("[cdp] 取插入点失败：" + ex.Message);
            return null;
        }
    }

    private async Task<int> FrameWorldAsync(string frameId)
    {
        if (_frameWorlds.TryGetValue(frameId, out var cached)) return cached;
        var raw = await CdpAsync("Page.createIsolatedWorld",
            "{\"frameId\":" + JsonSerializer.Serialize(frameId) + ",\"worldName\":\"wpCaret\"}");
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("executionContextId", out var id)) return -1;
        var ctx = id.GetInt32();
        _frameWorlds[frameId] = ctx;
        return ctx;
    }

    private async Task<string?> EvalAsync(int contextId, string expression)
    {
        var raw = await CdpAsync("Runtime.evaluate",
            "{\"expression\":" + JsonSerializer.Serialize(expression) +
            ",\"contextId\":" + contextId + ",\"returnByValue\":true}");
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("exceptionDetails", out _)) return null;
        if (!doc.RootElement.TryGetProperty("result", out var res)) return null;
        return res.TryGetProperty("value", out var v) ? v.ToString() : null;
    }

    private async Task<string> CdpAsync(string method, string json)
    {
        if (View.CoreWebView2 == null) throw new InvalidOperationException("网页还没就绪");
        return await View.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json);
    }

    private const string LeafCaretJs = """
(function(){try{
var el=document.activeElement;if(!el)return 'null';
if(el===document.body||el===document.documentElement)return 'null';
var r=el.getBoundingClientRect();if(!r||r.width<=0||r.height<=0)return 'null';
var cs=getComputedStyle(el);var tag=el.tagName;var x=r.left,y=r.top+r.height/2;
if(tag==='INPUT'||tag==='TEXTAREA'){
  var pos=el.selectionStart;if(pos===null||pos===undefined)pos=0;
  var before=(el.value||'').substring(0,pos);
  var m=document.createElement('div');
  m.style.cssText='position:absolute;left:0;top:0;visibility:hidden;pointer-events:none;overflow:hidden;white-space:pre-wrap;word-wrap:break-word;';
  m.style.font=cs.font;m.style.fontFamily=cs.fontFamily;m.style.fontSize=cs.fontSize;
  m.style.fontWeight=cs.fontWeight;m.style.lineHeight=cs.lineHeight;m.style.letterSpacing=cs.letterSpacing;
  m.style.paddingTop=cs.paddingTop;m.style.paddingLeft=cs.paddingLeft;
  m.style.paddingRight=cs.paddingRight;m.style.paddingBottom=cs.paddingBottom;
  m.style.borderTopWidth=cs.borderTopWidth;m.style.borderLeftWidth=cs.borderLeftWidth;
  m.style.boxSizing=cs.boxSizing;m.style.width=el.clientWidth+'px';
  m.textContent=before;
  var mk=document.createElement('span');mk.textContent='\u200b';m.appendChild(mk);
  document.body.appendChild(m);
  var mr=mk.getBoundingClientRect(),mm=m.getBoundingClientRect();
  document.body.removeChild(m);
  var bL=parseFloat(cs.borderLeftWidth)||0,bT=parseFloat(cs.borderTopWidth)||0;
  x=r.left+bL+(mr.left-mm.left)-(el.scrollLeft||0);
  y=r.top+bT+(mr.top-mm.top)-(el.scrollTop||0)+mr.height/2;
}else{
  var sel=getSelection();
  if(sel&&sel.rangeCount){
    var rg=sel.getRangeAt(0).cloneRange();rg.collapse(false);
    var rects=rg.getClientRects();var rr=rects.length?rects[rects.length-1]:rg.getBoundingClientRect();
    if(rr&&(rr.width||rr.height)){x=rr.right;y=rr.top+rr.height/2;}
  }
}
if(x<r.left-4)x=r.left;if(x>r.right+4)x=r.right;
return x+','+y;
}catch(e){return 'null';}})()
""";

    /// <summary>
    /// 问网页「插入点现在精确在哪」，换算成屏幕坐标。
    /// 输入法的候选框只能跟着窗口走，所以把隐形窗口摆到插入点上，
    /// 候选框才会像原生软件那样贴着光标。
    /// </summary>
    public async Task<System.Drawing.Point?> FocusRectScreenPointAsync()
    {
        if (_disposed || View.CoreWebView2 == null) return null;
        try
        {
            // 量的是「插入点」而不是「输入框」：输入框里已经打了几个字，插入点就往右移几个字宽。
            // 输入法和普通软件里一样，靠这个点来决定组字窗和候选框画在哪。
            // 用镜像元素算：换行、多行、滚动之后插入点在哪都能算对——这里最容易和原生体验差一截。
            // 页面里可能有 iframe：那时顶层只看得见 iframe 本身，要逐层问进去，
            // 把内层的插入点加上外层的边框、内边距和缩放，才能得到顶层的坐标。
            const string js = """
(function(){try{
function caretIn(win,doc){
  try{
  var el=doc.activeElement;if(!el)return null;
  var tag=el.tagName;
  if(tag==='IFRAME'||tag==='FRAME'){
    var inner=null;
    try{if(el.contentWindow&&el.contentDocument)inner=caretIn(el.contentWindow,el.contentDocument);}catch(e){inner=null;}
    var ir=el.getBoundingClientRect();
    if(inner){
      var fs=win.getComputedStyle(el);
      var tr=fs.transform||'none';
      if(tr!=='none'){
        var m=tr.replace(/^matrix\(|\)$/g,'').split(',').map(parseFloat);
        if(m.length===6&&(Math.abs(m[1])>1e-4||Math.abs(m[2])>1e-4))return {x:ir.left,y:ir.top+ir.height/2,deg:1};
      }
      var fbl=parseFloat(fs.borderLeftWidth)||0,fbt=parseFloat(fs.borderTopWidth)||0;
      var fpl=parseFloat(fs.paddingLeft)||0,fpt=parseFloat(fs.paddingTop)||0;
      var sx=el.offsetWidth?ir.width/el.offsetWidth:1,sy=el.offsetHeight?ir.height/el.offsetHeight:1;
      return {x:ir.left+(fbl+fpl+inner.x)*sx,y:ir.top+(fbt+fpt+inner.y)*sy};
    }
    if(ir&&ir.width>0)return {x:ir.left,y:ir.top+ir.height/2,deg:1};
    return null;
  }
  if(el===doc.body||el===doc.documentElement)return null;
  var r=el.getBoundingClientRect();if(!r||r.width<=0||r.height<=0)return null;
  var cs=win.getComputedStyle(el);var x=r.left,y=r.top+r.height/2;
  if(tag==='INPUT'||tag==='TEXTAREA'){
    var pos=el.selectionStart;if(pos===null||pos===undefined)pos=0;
    var before=(el.value||'').substring(0,pos);
    var m=doc.createElement('div');
    m.style.cssText='position:absolute;left:0;top:0;visibility:hidden;pointer-events:none;overflow:hidden;white-space:pre-wrap;word-wrap:break-word;';
    m.style.font=cs.font;m.style.fontFamily=cs.fontFamily;m.style.fontSize=cs.fontSize;
    m.style.fontWeight=cs.fontWeight;m.style.lineHeight=cs.lineHeight;m.style.letterSpacing=cs.letterSpacing;
    m.style.paddingTop=cs.paddingTop;m.style.paddingLeft=cs.paddingLeft;
    m.style.paddingRight=cs.paddingRight;m.style.paddingBottom=cs.paddingBottom;
    m.style.borderTopWidth=cs.borderTopWidth;m.style.borderLeftWidth=cs.borderLeftWidth;
    m.style.boxSizing=cs.boxSizing;m.style.width=el.clientWidth+'px';
    m.textContent=before;
    var mk=doc.createElement('span');mk.textContent='\u200b';m.appendChild(mk);
    doc.body.appendChild(m);
    var mr=mk.getBoundingClientRect(),mm=m.getBoundingClientRect();
    doc.body.removeChild(m);
    var bL=parseFloat(cs.borderLeftWidth)||0,bT=parseFloat(cs.borderTopWidth)||0;
    x=r.left+bL+(mr.left-mm.left)-(el.scrollLeft||0);
    y=r.top+bT+(mr.top-mm.top)-(el.scrollTop||0)+mr.height/2;
  }else{
    var sel=win.getSelection();
    if(sel&&sel.rangeCount){
      var rg=sel.getRangeAt(0).cloneRange();rg.collapse(false);
      var rects=rg.getClientRects();var rr=rects.length?rects[rects.length-1]:rg.getBoundingClientRect();
      if(rr&&(rr.width||rr.height)){x=rr.right;y=rr.top+rr.height/2;}
    }
  }
  if(x<r.left-4)x=r.left;if(x>r.right+4)x=r.right;
  return {x:x,y:y};
  }catch(e){return null;}
}
var out=caretIn(window,document);
return out?(out.x+','+out.y+(out.deg?',1':'')):'null';
}catch(e){return 'null';}})()
""";
            var raw = await View.CoreWebView2.ExecuteScriptAsync(js);
            var text = raw.Trim('"');
            if (text == "null") return null;
            var parts = text.Split(',');
            if (parts.Length != 2 && parts.Length != 3) return null;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var cx)) return null;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var cy)) return null;
            if (parts.Length == 3 && parts[2] == "1")
            {
                // 网页脚本被沙箱拦在外面，只能从浏览器层面逐层问进去
                var deep = await CaretViaBrowserAsync();
                if (deep.HasValue)
                {
                    cx = deep.Value.X;
                    cy = deep.Value.Y;
                }
                else if (Environment.TickCount64 - _lastDegradedLog > 5000)
                {
                    _lastDegradedLog = Environment.TickCount64;
                    Log.Write("[input] 内层插入点取不到（跨域、旋转或内层没焦点），候选框退回框架位置");
                }
            }

            double scale = CssScale;
            var origin = Form.PointToScreen(System.Drawing.Point.Empty);
            return new System.Drawing.Point(
                origin.X + (int)Math.Round(cx * scale),
                origin.Y + (int)Math.Round(cy * scale));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把输入法正在组的那串拼音写进网页，让它在文本框里内联显示（带下划线），
    /// 效果和原生输入法一样；传空字符串表示结束组字。
    /// </summary>
    public async Task SetCompositionAsync(string text)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        try
        {
            // CDP 要的是 selectionStart/selectionEnd 一对，给成 selectionLength 会直接被拒
            var json = "{\"text\":" + JsonSerializer.Serialize(text) +
                       ",\"selectionStart\":" + text.Length +
                       ",\"selectionEnd\":" + text.Length + "}";
            await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.imeSetComposition", json);
        }
        catch (Exception ex)
        {
            Log.Write("组字同步失败：" + ex.Message);
        }
    }

    /// <summary>上屏：先结束组字，再把最终的文字插进去。</summary>
    public async Task CommitCompositionAsync(string text)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        await SetCompositionAsync("");
        await InsertTextAsync(text);
    }

    /// <summary>把文字插到网页当前光标处（输入法组好的中文也走这里）。</summary>
    public async Task InsertTextAsync(string text)
    {
        if (_disposed || View.CoreWebView2 == null || string.IsNullOrEmpty(text)) return;
        try
        {
            var json = "{\"text\":" + JsonSerializer.Serialize(text) + "}";
            await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", json);
        }
        catch (Exception ex)
        {
            Log.Write("写入网页失败：" + ex.Message);
        }
    }

    /// <summary>把功能键交给浏览器处理（退格、回车、方向键等）。</summary>
    public async Task SendKeyAsync(string key)
    {
        int vk = key switch
        {
            "Backspace" => 8,
            "Tab" => 9,
            "Enter" => 13,
            "End" => 35,
            "Home" => 36,
            "ArrowLeft" => 37,
            "ArrowUp" => 38,
            "ArrowRight" => 39,
            "ArrowDown" => 40,
            "Delete" => 46,
            _ => 0
        };
        if (vk == 0) return;
        await SendKeyAsync(vk, 0, true);
        await SendKeyAsync(vk, 0, false);
    }

    /// <summary>
    /// 把一次按键完整地交给网页：按下与松开分开发，修饰键位按 CDP 的规则给（Alt=1/Ctrl=2/Meta=4/Shift=8）。
    /// 网页的快捷键监听靠 windowsVirtualKeyCode 查表，缺它就会「事件到了但不认」；
    /// Ctrl+V 另外附一条 Paste 编辑命令，这样贴进去的是真内容（图片、格式都在），
    /// 而不是一段被程序截出来的纯文字。
    /// </summary>
    public async Task SendKeyAsync(int vk, int modifiers, bool down)
    {
        if (_disposed || View.CoreWebView2 == null) return;
        if (!TryDescribeKey(vk, out string key, out string code)) return;

        bool paste = down && vk == 0x56 && (modifiers & KeyboardHook.ModCtrl) != 0;
        bool enter = vk == 0x0D;
        var body = $"\"key\":{Json(key)},\"code\":{Json(code)},\"windowsVirtualKeyCode\":{vk}," +
                   $"\"nativeVirtualKeyCode\":{vk},\"modifiers\":{modifiers}";
        if (paste) body += ",\"commands\":[\"Paste\"]";
        // 回车必须走 keyDown + text：既触发页面的 keydown（聊天框自己拦截 Enter 发送），
        // 又触发浏览器默认动作（多行框里真的换行）。rawKeyDown 只到 keydown，不会插入字符。
        // 不带 Shift 的回车：只发按键，让页面自己决定「发送」；带 text 就会变成换行。
        // 带 Shift 的回车：才是真的要在多行框里换行。
        if (enter && down && (modifiers & 8) != 0) body += ",\"text\":\"\\r\"";
        try
        {
            string type = down ? (enter ? "keyDown" : "rawKeyDown") : "keyUp";
            await View.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                "{\"type\":\"" + type + "\"," + body + "}");
        }
        catch (Exception ex)
        {
            Log.Write($"发送按键失败 vk={vk} mods={modifiers} down={down}：" + ex.Message);
        }
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    /// <summary>
    /// 虚拟键码 → 网页认识的 key / code。缺 key 名时网页拿到的 e.key 是空的，
    /// 所有 `e.key === 'Enter'` 这类判断都会失效，所以宁可这张表写全一点。
    /// </summary>
    private static bool TryDescribeKey(int vk, out string key, out string code)
    {
        key = code = "";
        if (vk >= 0x41 && vk <= 0x5A)                 // A-Z
        {
            char c = (char)('a' + (vk - 0x41));
            key = c.ToString();
            code = "Key" + char.ToUpperInvariant(c);
            return true;
        }
        if (vk >= 0x30 && vk <= 0x39)                 // 主键盘数字
        {
            char c = (char)vk;
            key = c.ToString();
            code = "Digit" + c;
            return true;
        }
        if (vk >= 0x60 && vk <= 0x69)                 // 小键盘数字
        {
            char c = (char)('0' + (vk - 0x60));
            key = c.ToString();
            code = "Numpad" + c;
            return true;
        }
        if (vk >= 0x70 && vk <= 0x87)                 // F1-F24
        {
            key = code = "F" + (vk - 0x6F);
            return true;
        }

        (key, code) = vk switch
        {
            0x08 => ("Backspace", "Backspace"),
            0x09 => ("Tab", "Tab"),
            0x0D => ("Enter", "Enter"),
            0x13 => ("Pause", "Pause"),
            0x14 => ("CapsLock", "CapsLock"),
            0x1B => ("Escape", "Escape"),
            0x20 => (" ", "Space"),
            0x21 => ("PageUp", "PageUp"),
            0x22 => ("PageDown", "PageDown"),
            0x23 => ("End", "End"),
            0x24 => ("Home", "Home"),
            0x25 => ("ArrowLeft", "ArrowLeft"),
            0x26 => ("ArrowUp", "ArrowUp"),
            0x27 => ("ArrowRight", "ArrowRight"),
            0x28 => ("ArrowDown", "ArrowDown"),
            0x2C => ("PrintScreen", "PrintScreen"),
            0x2D => ("Insert", "Insert"),
            0x2E => ("Delete", "Delete"),
            0x6A => ("*", "NumpadMultiply"),
            0x6B => ("+", "NumpadAdd"),
            0x6D => ("-", "NumpadSubtract"),
            0x6E => (".", "NumpadDecimal"),
            0x6F => ("/", "NumpadDivide"),
            0x90 => ("NumLock", "NumLock"),
            0x91 => ("ScrollLock", "ScrollLock"),
            0xBA => (";", "Semicolon"),
            0xBB => ("=", "Equal"),
            0xBC => (",", "Comma"),
            0xBD => ("-", "Minus"),
            0xBE => (".", "Period"),
            0xBF => ("/", "Slash"),
            0xC0 => ("`", "Backquote"),
            0xDB => ("[", "BracketLeft"),
            0xDC => ("\\", "Backslash"),
            0xDD => ("]", "BracketRight"),
            0xDE => ("'", "Quote"),
            _ => ("", "")
        };
        return key.Length > 0;
    }

    /// <summary>把 Ctrl+A / C / X / Z 交给浏览器的编辑命令（钩子未接管时的兜底）。</summary>
    public async Task SendShortcutAsync(string key)
    {
        int vk = key switch { "A" => 65, "C" => 67, "X" => 88, "Z" => 90, _ => 0 };
        if (vk == 0) return;
        await SendKeyAsync(vk, KeyboardHook.ModCtrl, true);
        await SendKeyAsync(vk, KeyboardHook.ModCtrl, false);
    }

    private long _enterTicks;
    private bool _liftWindow;

    /// <summary>纯遮罩窗口：不激活、不进任务栏、不抢焦点。</summary>
    /// <summary>网页里按了 Esc：请程序退出编辑态。</summary>
    public event Action<WallpaperWindow>? ExitRequested;

    /// <summary>是不是已经升成了真窗口（编辑态）。</summary>
    public bool EditMode { get; private set; }

    /// <summary>
    /// 进入编辑态：把窗口从桌面层摘出来，变成真正的前台窗口。
    /// 只有这样才能让输入法直接作用在网页上——候选框、换行、拼音下划线全由输入法自己算。
    /// 代价：这段时间桌面图标被盖住，退出编辑就恢复。
    /// </summary>
    public bool EnterEditMode(bool liftWindow)
    {
        if (EditMode || _disposed) return false;

        if (!liftWindow)
        {
            // 不改变窗口层级：把桌面推到前台，焦点交给网页。
            // 好处是完全不重建窗口，没有闪；代价是输入法认不认，要看系统。
            try
            {
                var prog = DesktopHost.Progman;
                if (prog != IntPtr.Zero) ForceForeground(prog);
                Native.SetFocus(View.Handle);
                View.Focus();
                EditMode = true;
                _liftWindow = false;
                _enterTicks = Environment.TickCount64;
                _ = InjectEscapeWatcherAsync();
                Log.Write("[edit] 进入编辑态（只抢焦点，不改层级）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("进入编辑态失败：" + ex.Message);
                return false;
            }
        }

        try
        {
            if (!DesktopHost.Detach(Form.Handle))
            {
                    Log.Write("升为前台失败：Detach 返回 false");
                return false;
            }
            EditMode = true;
            _enterTicks = Environment.TickCount64;

            // 抢前台 + 把焦点交给网页：顺序不能反，反了输入法会绑到空的编辑上下文。
            // 系统默认不允许后台程序抢前台，所以要先挂到当前前台线程上再抢。
            ForceForeground(Form.Handle);
            Form.Activate();
            Form.Focus();
            View.Focus();

            // 让网页自己报告「按了 Esc」，那是退出编辑最自然的动作
            _ = InjectEscapeWatcherAsync();
            _liftWindow = true;
            Log.Write("[edit] 进入编辑态");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("进入编辑态失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>退出编辑态：放回桌面图标下面，键盘交回系统。</summary>
    public void ExitEditMode()
    {
        if (!EditMode || _disposed) return;
        EditMode = false;

        if (!_liftWindow)
        {
            // 没动过层级，只需要把键盘还给系统
            _liftWindow = false;
            try { Native.SetForegroundWindow(Native.GetShellWindow()); } catch { }
            Log.Write("[edit] 退出编辑态（未改层级）");
            return;
        }

        try
        {
            DesktopHost.Reattach(Form.Handle, IntPtr.Zero);
            Log.Write("[edit] 退出编辑态");
        }
        catch (Exception ex)
        {
            Log.Write("退出编辑态失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把自己变成前台窗口。直接 SetForegroundWindow 会被系统拒绝（后台程序不许抢），
    /// 所以先 AttachThreadInput 挂到当前前台线程上，再设置，最后断开。
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var fg = Native.GetForegroundWindow();
            if (fg == hwnd) return;

            uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
            uint ourThread = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != ourThread &&
                            Native.AttachThreadInput(ourThread, fgThread, true);
            bool ok = Native.SetForegroundWindow(hwnd);
            Native.SetFocus(hwnd);
            if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
            Log.Write($"[edit] 抢前台 ok={ok} 前台线程={fgThread} 本线程={ourThread}");
        }
        catch (Exception ex)
        {
            Log.Write("抢前台失败：" + ex.Message);
        }
    }

    private async Task InjectEscapeWatcherAsync()
    {
        try
        {
            if (View.CoreWebView2 == null) return;
            await View.CoreWebView2.ExecuteScriptAsync(
                "if(!window.__wwEsc){window.__wwEsc=1;" +
                "document.addEventListener('keydown',function(e){" +
                "if(e.key==='Escape'&&!e.defaultPrevented){chrome.webview.postMessage('ww-exit-edit');}},true);}");
        }
        catch { }
    }

    public void Reload() => NavigateHome();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRetry();
        try
        {
            Native.SetParent(Form.Handle, IntPtr.Zero);
        }
        catch { }
        try
        {
            View.Dispose();
            Form.Close();
            Form.Dispose();
        }
        catch { }
    }
}

/// <summary>
/// 壁纸窗口本体。只多一件事：把系统动它的事件（显示/隐藏、桌面合成重建）记进日志，
/// 这样“网页突然闪回原桌面”再出现时，能看出是不是桌面层在动它。
/// </summary>
internal sealed class WallpaperForm : Form
{
    public Action<string>? Report;
    private long _lastReport;

    private void Tell(string what)
    {
        long now = Environment.TickCount64;
        if (now - _lastReport < 500) return;
        _lastReport = now;
        try { Report?.Invoke(what); } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0018:   // WM_SHOWWINDOW
                Tell("WM_SHOWWINDOW " + (m.WParam != IntPtr.Zero ? "显示" : "隐藏"));
                break;
            case 0x031E:   // WM_DWMCOMPOSITIONCHANGED
                Tell("窗口合成重建");
                break;
            case 0x0047 when m.LParam != IntPtr.Zero:   // WM_WINDOWPOSCHANGED
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
                if ((wp.flags & 0x0080) != 0) Tell("WM_WINDOWPOSCHANGED 隐藏");
                else if ((wp.flags & 0x0040) != 0) Tell("WM_WINDOWPOSCHANGED 显示");
                break;
        }
        base.WndProc(ref m);
    }
}

internal static class ErrorPage
{
    public static string Build(string url) =>
        "<!doctype html><html><head><meta charset='utf-8'><style>" +
        "html,body{height:100%;margin:0;background:#0f1116;color:#e8e8ea;font-family:'Segoe UI',sans-serif;}" +
        "body{display:flex;align-items:center;justify-content:center;flex-direction:column;gap:18px}" +
        ".t{font-size:42px;letter-spacing:1px}.u{font-size:20px;color:#8b93a7}.h{font-size:16px;color:#5c6478}" +
        "</style></head><body><div class='t'>正在等待页面启动</div><div class='u'>" +
        System.Net.WebUtility.HtmlEncode(url) +
        "</div><div class='h'>连上之后会自动显示，不用手动刷新</div></body></html>";
}
