using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WordFinder.Core;

namespace WordFinder.Desktop;

internal static class WorkerHost
{
    public static int Run(string directory, string runId, bool retryOnly)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (Console.ReadLine() != "go") return 2;
        try
        {
            var database = new IndexDatabase(directory);
            database.Initialize();
            var controlPath = Path.Combine(directory, $"control-{runId}.json");
            var clock = Stopwatch.StartNew();
            IndexProgress last = new("starting", "索引进程已启动");
            void Report(IndexProgress progress)
            {
                last = progress;
                Console.WriteLine(JsonSerializer.Serialize(progress));
            }
            void Check()
            {
                if (clock.ElapsedMilliseconds < 150) return;
                clock.Restart();
                var paused = false;
                while (true)
                {
                    var control = new WorkerControl();
                    if (File.Exists(controlPath))
                    {
                        using var input = new FileStream(controlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        control = JsonSerializer.Deserialize<WorkerControl>(input) ?? new();
                    }
                    if (control.Cancel) throw new OperationCanceledException();
                    if (!control.Paused)
                    {
                        if (paused) Report(last with { Phase = "reading", Message = "已继续索引" });
                        return;
                    }
                    if (!paused) Report(last with { Phase = "paused", Message = "已暂停，点击继续可恢复" });
                    paused = true;
                    Thread.Sleep(150);
                }
            }
            new IndexRunner(database).Run(runId, retryOnly, Report, Check);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
