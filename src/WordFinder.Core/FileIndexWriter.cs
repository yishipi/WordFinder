using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ExcelDataReader;

namespace WordFinder.Core;

internal sealed class FileIndexWriter : IDisposable
{
    private readonly SqliteConnection connection;
    private SqliteTransaction? transaction;
    private readonly SqliteCommand findText, insertText, insertFts, insertRow, insertCell;
    private readonly Dictionary<string, long> textCache = new(StringComparer.Ordinal);
    private int cacheCharacters;
    private int batchCells;
    public long Rows { get; private set; }
    public long Cells { get; private set; }

    public FileIndexWriter(SqliteConnection connection)
    {
        this.connection = connection;
        findText = IndexDatabase.Command(connection, null, "SELECT id,payload,codec FROM text_values WHERE hash=$hash", ("$hash", Array.Empty<byte>()));
        insertText = IndexDatabase.Command(connection, null, "INSERT INTO text_values(hash,payload,codec) VALUES($hash,$payload,$codec) RETURNING id",
            ("$hash", Array.Empty<byte>()), ("$payload", Array.Empty<byte>()), ("$codec", 0));
        insertFts = IndexDatabase.Command(connection, null, "INSERT INTO text_fts(rowid,tokens) VALUES($id,$tokens)", ("$id", 0L), ("$tokens", ""));
        insertRow = IndexDatabase.Command(connection, null, "INSERT INTO rows(sheet_id,number) VALUES($sheet,$row) RETURNING id", ("$sheet", 0L), ("$row", 0));
        insertCell = IndexDatabase.Command(connection, null, "INSERT INTO cells(row_id,column_index,value_id) VALUES($row,$column,$value)",
            ("$row", 0L), ("$column", 0), ("$value", 0L));
        BeginBatch();
    }

    public long AddSheet(long generation, string name, int ordinal) => Convert.ToInt64(IndexDatabase.Scalar(connection, transaction,
        "INSERT INTO sheets(generation_id,name,ordinal) VALUES($gen,$name,$ordinal) RETURNING id",
        ("$gen", generation), ("$name", name), ("$ordinal", ordinal)));

    public void AddRow(long sheet, int row, IReadOnlyList<CellValue> cells)
    {
        insertRow.Parameters["$sheet"].Value = sheet;
        insertRow.Parameters["$row"].Value = row;
        var rowId = Convert.ToInt64(insertRow.ExecuteScalar());
        foreach (var cell in cells)
        {
            var valueId = GetTextId(cell.Text);
            insertCell.Parameters["$row"].Value = rowId;
            insertCell.Parameters["$column"].Value = cell.Column;
            insertCell.Parameters["$value"].Value = valueId;
            insertCell.ExecuteNonQuery();
            Cells++;
            batchCells++;
        }
        Rows++;
        if (batchCells >= 10_000)
        {
            transaction!.Commit();
            transaction.Dispose();
            BeginBatch();
            batchCells = 0;
        }
    }

    public void AddMergedRange(long sheet, CellRange range, Action check)
    {
        var first = range.FromRow + 1;
        var last = range.ToRow + 1;
        var column = range.FromColumn;
        var anchor = IndexDatabase.Scalar(connection, transaction, """
            SELECT c.value_id FROM cells c JOIN rows r ON r.id=c.row_id
            WHERE r.sheet_id=$sheet AND r.number=$row AND c.column_index=$column
            """, ("$sheet", sheet), ("$row", first), ("$column", column));
        if (anchor is null) return;
        var id = Convert.ToInt64(anchor);
        IndexDatabase.Execute(connection, transaction, """
            UPDATE cells SET merge_first_row=$first,merge_last_row=$last,merge_last_column=$end
            WHERE row_id=(SELECT id FROM rows WHERE sheet_id=$sheet AND number=$first) AND column_index=$column
            """, ("$first", first), ("$last", last), ("$end", range.ToColumn), ("$sheet", sheet), ("$column", column));
        for (var row = first + 1; row <= last; row++)
        {
            check();
            var added = IndexDatabase.Execute(connection, transaction, "INSERT OR IGNORE INTO rows(sheet_id,number) VALUES($sheet,$row)", ("$sheet", sheet), ("$row", row));
            Rows += added;
            var inserted = IndexDatabase.Execute(connection, transaction, """
                INSERT OR IGNORE INTO cells(row_id,column_index,value_id,merge_first_row,merge_last_row,merge_last_column)
                SELECT id,$column,$value,$first,$last,$end FROM rows WHERE sheet_id=$sheet AND number=$row
                """, ("$column", column), ("$value", id), ("$first", first), ("$last", last),
                ("$end", range.ToColumn), ("$sheet", sheet), ("$row", row));
            Cells += inserted;
            batchCells += inserted;
            if (batchCells < 10_000) continue;
            transaction!.Commit();
            transaction.Dispose();
            BeginBatch();
            batchCells = 0;
        }
    }

    private long GetTextId(string text)
    {
        if (textCache.TryGetValue(text, out var cached)) return cached;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        findText.Parameters["$hash"].Value = hash;
        long id;
        using (var reader = findText.ExecuteReader())
        {
            if (reader.Read())
            {
                id = reader.GetInt64(0);
                if (!string.Equals(TextStorage.Decode((byte[])reader[1], reader.GetInt64(2)), text, StringComparison.Ordinal))
                    throw new InvalidDataException("文本校验冲突，已停止当前文件以保护索引。");
            }
            else id = 0;
        }
        if (id == 0)
        {
            var encoded = TextStorage.Encode(text);
            insertText.Parameters["$hash"].Value = hash;
            insertText.Parameters["$payload"].Value = encoded.Payload;
            insertText.Parameters["$codec"].Value = encoded.Codec;
            id = Convert.ToInt64(insertText.ExecuteScalar());
            insertFts.Parameters["$id"].Value = id;
            insertFts.Parameters["$tokens"].Value = TextStorage.Tokens(TextStorage.Normalize(text));
            insertFts.ExecuteNonQuery();
        }
        if (text.Length <= 65_536)
        {
            if (textCache.Count >= 2048 || cacheCharacters + text.Length > 1_000_000)
            {
                textCache.Clear();
                cacheCharacters = 0;
            }
            textCache[text] = id;
            cacheCharacters += text.Length;
        }
        return id;
    }

    private void BeginBatch()
    {
        transaction = connection.BeginTransaction();
        foreach (var command in new[] { findText, insertText, insertFts, insertRow, insertCell })
            command.Transaction = transaction;
    }

    public void Complete()
    {
        transaction!.Commit();
        transaction.Dispose();
        transaction = null;
    }

    public void Dispose()
    {
        transaction?.Dispose();
        findText.Dispose();
        insertText.Dispose();
        insertFts.Dispose();
        insertRow.Dispose();
        insertCell.Dispose();
    }
}
