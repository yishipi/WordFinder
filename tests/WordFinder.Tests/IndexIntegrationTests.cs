using System.Security.Cryptography;
using WordFinder.Core;

namespace WordFinder.Tests;

public sealed class IndexIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WordFinderTests", Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase database;
    private readonly SearchService search;
    private string Sources => Path.Combine(directory, "source");

    public IndexIntegrationTests()
    {
        Directory.CreateDirectory(Sources);
        database = new(Path.Combine(directory, "index"));
        database.Initialize();
        database.AddRoot(Sources, "测试分类");
        search = new(database);
    }

    private IndexSummary Run(bool retry = false, Action<IndexProgress>? progress = null, CancellationToken token = default) =>
        new IndexRunner(database).Run(Guid.NewGuid().ToString("N"), retry, progress, cancellationToken: token);

    [Fact]
    public async Task DirectoryMonitorDetectsCreateModifyRenameAndDeleteForIncrementalIndexing()
    {
        var first = Path.Combine(Sources, "first.xlsx");
        var renamed = Path.Combine(Sources, "renamed.xlsx");
        async Task ChangeAndUpdate(Action action)
        {
            var notification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitor = new DirectoryChangeMonitor(error => notification.TrySetResult(error));
            Assert.Empty(monitor.Configure(database.GetRoots()));
            action();
            Assert.False(await notification.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Run();
        }
        await ChangeAndUpdate(() => WorkbookFixture.Write(first, new TestSheet("表", [new(1, "A", "初次写入")])));
        Assert.Single(search.Search(["初次写入"]).Hits);
        await ChangeAndUpdate(() => WorkbookFixture.Write(first, new TestSheet("表", [new(1, "A", "更新后的内容")])));
        Assert.Empty(search.Search(["初次写入"]).Hits);
        Assert.Single(search.Search(["更新后的内容"]).Hits);
        await ChangeAndUpdate(() => File.Move(first, renamed));
        Assert.Equal(renamed, Assert.Single(search.Search(["更新后的内容"]).Hits).Path);
        await ChangeAndUpdate(() => File.Delete(renamed));
        Assert.Empty(search.Search(["更新后的内容"]).Hits);
    }

    [Fact]
    public async Task DirectoryMonitorIgnoresOtherFilesAndTracksDirectoryMoves()
    {
        var temporary = Path.Combine(Sources, "document.tmp");
        var renamed = Path.Combine(Sources, "document.pdf");
        var unexpected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var monitor = new DirectoryChangeMonitor(error => unexpected.TrySetResult(error)))
        {
            Assert.Empty(monitor.Configure(database.GetRoots()));
            File.WriteAllText(temporary, "content");
            File.Move(temporary, renamed);
            File.Delete(renamed);
            await Task.Delay(800);
            Assert.False(unexpected.Task.IsCompleted, "非 Excel 文件不能触发索引更新。");
        }
        var folder = Path.Combine(Sources, "业务.2026");
        var moved = Path.Combine(Sources, "业务.2027");
        async Task ExpectDirectoryEvent(Action action)
        {
            var notification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitor = new DirectoryChangeMonitor(error => notification.TrySetResult(error));
            Assert.Empty(monitor.Configure(database.GetRoots()));
            action();
            Assert.False(await notification.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await ExpectDirectoryEvent(() => Directory.CreateDirectory(folder));
        WorkbookFixture.Write(Path.Combine(folder, "data.xlsx"), new TestSheet("表", [new(1, "A", "目录内数据")]));
        Run();
        await ExpectDirectoryEvent(() => Directory.Move(folder, moved));
        Run();
        Assert.Equal(Path.Combine(moved, "data.xlsx"), Assert.Single(search.Search(["目录内数据"]).Hits).Path);
        await ExpectDirectoryEvent(() => Directory.Delete(moved, true));
        Run();
        Assert.Empty(search.Search(["目录内数据"]).Hits);
    }

    [Fact]
    public async Task DirectoryMonitorDetectsFilesRenamedIntoAndOutOfExcelFormats()
    {
        var temporary = Path.Combine(Sources, "book.tmp");
        var excel = Path.Combine(Sources, "book.xlsx");
        File.WriteAllText(temporary, "test");
        async Task ExpectExcelRename(string from, string to)
        {
            var notification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitor = new DirectoryChangeMonitor(error => notification.TrySetResult(error));
            Assert.Empty(monitor.Configure(database.GetRoots()));
            File.Move(from, to);
            Assert.False(await notification.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await ExpectExcelRename(temporary, excel);
        await ExpectExcelRename(excel, temporary);
    }

    [Fact]
    public void MergedValuesMatchEveryCoveredRowAndRetainOriginalRange()
    {
        var path = Path.Combine(Sources, "merged.xlsx");
        WorkbookFixture.Write(path, new TestSheet("表", [new(1, "C", "张三"), new(3, "G", "上海"), new(6, "G", "上海")],
            Merges: ["C1:F5"]));
        Assert.Equal(1, Run().Parsed);
        var hits = search.Search(["张三"]).Hits;
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, hits.Select(hit => hit.Row).Order().ToArray());
        Assert.All(hits, hit => Assert.Contains("C1:F5", hit.Preview));
        var combined = Assert.Single(search.Search(["张三", "上海"]).Hits);
        Assert.Equal(3, combined.Row);
        Assert.Contains("C1:F5", combined.Preview);
        Assert.Contains("G3", combined.Preview);
        Assert.Contains("张三", search.CopyRow(combined.RowId));
        Assert.Equal(1, Run().Skipped);
    }

    [Fact]
    public void MultipleMergedRangesDoNotCrossSheetsAndUpdateWithTheFile()
    {
        var path = Path.Combine(Sources, "merges.xlsx");
        WorkbookFixture.Write(path,
            new TestSheet("一", [new(1, "C", "姓名"), new(1, "G", "区域"), new(2, "I", "上海")], Merges: ["C1:F5", "G1:H3"]),
            new TestSheet("二", [new(3, "A", "上海")], Merges: ["A3:A4"]));
        Assert.Equal(1, Run().Parsed);
        Assert.Equal(new[] { 1, 2, 3 }, search.Search(["姓名", "区域"]).Hits.Select(hit => hit.Row).Order().ToArray());
        Assert.Equal(2, Assert.Single(search.Search(["姓名", "上海"]).Hits).Row);
        Assert.Equal(2, search.Search(["上海"]).Hits.Count(hit => hit.Sheet == "二"));
        WorkbookFixture.Write(path, new TestSheet("一", [new(1, "C", "新姓名")], Merges: ["C1:C2"]));
        Assert.Equal(1, Run().Parsed);
        Assert.Empty(search.Search(["区域"]).Hits);
        Assert.Equal(2, search.Search(["新姓名"]).Hits.Count);
    }

    [Fact]
    public void UpgradePreservesExistingIndexAndForcesOneReindex()
    {
        var path = Path.Combine(Sources, "upgrade.xlsx");
        WorkbookFixture.Write(path, new TestSheet("表", [new(1, "C", "保留")], Merges: ["C1:F5"]));
        Assert.Equal(1, Run().Parsed);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE cells DROP COLUMN merge_first_row; ALTER TABLE cells DROP COLUMN merge_last_row; ALTER TABLE cells DROP COLUMN merge_last_column; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        database.Initialize();
        Assert.NotEmpty(search.Search(["保留"]).Hits);
        Assert.All(search.Search(["保留"]).Hits, hit => Assert.True(hit.IsStale));
        Assert.Equal(1, Run().Parsed);
        Assert.Equal(5, search.Search(["保留"]).Hits.Count);
        Assert.Equal(1, Run().Skipped);
    }

    [Fact]
    public void MatchesTargetsOnSameRowAcrossCellsAndPreservesRealCoordinates()
    {
        WorkbookFixture.Write(Path.Combine(Sources, "people.xlsx"),
            new TestSheet("名单", [new(12, "B", "张三"), new(12, "F", "上海浦东"), new(13, "B", "张三"), new(14, "F", "上海")]),
            new TestSheet("另一张表", [new(13, "F", "上海")]));
        Assert.Equal(1, Run().Parsed);
        var hit = Assert.Single(search.Search(["张三", "上海"]).Hits);
        Assert.Equal(12, hit.Row);
        Assert.Equal("名单", hit.Sheet);
        Assert.Contains("B12", hit.Preview);
        Assert.Contains("F12", hit.Preview);
        Assert.Empty(search.Search(["张三上海"]).Hits);
        Assert.Equal("\t张三\t\t\t\t上海浦东", search.CopyRow(hit.RowId));
    }

    [Fact]
    public void ReadsHiddenContentCachedFormulaAndNumberFormattingWithoutChangingSource()
    {
        var path = Path.Combine(Sources, "hidden.xlsx");
        WorkbookFixture.Write(path, new TestSheet("隐藏表", [new(20, "B", "隐藏数据"), new(20, "G", "123", "n", 1), new(20, "H", "2", "n", 0, "1+1")], true));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        Run();
        var hit = Assert.Single(search.Search(["隐藏数据", "00123", "2"]).Hits);
        Assert.Equal(20, hit.Row);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void HandlesLiteralPunctuationSpacesCaseAndUnicode()
    {
        WorkbookFixture.Write(Path.Combine(Sources, "literal.xlsx"), new TestSheet("表", [new(1, "A", "AbC-001_% \" 😀"), new(2, "A", "张三 上海")]));
        Run();
        Assert.Single(search.Search(["abc-001_%", "😀", "\""]).Hits);
        Assert.Single(search.Search(["张三 上海"]).Hits);
        Assert.Empty(search.Search(["张三  上海"]).Hits);
        Assert.Empty(search.Search(["' OR 1=1 --"]).Hits);
        Assert.Single(search.Search(["海"]).Hits);
    }

    [Fact]
    public void SameCellCanSatisfyMultipleTargetsAndSourceIsNotReadDuringSearch()
    {
        var path = Path.Combine(Sources, "offline.xlsx");
        WorkbookFixture.Write(path, new TestSheet("表", [new(7, "A", "张三 上海")]));
        Run();
        File.Delete(path);
        Assert.Single(search.Search(["张三", "上海"]).Hits);
    }

    [Fact]
    public void IncrementalUpdateSkipsUnchangedAndRemovesDeletedData()
    {
        var path = Path.Combine(Sources, "changes.xlsx");
        WorkbookFixture.Write(path, new TestSheet("表", [new(1, "A", "旧值")]));
        Assert.Equal(1, Run().Parsed);
        Assert.Equal(1, Run().Skipped);
        WorkbookFixture.Write(path, new TestSheet("表", [new(1, "A", "修改后的新值")]));
        Assert.Equal(1, Run().Parsed);
        Assert.Empty(search.Search(["旧值"]).Hits);
        Assert.Single(search.Search(["修改后的新值"]).Hits);
        File.Delete(path);
        Run();
        Assert.Empty(search.Search(["新值"]).Hits);
        var stats = database.GetStatistics();
        Assert.Equal(0, stats.Files);
        Assert.Equal(0, stats.Texts);
    }

    [Fact]
    public void FailurePreservesPreviousVersionAndDoesNotPreventOtherFilesOrRetry()
    {
        var broken = Path.Combine(Sources, "a.xlsx");
        WorkbookFixture.Write(broken, new TestSheet("表", [new(1, "A", "保留旧索引")]));
        Run();
        File.WriteAllText(broken, "broken workbook");
        WorkbookFixture.Write(Path.Combine(Sources, "b.xlsx"), new TestSheet("表", [new(1, "A", "其他文件")]));
        var result = Run();
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Parsed);
        Assert.True(Assert.Single(search.Search(["保留旧索引"]).Hits).IsStale);
        Assert.Single(search.Search(["其他文件"]).Hits);
        Assert.Single(database.GetFailures());
        WorkbookFixture.Write(broken, new TestSheet("表", [new(1, "A", "重试成功")]));
        Assert.Equal(1, Run(true).Parsed);
        Assert.Empty(database.GetFailures());
        Assert.Empty(search.Search(["保留旧索引"]).Hits);
        Assert.Single(search.Search(["重试成功"]).Hits);
    }

    [Fact]
    public void CategoriesShareOverlappingFilesAndRemovingOneDoesNotRemoveOther()
    {
        var child = Path.Combine(Sources, "child");
        WorkbookFixture.Write(Path.Combine(child, "shared.xlsx"), new TestSheet("表", [new(1, "A", "共同内容")]));
        var root = database.GetRoots()[0];
        var second = database.AddRoot(child, "子目录");
        Assert.Equal(1, Run().Parsed);
        Assert.Single(search.Search(["共同内容"], root.Id).Hits);
        Assert.Single(search.Search(["共同内容"], second).Hits);
        database.RenameRoot(second, "新标记");
        Assert.Contains("新标记", Assert.Single(search.Search(["共同内容"]).Hits).Categories);
        database.RemoveRoot(root.Id);
        Assert.Single(search.Search(["共同内容"]).Hits);
        database.RemoveRoot(second);
        Assert.Empty(search.Search(["共同内容"]).Hits);
        Assert.Equal(0, database.GetStatistics().Texts);
    }

    [Fact]
    public void PaginatesAfterAllTargetsAndNotBeforeRowIntersection()
    {
        var cells = Enumerable.Range(1, 150).Select(row => new TestCell(row, "A", "共同目标")).ToList();
        cells.Add(new(150, "F", "稀有目标"));
        WorkbookFixture.Write(Path.Combine(Sources, "pages.xlsx"), new TestSheet("表", cells.ToArray()));
        Run();
        Assert.Equal(150, Assert.Single(search.Search(["共同目标", "稀有目标"], pageSize: 10).Hits).Row);
        var first = search.Search(["共同目标"], pageSize: 50);
        var second = search.Search(["共同目标"], afterRowId: first.Hits[^1].RowId, pageSize: 50);
        Assert.True(first.HasMore);
        Assert.Equal(50, second.Hits.Count);
        Assert.Empty(first.Hits.Select(hit => hit.RowId).Intersect(second.Hits.Select(hit => hit.RowId)));
    }

    [Fact]
    public void DeduplicatesAndCompressesTextAndSupportsClearAndCompact()
    {
        var longText = string.Concat(Enumerable.Repeat("重复内容 abc 编号123。", 100));
        WorkbookFixture.Write(Path.Combine(Sources, "repeat.xlsx"), new TestSheet("表",
            Enumerable.Range(1, 1000).Select(row => new TestCell(row, "A", longText)).ToArray()));
        Run();
        var stats = database.GetStatistics();
        Assert.Equal(1000, stats.Cells);
        Assert.Equal(1, stats.Texts);
        database.Compact();
        Assert.Equal(50, search.Search(["编号123"]).Hits.Count);
        database.ClearIndex();
        Assert.Empty(search.Search(["编号123"]).Hits);
        Assert.Single(database.GetRoots());
        Assert.Equal(0, database.GetStatistics().Texts);
        Assert.Equal(1, Run().Parsed);
        Assert.Equal(50, search.Search(["编号123"]).Hits.Count);
    }

    [Fact]
    public void CancellationDoesNotPublishPartialVersionAndCanResumeByFile()
    {
        var path = Path.Combine(Sources, "cancel.xlsx");
        WorkbookFixture.Write(path, new TestSheet("表", [new(1, "A", "原来的数据")]));
        Run();
        WorkbookFixture.Write(path, new TestSheet("表", Enumerable.Range(1, 16000).Select(row => new TestCell(row, "A", "新数据")).ToArray()));
        using var cancellation = new CancellationTokenSource();
        var result = Run(progress: progress => { if (progress.Cells >= 10_000) cancellation.Cancel(); }, token: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Single(search.Search(["原来的数据"]).Hits);
        Assert.Empty(search.Search(["新数据"]).Hits);
        Assert.Equal(1, Run().Parsed);
        Assert.Empty(search.Search(["原来的数据"]).Hits);
        Assert.Single(search.Search(["新数据"], pageSize: 1).Hits);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
