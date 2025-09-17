using ClosedXML.Excel;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class ExcelInputReader : IInputReader
{
    public string[] SupportedExtensions => new[] { ".xlsx", ".xls", ".xlsm" };

    public async Task<SampleData> ReadAsync(string filePath, int? limit = null)
    {
        return await Task.Run(() =>
        {
            var result = new SampleData
            {
                SourceFile = filePath
            };

            using var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheet(1);
            var rows = worksheet.RowsUsed();

            if (!rows.Any())
                return result;

            // Get headers from first row
            var headerRow = rows.First();
            foreach (var cell in headerRow.CellsUsed())
            {
                result.Headers.Add(cell.Value.ToString());
            }

            // Read data rows
            int rowCount = 0;
            foreach (var row in rows.Skip(1))
            {
                if (limit.HasValue && result.Rows.Count >= limit.Value)
                {
                    // Continue counting total rows
                    rowCount = rows.Count() - 1;
                    break;
                }

                var dataRow = new Dictionary<string, object>();
                for (int i = 0; i < result.Headers.Count; i++)
                {
                    var cell = row.Cell(i + 1);
                    dataRow[result.Headers[i]] = cell.Value.ToString() ?? string.Empty;
                }
                result.Rows.Add(dataRow);
                rowCount++;
            }

            result.TotalRows = rowCount;
            return result;
        });
    }
}