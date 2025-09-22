using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Processors;
using TriSplit.Core.Models;

namespace TriSplit.Core.Services;

public class ExcelExporter : IExcelExporter
{
    private const int StreamingRowThreshold = 50000;
    private const int StreamingColumnThreshold = 40;

    public async Task<string> WriteContactsAsync(string outputDirectory, string fileName, IEnumerable<ContactRecord> records, CancellationToken cancellationToken)
    {
        var data = Materialize(records, out var rowCount);
        var includeLinkedContact = data.Any(r => !string.IsNullOrWhiteSpace(r.LinkedContactId));

        var headers = new List<string>
        {
            "Import ID",
            "First Name",
            "Last Name",
            "Email",
            "Company"
        };

        if (includeLinkedContact)
        {
            headers.Add("Linked Contact ID");
        }

        headers.AddRange(new[] { "Association Label", "Data Source", "Data Type", "Tags", "Is Secondary" });

        var rows = data.Select(r =>
        {
            var values = new List<string>
            {
                r.ImportId,
                r.FirstName,
                r.LastName,
                r.Email,
                r.Company
            };

            if (includeLinkedContact)
            {
                values.Add(r.LinkedContactId);
            }

            values.Add(r.AssociationLabel);
            values.Add(r.DataSource);
            values.Add(r.DataType);
            values.Add(r.Tags);
            values.Add(r.IsSecondary ? "Yes" : "No");

            return values.ToArray();
        });

        return await WriteWorksheetAsync(outputDirectory, fileName, "Contacts", headers, rows, rowCount, headers.Count, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WritePhonesAsync(string outputDirectory, string fileName, IEnumerable<PhoneRecord> records, CancellationToken cancellationToken)
    {
        var data = Materialize(records, out var rowCount);
        var headers = new[] { "Import ID", "Phone Number", "Data Source", "Is Secondary" };
        var rows = data.Select(r => new[]
        {
            r.ImportId,
            r.PhoneNumber,
            r.DataSource,
            r.IsSecondary ? "Yes" : "No"
        });

        return await WriteWorksheetAsync(outputDirectory, fileName, "Phone Numbers", headers, rows, rowCount, headers.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WritePropertiesAsync(string outputDirectory, string fileName, IEnumerable<PropertyRecord> records, IReadOnlyList<string> additionalFieldOrder, CancellationToken cancellationToken)
    {
        var data = Materialize(records, out var rowCount);
        var baseHeaders = new[] { "Import ID", "Property Address", "City", "State", "Zip", "County", "Property Type", "Property Value" };
        var headers = baseHeaders
            .Concat(additionalFieldOrder)
            .Concat(new[] { "Association Label", "Data Source", "Data Type", "Tags", "Is Secondary" })
            .ToArray();

        var rows = data.Select(record =>
        {
            var baseValues = new List<string>
            {
                record.ImportId,
                record.Address,
                record.City,
                record.State,
                record.Zip,
                record.County,
                record.PropertyType,
                record.PropertyValue
            };

            foreach (var field in additionalFieldOrder)
            {
                record.AdditionalFields.TryGetValue(field, out var value);
                baseValues.Add(value ?? string.Empty);
            }

            baseValues.Add(record.AssociationLabel);
            baseValues.Add(record.DataSource);
            baseValues.Add(record.DataType);
            baseValues.Add(record.Tags);
            baseValues.Add(record.IsSecondary ? "Yes" : "No");
            return baseValues.ToArray();
        });

        return await WriteWorksheetAsync(outputDirectory, fileName, "Properties", headers, rows, rowCount, headers.Length, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<T> Materialize<T>(IEnumerable<T> source, out int count)
    {
        if (source is IReadOnlyCollection<T> collection)
        {
            count = collection.Count;
            return collection.ToList();
        }

        var list = source.ToList();
        count = list.Count;
        return list;
    }

    private async Task<string> WriteWorksheetAsync(string outputDirectory, string fileName, string sheetName, IEnumerable<string> headers, IEnumerable<string[]> rows, int rowCount, int columnCount, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var filePath = Path.Combine(outputDirectory, fileName);

        if (ShouldUseStreaming(rowCount, columnCount))
        {
            await WriteStreamingAsync(filePath, sheetName, headers, rows, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            WriteClosedXml(filePath, sheetName, headers, rows);
        }

        return filePath;
    }

    private static bool ShouldUseStreaming(int rowCount, int columnCount)
    {
        return rowCount > StreamingRowThreshold || columnCount > StreamingColumnThreshold;
    }

    private static void WriteClosedXml(string filePath, string sheetName, IEnumerable<string> headers, IEnumerable<string[]> rows)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(sheetName);

        var headerArray = headers.ToArray();
        for (int col = 0; col < headerArray.Length; col++)
        {
            worksheet.Cell(1, col + 1).Value = headerArray[col];
            worksheet.Cell(1, col + 1).Style.Font.Bold = true;
        }

        var rowIndex = 2;
        foreach (var row in rows)
        {
            for (int col = 0; col < row.Length; col++)
            {
                worksheet.Cell(rowIndex, col + 1).Value = row[col] ?? string.Empty;
            }
            rowIndex++;
        }

        worksheet.Columns().AdjustToContents(1, Math.Min(headerArray.Length, 50));
        workbook.SaveAs(filePath);
    }

    private static async Task WriteStreamingAsync(string filePath, string sheetName, IEnumerable<string> headers, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());

            WriteRow(writer, headers);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteRow(writer, row);
            }

            writer.WriteEndElement(); // SheetData
            writer.WriteEndElement(); // Worksheet
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var sheet = new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = sheetName
        };
        sheets.Append(sheet);

        workbookPart.Workbook.Save();
        await Task.CompletedTask;
    }

    private static void WriteRow(OpenXmlWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartElement(new Row());
        foreach (var value in values)
        {
            var cell = new Cell
            {
                DataType = CellValues.String,
                CellValue = new CellValue(value ?? string.Empty)
            };
            writer.WriteElement(cell);
        }
        writer.WriteEndElement();
    }
}
