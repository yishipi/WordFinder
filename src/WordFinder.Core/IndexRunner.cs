using System.Globalization;
using System.Text;
using ExcelDataReader;
using ExcelNumberFormat;
using Microsoft.Data.Sqlite;

namespace WordFinder.Core;

public sealed class IndexRunner(IndexDatabase database)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".xls", ".xlsb", ".xlsm" };
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("zh-CN");
    private sealed record FileEntry(long Id, string Path, long Size, long Write, bool Indexed, string State, string? OutcomeRun, string? Outcome);

    public IndexSummary Run(string runId, bool retryOnly, Action<IndexProgress>? progress = null,
        Action? checkpoint = null, CancellationToken cancellationToken = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var connection = database.Open();
        var total = 0;
        var parsed = 0;
        var skipped = 0;
        var failed = 0;
        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
        }
        void Report(string phase, string message, string? file = null, long cells = 0) =>
            progress?.Invoke(new(phase, message, file, total, parsed + skipped + failed, parsed, skipped, failed, cells));
        try
        {
            foreach (var root in database.GetRoots())
            {
                Check();
                Report("scan", $"正在扫描分类：{root.Label}", root.Path);
                ScanRoot(connection, root, runId, Check, message => Report("warning", message, root.Path));
            }
            IndexDatabase.PruneUnreferencedFiles(connection);
            var files = GetFiles(connection, retryOnly, runId);
            total = files.Count;
            parsed = files.Count(file => file.OutcomeRun == runId && file.Outcome == "parsed");
            skipped = files.Count(file => file.OutcomeRun == runId && file.Outcome == "skipped");
            failed = files.Count(file => file.OutcomeRun == runId && file.Outcome == "failed");
            foreach (var file in files)
            {
                Check();
                if (file.OutcomeRun == runId && file.Outcome is "parsed" or "skipped" or "failed") continue;
                Report("file", "正在读取 Excel", file.Path);
                try
                {
                    var before = new FileInfo(file.Path);
                    if (!before.Exists) throw new FileNotFoundException("文件已不存在。", file.Path);
                    var size = before.Length;
                    var write = before.LastWriteTimeUtc.Ticks;
                    if (file.Indexed && file.State == "ready" && file.Size == size && file.Write == write)
                    {
                        SetOutcome(connection, file.Id, runId, "skipped", "ready", null);
                        skipped++;
                        Report("progress", "文件未变化，已跳过", file.Path);
                        continue;
                    }
                    IndexFile(connection, file, size, write, runId, Check,
                        cells => Report("reading", "正在建立索引", file.Path, cells));
                    parsed++;
                    Report("progress", "文件索引完成", file.Path);
                }
                catch (OperationCanceledException)
                {
                    RemoveBuildingGenerations(connection, file.Id);
                    SetOutcome(connection, file.Id, runId, "cancelled", "interrupted", "本次索引已取消，下次从该文件重新开始。");
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    RemoveBuildingGenerations(connection, file.Id);
                    SetOutcome(connection, file.Id, runId, "failed", "failed", FriendlyError(exception));
                    failed++;
                    Report("error", FriendlyError(exception), file.Path);
                }
            }
            Report("cleanup", "正在清理旧索引数据");
            Check();
            IndexDatabase.CollectGarbage(connection);
            IndexDatabase.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            Report("done", $"完成：更新 {parsed}，跳过 {skipped}，失败 {failed}");
            return new(total, parsed, skipped, failed, false);
        }
        catch (OperationCanceledException)
        {
            Report("cancelled", "已保存完成进度，未完成文件可下次重试");
            return new(total, parsed, skipped, failed, true);
        }
    }

    private static void ScanRoot(SqliteConnection connection, DirectoryRoot root, string runId,
        Action check, Action<string> warning)
    {
        var complete = true;
        var folders = new Stack<string>();
        folders.Push(root.Path);
        using var insertFile = IndexDatabase.Command(connection, null,
            "INSERT INTO files(path,path_key) VALUES($path,$key) ON CONFLICT(path_key) DO NOTHING", ("$path", ""), ("$key", ""));
        using var associate = IndexDatabase.Command(connection, null, """
            INSERT INTO root_files(root_id,file_id,seen_run)
            SELECT $root,id,$run FROM files WHERE path_key=$key
            ON CONFLICT(root_id,file_id) DO UPDATE SET seen_run=excluded.seen_run
            """, ("$root", root.Id), ("$run", runId), ("$key", ""));
        while (folders.TryPop(out var folder))
        {
            check();
            string[] paths;
            try
            {
                paths = Directory.GetFiles(folder);
                foreach (var child in Directory.GetDirectories(folder))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) folders.Push(child);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                warning($"无法完整扫描 {folder}：{exception.Message}；已保留该分类旧记录。");
                continue;
            }
            using var transaction = connection.BeginTransaction();
            insertFile.Transaction = transaction;
            associate.Transaction = transaction;
            foreach (var path in paths)
            {
                check();
                if (!Extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) continue;
                var key = IndexDatabase.NormalizePath(path).ToUpperInvariant();
                insertFile.Parameters["$path"].Value = path;
                insertFile.Parameters["$key"].Value = key;
                insertFile.ExecuteNonQuery();
                associate.Parameters["$key"].Value = key;
                associate.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        if (complete) IndexDatabase.Execute(connection, null, "DELETE FROM root_files WHERE root_id=$root AND seen_run<>$run", ("$root", root.Id), ("$run", runId));
    }

    private static List<FileEntry> GetFiles(SqliteConnection connection, bool retryOnly, string runId)
    {
        using var command = IndexDatabase.Command(connection, null, """
            SELECT id,path,indexed_size,indexed_write,active_generation IS NOT NULL,state,outcome_run,outcome
            FROM files f WHERE EXISTS(SELECT 1 FROM root_files rf WHERE rf.file_id=f.id)
            AND ($retry=0 OR state IN ('failed','interrupted') OR outcome_run=$run) ORDER BY id
            """, ("$retry", retryOnly ? 1 : 0), ("$run", runId));
        using var reader = command.ExecuteReader();
        var files = new List<FileEntry>();
        while (reader.Read()) files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetBoolean(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return files;
    }

    private static void IndexFile(SqliteConnection connection, FileEntry file, long size, long write,
        string runId, Action check, Action<long> report)
    {
        long generation;
        using (var transaction = connection.BeginTransaction())
        {
            SetOutcome(connection, file.Id, runId, "processing", "processing", null, transaction);
            generation = Convert.ToInt64(IndexDatabase.Scalar(connection, transaction,
                "INSERT INTO generations(file_id) VALUES($file) RETURNING id", ("$file", file.Id)));
            transaction.Commit();
        }
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65_536, FileOptions.SequentialScan);
        // The initial scan is required for merged-range metadata, including ranges after sheetData.
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { SinglePassMode = false });
        using var writer = new FileIndexWriter(connection);
        var formats = new Dictionary<string, NumberFormat>(StringComparer.Ordinal);
        var sheetIndex = 0;
        do
        {
            check();
            var sheet = writer.AddSheet(generation, reader.Name, sheetIndex++);
            while (reader.Read())
            {
                check();
                var cells = new List<CellValue>();
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);
                    if (value is null or DBNull) continue;
                    var text = FormatCell(reader, column, value, formats);
                    if (text.Length > 0) cells.Add(new(column, text));
                }
                if (cells.Count > 0) writer.AddRow(sheet, reader.Depth + 1, cells);
                if (reader.Depth % 500 == 0) report(writer.Cells);
            }
            foreach (var range in reader.MergeCells) writer.AddMergedRange(sheet, range, check);
        } while (reader.NextResult());
        check();
        var after = new FileInfo(file.Path);
        if (!after.Exists || after.Length != size || after.LastWriteTimeUtc.Ticks != write)
            throw new IOException("文件在读取期间发生变化，请保存文件后重试。");
        writer.Complete();
        using var activate = connection.BeginTransaction();
        IndexDatabase.Execute(connection, activate, """
            UPDATE generations SET state='ready' WHERE id=$generation;
            UPDATE files SET active_generation=$generation,indexed_size=$size,indexed_write=$write,
                rows_count=$rows,cells_count=$cells,state='ready',last_error=NULL,outcome_run=$run,outcome='parsed'
            WHERE id=$file;
            """, ("$generation", generation), ("$size", size), ("$write", write), ("$rows", writer.Rows),
            ("$cells", writer.Cells), ("$run", runId), ("$file", file.Id));
        foreach (var old in IndexDatabase.ReadIds(connection, activate, "SELECT id FROM generations WHERE file_id=$file AND id<>$generation", ("$file", file.Id), ("$generation", generation)))
            IndexDatabase.DeleteGeneration(connection, activate, old);
        activate.Commit();
    }

    private static string FormatCell(IExcelDataReader reader, int column, object value, Dictionary<string, NumberFormat> formats)
    {
        if (value is string text) return text;
        var format = reader.GetNumberFormatString(column);
        if (!string.IsNullOrEmpty(format))
        {
            if (!formats.TryGetValue(format, out var numberFormat))
            {
                if (formats.Count >= 256) formats.Clear();
                numberFormat = new NumberFormat(format);
                formats[format] = numberFormat;
            }
            if (numberFormat.IsValid) return numberFormat.Format(value, Culture);
        }
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", Culture),
            bool boolean => boolean ? "TRUE" : "FALSE",
            _ => Convert.ToString(value, Culture) ?? ""
        };
    }

    private static void RemoveBuildingGenerations(SqliteConnection connection, long file)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var generation in IndexDatabase.ReadIds(connection, transaction, "SELECT id FROM generations WHERE file_id=$file AND state='building'", ("$file", file)))
            IndexDatabase.DeleteGeneration(connection, transaction, generation);
        transaction.Commit();
    }

    private static void SetOutcome(SqliteConnection connection, long file, string run, string outcome,
        string state, string? error, SqliteTransaction? transaction = null) =>
        IndexDatabase.Execute(connection, transaction, "UPDATE files SET outcome_run=$run,outcome=$outcome,state=$state,last_error=$error WHERE id=$file",
            ("$run", run), ("$outcome", outcome), ("$state", state), ("$error", error), ("$file", file));

    private static string FriendlyError(Exception error) => error switch
    {
        UnauthorizedAccessException => "没有权限读取文件。",
        FileNotFoundException => "文件已不存在。",
        ExcelDataReader.Exceptions.InvalidPasswordException => "文件需要密码，首版不读取加密文件。",
        ExcelDataReader.Exceptions.HeaderException => "无法识别 Excel 格式，文件可能损坏或只是修改了扩展名。",
        _ => error.Message
    };
}
