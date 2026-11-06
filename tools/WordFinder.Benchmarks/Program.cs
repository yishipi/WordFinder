using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using WordFinder.Core;
using WordFinder.Desktop;
using WordFinder.Tests;

if (args.Length < 2) throw new ArgumentException("generate-demo|generate-large|generate-many|generate-memory|run|query <path> ...");
var mode = args[0];
var path = Path.GetFullPath(args[1]);
if (mode.StartsWith("generate-", StringComparison.Ordinal)) Directory.CreateDirectory(path);
switch (mode)
{
    case "generate-demo":
        WorkbookFixture.Write(Path.Combine(path, "客户资料.xlsx"), new TestSheet("客户名单",
            [new(12, "B", "张三"), new(12, "F", "上海浦东"), new(12, "H", "123", "n", 1),
             new(18, "B", "李四"), new(18, "F", "北京"), new(20, "B", "张三 上海"), new(20, "H", "2", "n", 0, "1+1")]),
            new TestSheet("隐藏工作表", [new(8, "C", "隐藏样本 张三 上海")], true));
        WorkbookFixture.Write(Path.Combine(path, "订单资料.xlsx"), new TestSheet("订单",
            [new(5, "A", "ORDER-AbC-001_%"), new(5, "B", "张三"), new(5, "G", "上海"), new(6, "B", "王五"), new(7, "G", "上海")]));
        File.WriteAllText(Path.Combine(path, "异常示例.xlsx"), "This is deliberately not an Excel workbook.");
        Console.WriteLine(path);
        break;
    case "generate-many":
        var count = args.Length > 2 ? int.Parse(args[2]) : 10_000;
        for (var index = 0; index < count; index++)
        {
            WorkbookFixture.Write(Path.Combine(path, $"分类{index % 3}", $"文件{index:D5}.xlsx"), new TestSheet("名单",
                [new(2, "A", $"编号{index:D8}"), new(2, "B", index % 2 == 0 ? "张三" : "李四"), new(2, "F", index % 3 == 0 ? "上海" : "北京")]));
            if (index % 1000 == 0) Console.WriteLine($"Generated {index}/{count}");
        }
        Console.WriteLine($"Generated {count} workbooks");
        break;
    case "generate-large":
        GenerateLarge(path, args.Length > 2 ? int.Parse(args[2]) : 500);
        break;
    case "generate-memory":
        GenerateMemory(path);
        break;
    case "run":
        if (args.Length < 4) throw new ArgumentException("run <source> <data> <desktop exe> [memoryMiB]");
        var database = new IndexDatabase(args[2]);
        database.Initialize();
        database.RecoverInterrupted();
        database.AddRoot(path, new DirectoryInfo(path).Name);
        var limit = args.Length > 4 ? int.Parse(args[4]) : 512;
        var controller = new IndexerController(database, limit, Path.GetFullPath(args[3]));
        var watch = Stopwatch.StartNew();
        var task = controller.RunAsync(false);
        var tick = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(1000));
            if (tick.ElapsedMilliseconds < 5000) continue;
            tick.Restart();
            Console.WriteLine(JsonSerializer.Serialize(controller.Latest));
        }
        var summary = await task;
        var elapsed = watch.Elapsed;
        var stats = database.GetStatistics();
        var service = new SearchService(database);
        var queryTimes = new List<double>();
        var hits = 0;
        for (var index = 0; index < 10; index++)
        {
            watch.Restart();
            hits = service.Search(["张三", "上海"]).Hits.Count;
            queryTimes.Add(watch.Elapsed.TotalMilliseconds);
        }
        var report = new { Summary = summary, Seconds = elapsed.TotalSeconds, LimitMiB = limit,
            WorkerPeakCommittedMiB = controller.PeakCommittedBytes / (1024d * 1024), Statistics = stats,
            FirstPageHits = hits, QueryMilliseconds = queryTimes, Warnings = controller.Messages,
            OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString() };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(database.DirectoryPath, "benchmark-report.json"), json);
        Console.WriteLine(json);
        break;
    case "control":
        if (args.Length < 4) throw new ArgumentException("control <source> <data> <desktop exe>");
        var controlDatabase = new IndexDatabase(args[2]);
        controlDatabase.Initialize();
        controlDatabase.AddRoot(path);
        var supervised = new IndexerController(controlDatabase, 512, Path.GetFullPath(args[3]));
        var supervisedTask = supervised.RunAsync(false);
        var deadline = Stopwatch.StartNew();
        while (!supervisedTask.IsCompleted && supervised.Latest.Phase != "reading" && deadline.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        if (supervisedTask.IsCompleted || supervised.Latest.Phase != "reading") throw new InvalidOperationException("No active reading phase to pause.");
        supervised.SetPaused(true);
        while (!supervisedTask.IsCompleted && supervised.Latest.Phase != "paused" && deadline.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(50);
        if (supervised.Latest.Phase != "paused") throw new InvalidOperationException("Worker did not pause.");
        var pausedCells = supervised.Latest.Cells;
        await Task.Delay(400);
        if (supervisedTask.IsCompleted || supervised.Latest.Cells != pausedCells || supervised.Latest.Phase != "paused") throw new InvalidOperationException("Paused worker continued reading.");
        supervised.SetPaused(false);
        while (!supervisedTask.IsCompleted && supervised.Latest.Phase == "paused" && deadline.Elapsed < TimeSpan.FromSeconds(28)) await Task.Delay(50);
        if (supervised.Latest.Phase == "paused") throw new InvalidOperationException("Worker did not resume.");
        await supervised.StopAsync();
        var stopped = await supervisedTask;
        if (!stopped.Cancelled || controlDatabase.GetStatistics().Files != 0) throw new InvalidOperationException("Cancellation published a partial file.");
        var controlReport = JsonSerializer.Serialize(new { Paused = true, Resumed = true, Cancelled = stopped.Cancelled, NoPartialFile = true }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(controlDatabase.DirectoryPath, "control-report.json"), controlReport);
        Console.WriteLine(controlReport);
        break;
    case "query":
        var queryDatabase = new IndexDatabase(path);
        queryDatabase.Initialize();
        Console.WriteLine(JsonSerializer.Serialize(new SearchService(queryDatabase).Search(args.Skip(2))));
        break;
    default: throw new ArgumentException("Unknown mode");
}

static void GenerateLarge(string directory, int mebibytes)
{
    var metadata = CreateMetadata(directory);
    var file = Path.Combine(directory, $"large-{mebibytes}MiB.xlsx");
    using var stream = File.Create(file);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    CopyMetadata(archive, metadata);
    using var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.NoCompression).Open();
    using var writer = XmlWriter.Create(sheet, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
    const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    writer.WriteStartElement("worksheet", ns);
    writer.WriteStartElement("sheetData");
    var payload = "张三 " + new string('x', 4096) + " 真实读取的重复文本";
    var row = 0;
    while (stream.Position < (long)mebibytes * 1024 * 1024)
    {
        row++;
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", row.ToString());
        foreach (var cell in new[] { ("A", row.ToString()), ("B", payload), ("F", "上海浦东") })
        {
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", cell.Item1 + row);
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is"); writer.WriteElementString("t", cell.Item2); writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        if (row % 1000 == 0) writer.Flush();
    }
    writer.WriteEndElement(); writer.WriteEndElement(); writer.Flush();
    Console.WriteLine($"Generated {row} rows; approximately {stream.Position / (1024d * 1024):N1} MiB of actual worksheet XML; {file}");
}

static Dictionary<string, byte[]> CreateMetadata(string directory)
{
    var temporary = Path.Combine(directory, "template.xlsx");
    WorkbookFixture.Write(temporary, new TestSheet("样本", [new(1, "A", "template")]));
    var result = new Dictionary<string, byte[]>();
    using (var template = ZipFile.OpenRead(temporary))
        foreach (var entry in template.Entries.Where(entry => entry.FullName != "xl/worksheets/sheet1.xml"))
        {
            using var buffer = new MemoryStream();
            using var input = entry.Open(); input.CopyTo(buffer);
            result.Add(entry.FullName, buffer.ToArray());
        }
    File.Delete(temporary);
    return result;
}

static void CopyMetadata(ZipArchive archive, Dictionary<string, byte[]> metadata)
{
    foreach (var entry in metadata)
    {
        using var output = archive.CreateEntry(entry.Key).Open();
        output.Write(entry.Value);
    }
}

static void GenerateMemory(string directory)
{
    var metadata = CreateMetadata(directory);
    using (var file = File.Create(Path.Combine(directory, "a-large-shared-strings.xlsx")))
    using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
    {
        var relations = Encoding.UTF8.GetString(metadata["xl/_rels/workbook.xml.rels"]);
        relations = relations.Replace("</Relationships>", "<Relationship Id=\"strings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
        metadata["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes(relations);
        CopyMetadata(archive, metadata);
        using (var output = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open()))
            output.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row></sheetData></worksheet>");
        using var writer = new StreamWriter(archive.CreateEntry("xl/sharedStrings.xml", CompressionLevel.Fastest).Open());
        writer.Write("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"100000\" uniqueCount=\"100000\">");
        var text = new string('x', 2048);
        for (var index = 0; index < 100_000; index++) writer.Write($"<si><t>{index:D8}{text}</t></si>");
        writer.Write("</sst>");
    }
    WorkbookFixture.Write(Path.Combine(directory, "b-small-valid.xlsx"), new TestSheet("表", [new(1, "A", "张三 上海 内存受限后继续")]));
    Console.WriteLine("Generated oversized shared string table and a following valid workbook");
}
