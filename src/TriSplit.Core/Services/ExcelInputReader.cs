using System.Text;
using TriSplit.Core.Interfaces;
using ExcelDataReader;

namespace TriSplit.Core.Services;

public class ExcelInputReader : IInputReader
{
    public string[] SupportedExtensions => new[] { ".xlsx", ".xls" };

    public async Task<SampleData> ReadAsync(string filePath, int? limit = null)
    {
        return await Task.Run(() =>
        {
            // Encoding provider registered at app startup

            using var fs = File.OpenRead(filePath);
            using var reader = ExcelReaderFactory.CreateReader(fs); // streaming, forward-only

            var result = new SampleData { SourceFile = filePath };
            var max = Math.Max(1, limit ?? 100);

            // Read first sheet only (preview semantics)
            if (!reader.Read()) return result; // first row (header?) or empty

            // Build headers from first non-empty row
            var headers = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = (reader.GetValue(i)?.ToString() ?? "").Trim();
                headers.Add(string.IsNullOrWhiteSpace(name) ? $"Column {i + 1}" : name);
            }
            result.Headers = headers;

            int rowsRead = 0;
            while (rowsRead < max && reader.Read())
            {
                var rowDict = new Dictionary<string, object>(headers.Count);
                for (int i = 0; i < headers.Count; i++)
                    rowDict[headers[i]] = reader.GetValue(i)?.ToString() ?? string.Empty;

                result.Rows.Add(rowDict);
                rowsRead++;
            }

            // We don't know total rows without a full scan; set to preview count.
            result.TotalRows = rowsRead;
            return result;
        });
    }
}