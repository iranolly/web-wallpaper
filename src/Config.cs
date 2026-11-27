using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebWallpaper;

internal sealed class ScreenConfig
{
    public string Monitor { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Zoom { get; set; } = 1.0;
}

internal sealed class AppConfig
{
    public string DefaultUrl { get; set; } = "http://127.0.0.1:18787/";
    /// <summary>右键是否保留给 Windows 桌面菜单。</summary>
    public bool RightClickDesktopMenu { get; set; } = true;
    /// <summary>滚轮是否转发给网页。关掉时滚轮就交回系统，桌面完全不会动。</summary>
    public bool WheelScroll { get; set; } = true;
    /// <summary>在桌面网页里打字时，Shift 也能切换输入法的中/英文（输入法自己不响应时由本程序代劳）。</summary>
    public bool ImeShiftToggle { get; set; } = true;
    /// <summary>是否安装全局键盘监听。关掉就退回旧行为：只支持少数几个组合键。</summary>
    public bool KeyboardHook { get; set; } = true;
    /// <summary>
    /// 编辑时把网页窗口升成真窗口，让输入法直接作用在网页上（原生候选框/换行/组字）。
    /// 关掉就退回旧的「隐形窗口代收键盘」方式：图标随时可点，但输入法只是接近原生。
    /// </summary>
    public bool NativeFocus { get; set; } = true;
    /// <summary>
    /// 进入编辑时是否把窗口升到图标上面。升上去输入法最稳，但窗口要重建、会有一下闪；
    /// 关掉则只抢焦点、不改变层级（不闪），但输入法可能不认（取决于系统）。
    /// </summary>
    public bool LiftWindow { get; set; } = true;
    public List<ScreenConfig> Screens { get; set; } = new();

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取配置失败：" + ex.Message);
        }

        var fresh = new AppConfig();
        foreach (var screen in Screen.AllScreens)
        {
            fresh.Screens.Add(new ScreenConfig { Monitor = screen.DeviceName, Url = "", Enabled = true, Zoom = 1.0 });
        }
        fresh.Save();
        Log.Write("已生成新配置：" + FilePath);
        return fresh;
    }

    private void Normalize()
    {
        Screens ??= new List<ScreenConfig>();
        if (!IsWebUrl(DefaultUrl)) DefaultUrl = "http://127.0.0.1:18787/";

        foreach (var s in Screens)
        {
            if (s == null) continue;
            s.Monitor ??= "";
            s.Url ??= "";
            if (!string.IsNullOrWhiteSpace(s.Url) && !IsWebUrl(s.Url)) s.Url = "";
            if (s.Zoom < 0.25 || s.Zoom > 5) s.Zoom = 1.0;
        }

        // 新接入的屏幕自动补一条，避免只有旧屏有网页
        foreach (var screen in Screen.AllScreens)
        {
            if (!Screens.Any(s => s != null && string.Equals(s.Monitor, screen.DeviceName, StringComparison.OrdinalIgnoreCase)))
                Screens.Add(new ScreenConfig { Monitor = screen.DeviceName, Url = "", Enabled = true, Zoom = 1.0 });
        }
    }

    public static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" or "file";
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Write("写入配置失败：" + ex.Message);
        }
    }

    public ScreenConfig? ForScreen(Screen screen, int index)
    {
        var byName = Screens.FirstOrDefault(s => s != null &&
            string.Equals(s.Monitor, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            Log.Write($"屏幕 {screen.DeviceName} 匹配到配置 {byName.Monitor}");
            return byName;
        }

        if (index < Screens.Count)
        {
            Log.Write($"屏幕 {screen.DeviceName} 未按名字匹配，回落到第 {index} 条配置");
            return Screens[index];
        }

        Log.Write($"屏幕 {screen.DeviceName} 没有配置，使用默认网址");
        return null;
    }
}
