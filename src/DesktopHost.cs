namespace WebWallpaper;

/// <summary>
/// 负责把窗口贴进 Windows 桌面层（Progman）——比普通窗口低、比壁纸高。
/// Windows 11 24H2+：Progman 是「raised desktop」，SHELLDLL_DefView 直接挂在 Progman 下，
/// 自己的窗口要作为 Progman 的子窗口插到 DefView 下面才会被绘制。
/// </summary>
internal static class DesktopHost
{
    private const uint WM_SPAWN_WORKERW = 0x052C;

    public static IntPtr Progman => Native.FindWindow("Progman", null);

    public static IntPtr DefView(IntPtr progman) =>
        progman == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

    public static void SpawnWallpaperLayer()
    {
        var prog = Progman;
        if (prog == IntPtr.Zero) return;
        Native.SendMessageTimeout(prog, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, 0, 1500, out _);
    }

    /// <summary>
    /// 贴到桌面层。<paramref name="bounds"/> 是相对虚拟屏幕左上角的物理像素坐标。
    /// </summary>
    public static bool Attach(IntPtr hwnd, Rectangle bounds, bool aboveIcons)
    {
        var prog = Progman;
        if (prog == IntPtr.Zero)
        {
            Log.Write("找不到 Progman");
            return false;
        }

        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        style = (style & ~Native.WS_POPUP) | Native.WS_CHILD | Native.WS_VISIBLE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style));

        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW;
        if (aboveIcons) ex &= ~Native.WS_EX_NOACTIVATE;   // 输入模式允许真正获得焦点（输入法需要）
        else ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        Native.SetParent(hwnd, prog);

        var defView = DefView(prog);
        IntPtr insertAfter = defView;
        if (aboveIcons)
            insertAfter = defView == IntPtr.Zero ? IntPtr.Zero : Native.GetWindow(defView, Native.GW_HWNDPREV);

        bool ok = Native.SetWindowPos(hwnd, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOCOPYBITS);
        if (!ok)
            Log.Write($"SetWindowPos 失败 err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return ok;
    }

    /// <summary>从桌面层摘出来，变成普通的顶层窗口——这样它才能拿到真正的系统焦点（输入法需要）。</summary>
    /// <summary>
    /// 把窗口从桌面图标底下摘出来，变成独立顶层窗口。
    /// 关键：位置和大小用 GetWindowRect 原样带走，一个像素都不改——
    /// 改了尺寸 WebView2 就会重新排版，那就是用户看到的那一下闪。
    /// </summary>
    public static bool Detach(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        style = (style & ~Native.WS_CHILD) | Native.WS_POPUP | Native.WS_VISIBLE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style));
        Native.SetParent(hwnd, IntPtr.Zero);

        bool ok = Native.SetWindowPos(hwnd, Native.HWND_TOP, r.Left, r.Top, w, h,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
        Native.InvalidateRect(hwnd, IntPtr.Zero, false);
        return ok;
    }

    /// <summary>
    /// 放回桌面图标下面。同样按当前的物理矩形搬回去，尺寸不变、位置不漂。
    /// </summary>
    public static bool Reattach(IntPtr hwnd, IntPtr parent)
    {
        var prog = parent == IntPtr.Zero ? Progman : parent;
        if (prog == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        var pt = new Native.POINT { X = r.Left, Y = r.Top };
        Native.ScreenToClient(prog, ref pt);

        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        style = (style & ~Native.WS_POPUP) | Native.WS_CHILD | Native.WS_VISIBLE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style));
        Native.SetParent(hwnd, prog);

        var defView = DefView(prog);
        bool ok = Native.SetWindowPos(hwnd, defView, pt.X, pt.Y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.InvalidateRect(hwnd, IntPtr.Zero, false);
        return ok;
    }


    public static bool IsAttached(IntPtr hwnd)
    {
        var prog = Progman;
        if (prog == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        return Native.GetParent(hwnd) == prog;
    }

    /// <summary>把窗口重新插到桌面层指定层级（幂等，仅位置/层级需要修正时调用）。</summary>
    public static void Reapply(IntPtr hwnd, Rectangle bounds, bool aboveIcons)
    {
        var prog = Progman;
        if (prog == IntPtr.Zero) return;
        var defView = DefView(prog);
        IntPtr insertAfter = aboveIcons
            ? (defView == IntPtr.Zero ? IntPtr.Zero : Native.GetWindow(defView, Native.GW_HWNDPREV))
            : defView;
        Native.SetWindowPos(hwnd, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOCOPYBITS);
    }

    public static bool IsDesktopWindow(IntPtr hwnd)    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = Native.ClassName(hwnd);
        if (cls is "SysListView32" or "SHELLDLL_DefView" or "Progman" or "WorkerW") return true;

        // 桌面层的根是不是 Progman / WorkerW
        var root = hwnd;
        for (int i = 0; i < 8; i++)
        {
            var parent = Native.GetParent(root);
            if (parent == IntPtr.Zero) break;
            root = parent;
        }
        var rootClass = Native.ClassName(root);
        return rootClass is "Progman" or "WorkerW";
    }

    /// <summary>桌面图标层（SysListView32）上是否有图标；用于决定点击要不要转给网页。</summary>
    public static bool IsOverIcon(IntPtr listView, Point screenPoint)
    {
        if (Native.ClassName(listView) != "SysListView32") return false;

        var client = new Native.POINT(screenPoint.X, screenPoint.Y);
        Native.ScreenToClient(listView, ref client);

        var info = new Native.LVHITTESTINFO { pt = client };
        var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LVHITTESTINFO>();
        var buffer = new byte[size];
        var handle = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, handle, false);
            System.Runtime.InteropServices.Marshal.Copy(handle, buffer, 0, (int)size);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(handle);
        }

        uint pid;
        Native.GetWindowThreadProcessId(listView, out pid);
        var process = Native.OpenProcess(Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ |
                                        Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;

        IntPtr remote = IntPtr.Zero;
        try
        {
            remote = Native.VirtualAllocEx(process, IntPtr.Zero, size, 0x1000 | 0x2000, 0x04);
            if (remote == IntPtr.Zero) return false;
            if (!Native.WriteProcessMemory(process, remote, buffer, size, out _)) return false;

            var sent = Native.SendMessageTimeout(listView, Native.LVM_HITTEST, IntPtr.Zero, remote,
                Native.SMTO_ABORTIFHUNG, 60, out var result);
            if (sent == IntPtr.Zero) return false;   // 调用失败/超时：当作不在图标上，交给网页
            return result.ToInt64() >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (remote != IntPtr.Zero) Native.VirtualFreeEx(process, remote, 0, 0x8000);
            Native.CloseHandle(process);
        }
    }
}
