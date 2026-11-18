using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Security;

namespace TabularEditor.UI
{
    internal static class DaxQueryWorkbook
    {
        private const string Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string Package = "http://schemas.openxmlformats.org/package/2006/";

        internal static string ColumnName(string name)
        {
            if (!name.EndsWith("]", StringComparison.Ordinal)) return name;
            var start = name.IndexOf('[');
            return start < 0 ? name : name.Substring(start + 1, name.Length - start - 2).Replace("]]", "]");
        }

        public static void Save(string path, DataColumnCollection columns, DataRow[] rows, string executedQuery)
        {
            if (columns.Count > 16384 || rows.Length > 1048575)
                throw new InvalidOperationException("The result exceeds Excel's worksheet limits. Filter the result or return fewer columns before exporting.");

            var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Guid.NewGuid() + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                using (var workbook = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WritePart(workbook, "[Content_Types].xml",
                        $"<Types xmlns='{Package}content-types'>" +
                        "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>" +
                        "<Default Extension='xml' ContentType='application/xml'/>" +
                        "<Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/>" +
                        "<Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/>" +
                        "<Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/>" +
                        "<Override PartName='/xl/styles.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml'/>" +
                        "</Types>");
                    WritePart(workbook, "_rels/.rels",
                        $"<Relationships xmlns='{Package}relationships'><Relationship Id='rId1' Type='{Relationships}/officeDocument' Target='xl/workbook.xml'/></Relationships>");
                    WritePart(workbook, "xl/workbook.xml",
                        $"<workbook xmlns='{Spreadsheet}' xmlns:r='{Relationships}'><sheets><sheet name='DAX Results' sheetId='1' r:id='rId1'/><sheet name='DAX Script' sheetId='2' r:id='rId3'/></sheets></workbook>");
                    WritePart(workbook, "xl/_rels/workbook.xml.rels",
                        $"<Relationships xmlns='{Package}relationships'>" +
                        $"<Relationship Id='rId1' Type='{Relationships}/worksheet' Target='worksheets/sheet1.xml'/>" +
                        $"<Relationship Id='rId3' Type='{Relationships}/worksheet' Target='worksheets/sheet2.xml'/>" +
                        $"<Relationship Id='rId2' Type='{Relationships}/styles' Target='styles.xml'/></Relationships>");
                    WritePart(workbook, "xl/styles.xml",
                        $"<styleSheet xmlns='{Spreadsheet}'>" +
                        $"<numFmts count='{columns.Count + 1}'><numFmt numFmtId='164' formatCode='yyyy-mm-dd hh:mm:ss'/>" +
                        ColumnFormats(columns) + "</numFmts>" +
                        "<fonts count='1'><font><sz val='11'/><name val='Calibri'/></font></fonts>" +
                        "<fills count='2'><fill><patternFill patternType='none'/></fill><fill><patternFill patternType='gray125'/></fill></fills>" +
                        "<borders count='1'><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                        "<cellStyleXfs count='1'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/></cellStyleXfs>" +
                        $"<cellXfs count='{columns.Count + 2}'><xf numFmtId='0' fontId='0' fillId='0' borderId='0' xfId='0'/>" +
                        "<xf numFmtId='164' fontId='0' fillId='0' borderId='0' xfId='0' applyNumberFormat='1'/>" +
                        ColumnStyles(columns) + "</cellXfs>" +
                        "<cellStyles count='1'><cellStyle name='Normal' xfId='0' builtinId='0'/></cellStyles></styleSheet>");
                    using (var sheet = workbook.CreateEntry("xl/worksheets/sheet1.xml").Open())
                    using (var xml = XmlWriter.Create(sheet))
                    {
                        xml.WriteStartElement("worksheet", Spreadsheet);
                        xml.WriteStartElement("sheetData", Spreadsheet);
                        xml.WriteStartElement("row", Spreadsheet);
                        foreach (DataColumn column in columns) WriteCell(xml, ColumnName(column.ColumnName));
                        xml.WriteEndElement();
                        foreach (var row in rows)
                        {
                            xml.WriteStartElement("row", Spreadsheet);
                            for (var column = 0; column < columns.Count; column++)
                                WriteCell(xml, row[column], string.IsNullOrEmpty(columns[column].ExtendedProperties["DaxFormat"] as string) ? 0 : column + 2);
                            xml.WriteEndElement();
                        }
                        xml.WriteEndElement();
                        xml.WriteEndElement();
                    }
                    using (var sheet = workbook.CreateEntry("xl/worksheets/sheet2.xml").Open())
                    using (var xml = XmlWriter.Create(sheet))
                    {
                        xml.WriteStartElement("worksheet", Spreadsheet);
                        xml.WriteStartElement("sheetData", Spreadsheet);
                        foreach (var line in executedQuery.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                        {
                            xml.WriteStartElement("row", Spreadsheet);
                            // Split unusually long lines across cells to preserve the full script.
                            for (var offset = 0; offset < Math.Max(1, line.Length); offset += 32767)
                                WriteCell(xml, line.Substring(offset, Math.Min(32767, line.Length - offset)));
                            xml.WriteEndElement();
                        }
                        xml.WriteEndElement();
                        xml.WriteEndElement();
                    }
                }

                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void WritePart(ZipArchive workbook, string name, string xml)
        {
            using (var writer = new StreamWriter(workbook.CreateEntry(name).Open(), new UTF8Encoding(false)))
                writer.Write(xml);
        }

        private static string ColumnFormats(DataColumnCollection columns)
        {
            var formats = new StringBuilder();
            foreach (DataColumn column in columns)
                formats.Append($"<numFmt numFmtId='{165 + column.Ordinal}' formatCode='{SecurityElement.Escape(column.ExtendedProperties["DaxFormat"] as string ?? "General")}'/>");
            return formats.ToString();
        }

        private static string ColumnStyles(DataColumnCollection columns)
        {
            var styles = new StringBuilder();
            foreach (DataColumn column in columns)
                styles.Append($"<xf numFmtId='{165 + column.Ordinal}' fontId='0' fillId='0' borderId='0' xfId='0' applyNumberFormat='1'/>");
            return styles.ToString();
        }

        private static void WriteCell(XmlWriter xml, object value, int style = 0)
        {
            xml.WriteStartElement("c", Spreadsheet);
            if (style > 0 && !(value is DateTime)) xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            switch (value)
            {
                case null:
                case DBNull _:
                    break;
                case bool flag:
                    xml.WriteAttributeString("t", "b");
                    xml.WriteElementString("v", Spreadsheet, flag ? "1" : "0");
                    break;
                case DateTime date when date >= new DateTime(1900, 1, 1):
                    var serial = date.ToOADate();
                    if (date < new DateTime(1900, 3, 1)) serial--;
                    xml.WriteAttributeString("s", (style > 0 ? style : 1).ToString(CultureInfo.InvariantCulture));
                    xml.WriteElementString("v", Spreadsheet, serial.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case long integer when integer < -999999999999999L || integer > 999999999999999L:
                    WriteText(xml, integer.ToString(CultureInfo.InvariantCulture));
                    break;
                case double number when double.IsNaN(number) || double.IsInfinity(number):
                    WriteText(xml, number.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    var culture = CultureInfo.InvariantCulture;
                    if (value is int || value is long || value is short || value is byte ||
                        value is decimal || value is double || value is float)
                        xml.WriteElementString("v", Spreadsheet, Convert.ToString(value, culture));
                    else
                        WriteText(xml, value is DateTime oldDate ? oldDate.ToString("O", culture) : Convert.ToString(value, culture));
                    break;
            }
            xml.WriteEndElement();
        }

        private static void WriteText(XmlWriter xml, string text)
        {
            if (text.Length > 32767)
                throw new InvalidOperationException("A result cell exceeds Excel's limit of 32,767 characters.");

            // SpreadsheetML escapes control characters and literal occurrences of its escape prefix.
            var escaped = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (i + 1 < text.Length && char.IsSurrogatePair(ch, text[i + 1]))
                {
                    escaped.Append(ch).Append(text[++i]);
                }
                else if (!XmlConvert.IsXmlChar(ch) || ch == '\r' ||
                    ch == '_' && i + 6 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X') &&
                    text[i + 6] == '_' && ushort.TryParse(text.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                {
                    escaped.Append("_x").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
                }
                else escaped.Append(ch);
            }
            xml.WriteAttributeString("t", "inlineStr");
            xml.WriteStartElement("is", Spreadsheet);
            xml.WriteStartElement("t", Spreadsheet);
            xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(escaped.ToString());
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
    }
}
