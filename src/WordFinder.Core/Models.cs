namespace WordFinder.Core;

public sealed record DirectoryRoot(long Id, string Path, string Label);
public sealed record IndexFailure(long Id, string Path, string Message, bool HasPreviousIndex);
public sealed record CellValue(int Column, string Text, int? MergeFirstRow = null, int? MergeLastRow = null, int? MergeLastColumn = null)
{
    public string Address(int row) => MergeFirstRow is int first && MergeLastRow is int last && MergeLastColumn is int end
        ? $"{CellAddress.Format(Column, first)}:{CellAddress.Format(end, last)}（合并）"
        : CellAddress.Format(Column, row);
}
public sealed record SearchHit(long RowId, string Path, string Sheet, int Row, string Categories,
    string Preview, bool IsStale);
public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, bool HasMore);
public sealed record IndexStatistics(long Files, long Rows, long Cells, long Texts, long Failed,
    long SourceBytes, long StorageBytes);
public sealed record IndexProgress(string Phase, string Message, string? File = null,
    int Total = 0, int Completed = 0, int Parsed = 0, int Skipped = 0, int Failed = 0,
    long Cells = 0);
public sealed record IndexSummary(int Total, int Parsed, int Skipped, int Failed, bool Cancelled);
public sealed record WorkerControl(bool Paused = false, bool Cancel = false);

public static class CellAddress
{
    public static string ColumnName(int index)
    {
        var result = "";
        for (var value = index + 1; value > 0; value = (value - 1) / 26)
            result = (char)('A' + (value - 1) % 26) + result;
        return result;
    }

    public static string Format(int column, int row) => $"{ColumnName(column)}{row}";
}
