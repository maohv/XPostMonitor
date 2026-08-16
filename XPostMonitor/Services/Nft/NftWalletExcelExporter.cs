using System.IO.Compression;
using System.Text;
using System.Xml;

namespace XPostMonitor.Services.Nft;

// Tạo file Excel thật chỉ bằng thư viện chuẩn .NET, không cần lưu file xuống server.
public static class NftWalletExcelExporter
{
    public static byte[] Create(IReadOnlyList<NftWalletExportRow> rows)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
        {
            WriteText(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "</Types>");
            WriteText(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>");
            WriteText(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Mint Wallets\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteText(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "</Relationships>");
            WriteSheet(archive, rows);
        }
        return output.ToArray();
    }

    private static void WriteSheet(ZipArchive archive, IReadOnlyList<NftWalletExportRow> rows)
    {
        ZipArchiveEntry entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
        using Stream stream = entry.Open();
        using XmlWriter xml = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        xml.WriteStartElement("cols");
        WriteColumn(xml, 1, 12); WriteColumn(xml, 2, 10);
        WriteColumn(xml, 3, 48); WriteColumn(xml, 4, 72);
        xml.WriteEndElement();
        xml.WriteStartElement("sheetData");
        WriteRow(xml, 1, "Group", "Wallet", "Address", "PrivateKey");
        for (int index = 0; index < rows.Count; index++)
        {
            NftWalletExportRow row = rows[index];
            WriteRow(xml, index + 2, row.Group, row.WalletNumber.ToString(), row.Address, row.PrivateKey);
        }
        xml.WriteEndElement();
        xml.WriteStartElement("autoFilter");
        xml.WriteAttributeString("ref", $"A1:D{rows.Count + 1}");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteColumn(XmlWriter xml, int number, int width)
    {
        xml.WriteStartElement("col");
        xml.WriteAttributeString("min", number.ToString());
        xml.WriteAttributeString("max", number.ToString());
        xml.WriteAttributeString("width", width.ToString());
        xml.WriteAttributeString("customWidth", "1");
        xml.WriteEndElement();
    }

    private static void WriteRow(XmlWriter xml, int rowNumber, params string[] values)
    {
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", rowNumber.ToString());
        for (int index = 0; index < values.Length; index++)
        {
            xml.WriteStartElement("c");
            xml.WriteAttributeString("r", $"{(char)('A' + index)}{rowNumber}");
            xml.WriteAttributeString("t", "inlineStr");
            xml.WriteStartElement("is");
            xml.WriteElementString("t", values[index]);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using Stream stream = archive.CreateEntry(path).Open();
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed record NftWalletExportRow(string Group, int WalletNumber, string Address,
    string PrivateKey);
