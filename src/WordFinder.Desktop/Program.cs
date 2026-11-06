using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using WordFinder.Core;

namespace WordFinder.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = GetArgument(args, "--data-dir")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordFinder");
        if (args.Contains("--index-worker"))
        {
            var runId = GetArgument(args, "--run-id");
            if (!Guid.TryParseExact(runId, "N", out _)) return 2;
            return WorkerHost.Run(directory, runId!, args.Contains("--retry-only"));
        }
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("WordFinder.Desktop"));
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
        try
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..20];
            using var mutex = new Mutex(true, @"Local\WordFinder-" + name, out var created);
            if (!created)
            {
                MessageBox.Show("WordFinder 已经在运行，请切换到已打开的窗口。", "WordFinder");
                return 0;
            }
            try
            {
                var database = new IndexDatabase(directory);
                database.Initialize();
                database.RecoverInterrupted();
                AppSettings settings;
                try { settings = AppSettings.Load(directory); }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
                {
                    MessageBox.Show("设置文件无法读取，将使用默认设置。原文件保留在数据目录。\n" + exception.Message, "WordFinder");
                    settings = new();
                }
                Application.Run(new MainForm(database, settings));
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show("无法启动 WordFinder：\n" + exception.Message, "WordFinder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
