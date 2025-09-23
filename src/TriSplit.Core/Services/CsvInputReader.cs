using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class CsvInputReader : IInputReader
{
    public string[] SupportedExtensions => new[] { ".csv", ".tsv", ".txt" };

    public async Task<SampleData> ReadAsync(string filePath, int? limit = null)
    {
        var result = new SampleData
        {
            SourceFile = filePath
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null,
            BadDataFound = null,
            DetectDelimiter = true,
            DetectDelimiterValues = new[] { ",", ";", "\t", "|" },
            IgnoreBlankLines = true,
            ReadingExceptionOccurred = _ => false
        });

        await csv.ReadAsync();
        csv.ReadHeader();

        if (csv.HeaderRecord != null)
        {
            result.Headers = csv.HeaderRecord.ToList();
        }

        int rowCount = 0;
        while (await csv.ReadAsync())
        {
            if (limit.HasValue && rowCount >= limit.Value)
            {
                // STOP reading the file - just use what we have
                break;
            }

            var row = new Dictionary<string, object>();
            foreach (var header in result.Headers)
            {
                try
                {
                    row[header] = csv.GetField(header) ?? string.Empty;
                }
                catch
                {
                    row[header] = string.Empty;
                }
            }
            result.Rows.Add(row);
            rowCount++;
        }

        // For preview, we only know the rows we've read
        result.TotalRows = rowCount;
        return result;
    }
}
