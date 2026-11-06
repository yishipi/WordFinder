using Microsoft.Data.Sqlite;

namespace WordFinder.Core;

public sealed class IndexDatabase(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string DatabasePath => Path.Combine(DirectoryPath, "index.db");

    public void Initialize()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var connection = Open();
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version > 1) throw new InvalidDataException("索引由较新版本创建，请使用较新版 WordFinder。");
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS roots(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                indexed_size INTEGER NOT NULL DEFAULT -1, indexed_write INTEGER NOT NULL DEFAULT -1,
                active_generation INTEGER, state TEXT NOT NULL DEFAULT 'new', last_error TEXT,
                outcome_run TEXT, outcome TEXT, rows_count INTEGER NOT NULL DEFAULT 0,
                cells_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS root_files(
                root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                seen_run TEXT NOT NULL, PRIMARY KEY(root_id,file_id)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS root_files_file ON root_files(file_id,root_id);
            CREATE TABLE IF NOT EXISTS generations(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                state TEXT NOT NULL DEFAULT 'building');
            CREATE INDEX IF NOT EXISTS generations_file ON generations(file_id);
            CREATE TABLE IF NOT EXISTS sheets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                generation_id INTEGER NOT NULL REFERENCES generations(id) ON DELETE CASCADE,
                name TEXT NOT NULL, ordinal INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS sheets_generation ON sheets(generation_id);
            CREATE TABLE IF NOT EXISTS rows(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sheet_id INTEGER NOT NULL REFERENCES sheets(id) ON DELETE CASCADE,
                number INTEGER NOT NULL, UNIQUE(sheet_id,number));
            CREATE TABLE IF NOT EXISTS text_values(
                id INTEGER PRIMARY KEY AUTOINCREMENT, hash BLOB NOT NULL UNIQUE,
                payload BLOB NOT NULL, codec INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS cells(
                row_id INTEGER NOT NULL REFERENCES rows(id) ON DELETE CASCADE,
                column_index INTEGER NOT NULL,
                value_id INTEGER NOT NULL REFERENCES text_values(id),
                PRIMARY KEY(row_id,column_index)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS cells_value_row ON cells(value_id,row_id,column_index);
            CREATE VIRTUAL TABLE IF NOT EXISTS text_fts USING fts5(
                tokens, content='', contentless_delete=1, detail=none);
            CREATE TABLE IF NOT EXISTS garbage_values(value_id INTEGER PRIMARY KEY);
            PRAGMA user_version=1;
            """);
        transaction.Commit();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = true, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA cache_size=-4096; PRAGMA temp_store=FILE;");
        return connection;
    }

    public IReadOnlyList<DirectoryRoot> GetRoots()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT id,path,label FROM roots ORDER BY label,id");
        using var reader = command.ExecuteReader();
        var roots = new List<DirectoryRoot>();
        while (reader.Read()) roots.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return roots;
    }

    public long AddRoot(string path, string? label = null)
    {
        path = NormalizePath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在或无法访问。");
        if (!IsLocalPath(path)) throw new ArgumentException("首版只支持本机磁盘文件夹，不支持共享路径或映射网络盘。");
        label = string.IsNullOrWhiteSpace(label) ? new DirectoryInfo(path).Name : label.Trim();
        if (label.Length == 0) label = path;
        using var connection = Open();
        Execute(connection, null, """
            INSERT INTO roots(path,path_key,label) VALUES($path,$key,$label)
            ON CONFLICT(path_key) DO NOTHING
            """, ("$path", path), ("$key", path.ToUpperInvariant()), ("$label", label));
        return Convert.ToInt64(Scalar(connection, null, "SELECT id FROM roots WHERE path_key=$key",
            ("$key", path.ToUpperInvariant())));
    }

    public void RenameRoot(long id, string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 80)
            throw new ArgumentException("分类标记应为 1–80 个字符。");
        using var connection = Open();
        Execute(connection, null, "UPDATE roots SET label=$label WHERE id=$id", ("$label", label.Trim()), ("$id", id));
    }

    public void RemoveRoot(long id)
    {
        using var connection = Open();
        Execute(connection, null, "DELETE FROM roots WHERE id=$id", ("$id", id));
        PruneUnreferencedFiles(connection);
        CollectGarbage(connection);
    }

    public IReadOnlyList<IndexFailure> GetFailures()
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT id,path,COALESCE(last_error,'未完成'),active_generation IS NOT NULL
            FROM files WHERE state IN ('failed','interrupted') ORDER BY path
            """);
        using var reader = command.ExecuteReader();
        var failures = new List<IndexFailure>();
        while (reader.Read()) failures.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return failures;
    }

    public IndexStatistics GetStatistics()
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT COUNT(CASE WHEN active_generation IS NOT NULL THEN 1 END),
                COALESCE(SUM(rows_count),0),COALESCE(SUM(cells_count),0),
                COUNT(CASE WHEN state IN ('failed','interrupted') THEN 1 END),
                COALESCE(SUM(CASE WHEN active_generation IS NOT NULL THEN indexed_size ELSE 0 END),0)
            FROM files
            """);
        using var reader = command.ExecuteReader();
        reader.Read();
        var values = Enumerable.Range(0, 5).Select(reader.GetInt64).ToArray();
        reader.Close();
        var texts = Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM text_values"));
        return new(values[0], values[1], values[2], texts, values[3], values[4], StorageBytes());
    }

    public long StorageBytes() => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateFiles(DirectoryPath, "index.db*").Sum(path => new FileInfo(path).Length)
        : 0;

    public void ClearIndex()
    {
        using var connection = Open();
        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, transaction, """
                DELETE FROM files;
                DELETE FROM text_fts;
                DELETE FROM text_values;
                DELETE FROM garbage_values;
                """);
            transaction.Commit();
        }
        Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public void Compact()
    {
        using var connection = Open();
        CollectGarbage(connection);
        Execute(connection, null, "INSERT INTO text_fts(text_fts) VALUES('optimize'); PRAGMA wal_checkpoint(TRUNCATE);");
        using var command = Command(connection, null, "VACUUM;");
        command.CommandTimeout = 120;
        command.ExecuteNonQuery();
        Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public int RecoverInterrupted(string? runId = null, string message = "上次索引未完成，可点击更新索引或重试。")
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var ids = ReadIds(connection, transaction, "SELECT id FROM generations WHERE state='building'");
        foreach (var id in ids) DeleteGeneration(connection, transaction, id);
        var count = Execute(connection, transaction, """
            UPDATE files SET state='interrupted',last_error=$message,outcome='failed',
                outcome_run=COALESCE($run,outcome_run) WHERE state='processing'
            """, ("$message", message), ("$run", runId));
        transaction.Commit();
        CollectGarbage(connection);
        return count;
    }

    internal static void DeleteGeneration(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        Execute(connection, transaction, """
            INSERT OR IGNORE INTO garbage_values(value_id)
                SELECT DISTINCT c.value_id FROM cells c JOIN rows r ON r.id=c.row_id
                JOIN sheets s ON s.id=r.sheet_id WHERE s.generation_id=$id;
            DELETE FROM generations WHERE id=$id;
            """, ("$id", id));
    }

    internal static void PruneUnreferencedFiles(SqliteConnection connection)
    {
        var files = ReadIds(connection, null, "SELECT id FROM files f WHERE NOT EXISTS(SELECT 1 FROM root_files rf WHERE rf.file_id=f.id)");
        foreach (var file in files)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var id in ReadIds(connection, transaction, "SELECT id FROM generations WHERE file_id=$file", ("$file", file)))
                DeleteGeneration(connection, transaction, id);
            Execute(connection, transaction, "DELETE FROM files WHERE id=$file", ("$file", file));
            transaction.Commit();
        }
    }

    internal static void CollectGarbage(SqliteConnection connection)
    {
        while (true)
        {
            var ids = ReadIds(connection, null, "SELECT value_id FROM garbage_values LIMIT 1000");
            if (ids.Count == 0) return;
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
            {
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT EXISTS(SELECT 1 FROM cells WHERE value_id=$id)", ("$id", id))) == 0)
                {
                    Execute(connection, transaction, "DELETE FROM text_fts WHERE rowid=$id; DELETE FROM text_values WHERE id=$id", ("$id", id));
                }
                Execute(connection, transaction, "DELETE FROM garbage_values WHERE value_id=$id", ("$id", id));
            }
            transaction.Commit();
        }
    }

    internal static List<long> ReadIds(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static bool IsLocalPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return !OperatingSystem.IsWindows() || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Network;
    }
}
