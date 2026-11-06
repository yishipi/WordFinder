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
