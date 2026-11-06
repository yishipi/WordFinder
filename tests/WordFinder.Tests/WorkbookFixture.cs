using System.IO.Compression;
using System.Xml.Linq;

namespace WordFinder.Tests;

internal sealed record TestCell(int Row, string Column, string Value, string Type = "inlineStr", int Style = 0, string? Formula = null);
internal sealed record TestSheet(string Name, TestCell[] Cells, bool Hidden = false);

internal static class WorkbookFixture
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(string path, params TestSheet[] sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var contentTypes = XElement.Parse("""
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            </Types>
            """);
        var typesNamespace = contentTypes.Name.Namespace;
        contentTypes.Add(new XElement(typesNamespace + "Override", new XAttribute("PartName", "/xl/styles.xml"),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
        for (var index = 0; index < sheets.Length; index++)
            contentTypes.Add(new XElement(typesNamespace + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{index + 1}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        Add(archive, "[Content_Types].xml", contentTypes.ToString());
        Add(archive, "_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        var workbook = new XElement(Spreadsheet + "workbook", new XAttribute(XNamespace.Xmlns + "r", Relations),
            new XElement(Spreadsheet + "sheets", sheets.Select((sheet, index) =>
                new XElement(Spreadsheet + "sheet", new XAttribute("name", sheet.Name), new XAttribute("sheetId", index + 1),
                    new XAttribute("state", sheet.Hidden ? "hidden" : "visible"), new XAttribute(Relations + "id", $"rId{index + 1}")))));
        Add(archive, "xl/workbook.xml", workbook.ToString());
        var packageRelations = (XNamespace)"http://schemas.openxmlformats.org/package/2006/relationships";
        var relations = new XElement(packageRelations + "Relationships", sheets.Select((_, index) => new XElement(packageRelations + "Relationship",
            new XAttribute("Id", $"rId{index + 1}"), new XAttribute("Type", Relations.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{index + 1}.xml"))));
        relations.Add(new XElement(packageRelations + "Relationship", new XAttribute("Id", "styles"),
            new XAttribute("Type", Relations.NamespaceName + "/styles"), new XAttribute("Target", "styles.xml")));
        Add(archive, "xl/_rels/workbook.xml.rels", relations.ToString());
        Add(archive, "xl/styles.xml", """
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <numFmts count="1"><numFmt numFmtId="164" formatCode="00000"/></numFmts>
              <fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders>
              <cellStyleXfs count="1"><xf numFmtId="0"/></cellStyleXfs>
              <cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="164" applyNumberFormat="1"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>
            </styleSheet>
            """);
        for (var index = 0; index < sheets.Length; index++)
        {
            var rows = sheets[index].Cells.GroupBy(cell => cell.Row).OrderBy(group => group.Key).Select(group =>
                new XElement(Spreadsheet + "row", new XAttribute("r", group.Key), new XAttribute("hidden", sheets[index].Hidden ? "1" : "0"), group.Select(cell =>
                {
                    var node = new XElement(Spreadsheet + "c", new XAttribute("r", $"{cell.Column}{cell.Row}"), new XAttribute("s", cell.Style));
                    if (cell.Formula is not null) node.Add(new XElement(Spreadsheet + "f", cell.Formula));
                    if (cell.Type == "inlineStr")
                    {
                        node.Add(new XAttribute("t", "inlineStr"), new XElement(Spreadsheet + "is", new XElement(Spreadsheet + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"), cell.Value)));
                    }
                    else node.Add(new XAttribute("t", cell.Type), new XElement(Spreadsheet + "v", cell.Value));
                    return node;
                })));
            Add(archive, $"xl/worksheets/sheet{index + 1}.xml",
                new XElement(Spreadsheet + "worksheet", new XElement(Spreadsheet + "sheetData", rows)).ToString());
        }
    }

    private static void Add(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }
}
