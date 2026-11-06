using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WordFinder.Core;

namespace WordFinder.Desktop;

internal sealed class IndexerController(IndexDatabase database, int memoryLimitMiB, string? executablePath = null)
{
    private readonly object controlLock = new();
    private readonly ConcurrentQueue<string> messages = new();
    private Process? process;
    private IndexProgress latest = new("idle", "尚未开始索引");
    private string? controlPath;
    private bool paused;
    private volatile bool cancelling;

    public IndexProgress Latest => Volatile.Read(ref latest);
    public IReadOnlyList<string> Messages => messages.ToArray();
    public bool IsPaused => paused;
    public long PeakCommittedBytes { get; private set; }

    public Task<IndexSummary> RunAsync(bool retryOnly) => Task.Run<IndexSummary>(async () =>
    {
        var runId = Guid.NewGuid().ToString("N");
        controlPath = Path.Combine(database.DirectoryPath, $"control-{runId}.json");
        WriteControl();
        try
        {
            while (true)
            {
                if (cancelling) return new(0, 0, 0, 0, true);
                using var job = new MemoryJob(memoryLimitMiB);
                using var child = new Process
                {
                    StartInfo = new ProcessStartInfo(executablePath ?? Environment.ProcessPath!)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        StandardInputEncoding = new UTF8Encoding(false)
                    }
                };
                foreach (var argument in new[] { "--index-worker", "--data-dir", database.DirectoryPath, "--run-id", runId })
                    child.StartInfo.ArgumentList.Add(argument);
                if (retryOnly) child.StartInfo.ArgumentList.Add("--retry-only");
                child.Start();
                Volatile.Write(ref process, child);
                try
                {
                    job.Assign(child);
                    child.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    if (!child.HasExited) child.Kill(true);
                    throw;
                }
                var output = ReadProgressAsync(child.StandardOutput);
                var error = child.StandardError.ReadToEndAsync();
                if (cancelling)
                {
                    child.Kill(true);
                    await child.WaitForExitAsync();
                    await output;
                    await error;
                    return new(0, 0, 0, 0, true);
                }
                await child.StandardInput.WriteLineAsync("go");
                child.StandardInput.Close();
                await child.WaitForExitAsync();
                await output;
                var details = await error;
                PeakCommittedBytes = Math.Max(PeakCommittedBytes, job.PeakCommittedBytes);
                Volatile.Write(ref process, null);
                if (cancelling)
                {
                    database.RecoverInterrupted(runId, "索引已停止，下次可重试未完成文件。");
                    var state = Latest;
                    return new(state.Total, state.Parsed, state.Skipped, state.Failed, true);
                }
                if (child.ExitCode == 0)
                {
                    var state = Latest;
                    return new(state.Total, state.Parsed, state.Skipped, state.Failed, state.Phase == "cancelled");
                }
                var reason = $"索引进程提前结束（退出码 {child.ExitCode}，额度 {memoryLimitMiB} MB）。可调高额度后重试。";
                Log(reason + (details.Length > 0 ? Environment.NewLine + details[..Math.Min(details.Length, 4000)] : ""));
                // A failed reader is isolated; completed files remain committed and the same run skips this file.
                if (database.RecoverInterrupted(runId, reason) == 0)
                    throw new InvalidOperationException("索引进程未能启动或继续。" + Environment.NewLine + reason);
                Volatile.Write(ref latest, Latest with { Phase = "restarting", Message = "已记录失败文件，正在继续其他文件" });
            }
        }
        finally
        {
            Volatile.Write(ref process, null);
            if (controlPath is not null && File.Exists(controlPath)) File.Delete(controlPath);
        }
    });

    public void SetPaused(bool value)
    {
        lock (controlLock)
        {
            paused = value;
            WriteControl();
        }
    }

    public async Task StopAsync()
    {
        cancelling = true;
        WriteControl();
        var child = Volatile.Read(ref process);
        if (child is null) return;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            if (!child.HasExited) child.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    private void WriteControl()
    {
        lock (controlLock)
        {
            if (controlPath is null) return;
            var temporary = controlPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new WorkerControl(paused, cancelling)));
            File.Move(temporary, controlPath, true);
        }
    }

    private async Task ReadProgressAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var progress = JsonSerializer.Deserialize<IndexProgress>(line);
            if (progress is null) continue;
            Volatile.Write(ref latest, progress);
            if (progress.Phase is "error" or "warning") Log($"{progress.File}：{progress.Message}");
        }
    }

    private void Log(string message)
    {
        messages.Enqueue(message);
        while (messages.Count > 500) messages.TryDequeue(out _);
        var path = Path.Combine(database.DirectoryPath, "index.log");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            File.Move(path, path + ".old", true);
        File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
