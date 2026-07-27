using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using GarageGames.Core.Domain;

namespace GarageGames.Controller.Services;

public sealed class ExportService(RunService runs)
{
    public async Task<byte[]> CreateCsvAsync(CancellationToken cancellationToken = default)
    {
        var leaderboard = await runs.GetLeaderboardAsync(cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("Rank,Competitor,Score,Time Remaining,Completed,Attempted,Bonuses,Finished");
        foreach (var item in leaderboard)
        {
            builder.Append(item.Rank).Append(',')
                .Append(Csv(item.CompetitorName)).Append(',')
                .Append(item.Score.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.RemainingSeconds).Append(',')
                .Append(item.Completed).Append(',')
                .Append(item.Attempted).Append(',')
                .Append(item.Bonuses).Append(',')
                .Append(item.FinishedAt.ToString("O")).AppendLine();
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public async Task<byte[]> CreateXlsxAsync(CancellationToken cancellationToken = default)
    {
        var leaderboard = await runs.GetLeaderboardAsync(cancellationToken);
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Leaderboard" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/styles.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="2"><font><sz val="11"/><name val="Aptos"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Aptos"/></font></fonts>
                  <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F2937"/><bgColor indexed="64"/></patternFill></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs>
                </styleSheet>
                """);

            var rows = new List<IReadOnlyList<object>>
            {
                new object[] { "Rank", "Competitor", "Score", "Time Remaining", "Completed", "Attempted", "Bonuses", "Finished" }
            };
            rows.AddRange(leaderboard.Select(item => (IReadOnlyList<object>)new object[]
            {
                item.Rank, item.CompetitorName, item.Score, item.RemainingSeconds,
                item.Completed, item.Attempted, item.Bonuses, item.FinishedAt.ToString("O")
            }));
            WriteEntry(archive, "xl/worksheets/sheet1.xml", SheetXml(rows));
        }
        return stream.ToArray();
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string SheetXml(IReadOnlyList<IReadOnlyList<object>> rows)
    {
        var builder = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
            "<cols><col min=\"1\" max=\"1\" width=\"8\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"26\" customWidth=\"1\"/><col min=\"3\" max=\"8\" width=\"16\" customWidth=\"1\"/></cols><sheetData>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append("<row r=\"").Append(rowIndex + 1).Append("\">");
            for (var columnIndex = 0; columnIndex < rows[rowIndex].Count; columnIndex++)
            {
                var address = Column(columnIndex + 1) + (rowIndex + 1);
                var value = rows[rowIndex][columnIndex];
                if (value is int or long or decimal or double)
                {
                    builder.Append("<c r=\"").Append(address).Append("\"")
                        .Append(rowIndex == 0 ? " s=\"1\"" : "")
                        .Append("><v>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                }
                else
                {
                    builder.Append("<c r=\"").Append(address).Append("\" t=\"inlineStr\"")
                        .Append(rowIndex == 0 ? " s=\"1\"" : "")
                        .Append("><is><t>")
                        .Append(XmlEscape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))
                        .Append("</t></is></c>");
                }
            }
            builder.Append("</row>");
        }
        builder.Append("</sheetData><autoFilter ref=\"A1:H")
            .Append(rows.Count)
            .Append("\"/></worksheet>");
        return builder.ToString();
    }

    private static string XmlEscape(string value)
    {
        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment });
        writer.WriteString(value);
        writer.Flush();
        return builder.ToString();
    }

    private static string Column(int number)
    {
        var result = "";
        while (number > 0)
        {
            number--;
            result = (char)('A' + number % 26) + result;
            number /= 26;
        }
        return result;
    }
}

