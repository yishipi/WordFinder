using Microsoft.Data.Sqlite;

namespace WordFinder.Core;

public sealed class SearchService(IndexDatabase database)
{
    public SearchPage Search(IEnumerable<string> targets, long? rootId = null, long afterRowId = 0,
        int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var normalized = targets.Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(TextStorage.Normalize).Distinct(StringComparer.Ordinal).OrderByDescending(target => target.Length).ToArray();
        if (normalized.Length == 0) return new([], false);
        if (normalized.Length > 16) throw new ArgumentException("最多支持 16 个搜索目标。");
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var connection = database.Open();
        connection.CreateFunction<byte[], long, string, bool>("text_contains", (payload, codec, target) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TextStorage.Contains(TextStorage.Decode(payload, codec), target);
        });
        var conditions = new List<string>();
        var matches = new List<string>();
        for (var index = 0; index < normalized.Length; index++)
        {
            // Verify each distinct text once, before expanding its many cell references.
            matches.Add($"matched{index} AS MATERIALIZED (SELECT v.id FROM text_fts JOIN text_values v ON v.id=text_fts.rowid WHERE text_fts MATCH $query{index} AND text_contains(v.payload,v.codec,$target{index}))");
            if (index > 0) conditions.Add($"EXISTS(SELECT 1 FROM cells cx WHERE cx.row_id=r.id AND cx.value_id IN(SELECT id FROM matched{index}))");
        }
        using var command = IndexDatabase.Command(connection, null, $$"""
            WITH {{string.Join(",", matches)}}
            SELECT r.id,f.path,s.name,r.number,f.state,
                (SELECT group_concat(label,'、') FROM roots rt JOIN root_files rf ON rf.root_id=rt.id WHERE rf.file_id=f.id)
            FROM rows r
            JOIN sheets s ON s.id=r.sheet_id JOIN files f ON f.active_generation=s.generation_id
            WHERE r.id IN(SELECT c.row_id FROM cells c WHERE c.value_id IN(SELECT id FROM matched0) AND c.row_id>$after)
                AND ($root IS NULL OR EXISTS(SELECT 1 FROM root_files rf WHERE rf.file_id=f.id AND rf.root_id=$root))
                {{(conditions.Count == 0 ? "" : "AND " + string.Join(" AND ", conditions))}}
            ORDER BY r.id LIMIT $limit
            """, ("$after", afterRowId),
            ("$root", rootId), ("$limit", pageSize + 1));
        for (var index = 0; index < normalized.Length; index++)
        {
            command.Parameters.AddWithValue($"$target{index}", normalized[index]);
            command.Parameters.AddWithValue($"$query{index}", TextStorage.Tokens(normalized[index], true));
        }
        command.CommandTimeout = 20;
        using var registration = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<SearchHit>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.IsDBNull(5) ? "" : reader.GetString(5), "", reader.GetString(4) != "ready"));
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hit = rows[index];
            var matching = GetCells(connection, hit.RowId).Where(cell => normalized.Any(target => TextStorage.Contains(cell.Text, target)));
            var preview = string.Join("；", matching.Take(3).Select(cell => $"{CellAddress.Format(cell.Column, hit.Row)} {TextStorage.Preview(cell.Text, 70)}"));
            rows[index] = hit with { Preview = preview };
        }
        return new(rows, hasMore);
    }

    public IReadOnlyList<CellValue> GetCells(long rowId)
    {
        using var connection = database.Open();
        return GetCells(connection, rowId);
    }

    private static IReadOnlyList<CellValue> GetCells(SqliteConnection connection, long rowId)
    {
        using var command = IndexDatabase.Command(connection, null, """
            SELECT c.column_index,v.payload,v.codec FROM cells c JOIN text_values v ON v.id=c.value_id
            WHERE c.row_id=$row ORDER BY c.column_index
            """, ("$row", rowId));
        using var reader = command.ExecuteReader();
        var cells = new List<CellValue>();
        while (reader.Read()) cells.Add(new(reader.GetInt32(0), TextStorage.Decode((byte[])reader[1], reader.GetInt64(2))));
        return cells;
    }

    public string CopyRow(long rowId)
    {
        var cells = GetCells(rowId);
        if (cells.Count == 0) return "";
        var values = new string[cells[^1].Column + 1];
        Array.Fill(values, "");
        foreach (var cell in cells)
            values[cell.Column] = cell.Text.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
                ? "\"" + cell.Text.Replace("\"", "\"\"") + "\"" : cell.Text;
        return string.Join('\t', values);
    }
}
