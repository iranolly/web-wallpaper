namespace WebWallpaper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\WebWallpaper.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("网页壁纸已经在运行，请点右下角的托盘图标。", "网页壁纸",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            Log.Write("程序异常退出：" + ex);
            MessageBox.Show("网页壁纸启动失败：" + ex.Message, "网页壁纸",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
