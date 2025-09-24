using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class CsvInputReader : IInputReader
{
    public string[] SupportedExtensions => new[] { ".csv", ".tsv", ".txt" };

    public async Task<SampleData> ReadAsync(string filePath, int? limit = null)
    {
        try
        {
            return await ReadWithConfigurationAsync(filePath, limit, ignoreQuotes: false).ConfigureAwait(false);
        }
        catch (CsvHelperException ex) when (ShouldRetryWithoutQuotes(ex))
        {
            return await ReadWithConfigurationAsync(filePath, limit, ignoreQuotes: true).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetryWithoutQuotes(CsvHelperException exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is BadDataException || current is ParserException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private async Task<SampleData> ReadWithConfigurationAsync(string filePath, int? limit, bool ignoreQuotes)
    {
        var result = new SampleData
        {
            SourceFile = filePath
        };

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CreateConfiguration(ignoreQuotes));

        if (await csv.ReadAsync().ConfigureAwait(false))
        {
            csv.ReadHeader();
            if (csv.HeaderRecord is not null)
            {
                result.Headers = csv.HeaderRecord.ToList();
            }
        }

        var rowCount = 0;
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            if (limit.HasValue && rowCount >= limit.Value)
            {
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

        result.TotalRows = rowCount;
        return result;
    }

    private static CsvConfiguration CreateConfiguration(bool ignoreQuotes)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null,
            BadDataFound = null,
            DetectDelimiter = true,
            DetectDelimiterValues = new[] { ",", ";", "\t", "|" },
            IgnoreBlankLines = true,
            ReadingExceptionOccurred = _ => false
        };

        if (ignoreQuotes)
        {
            configuration.Mode = CsvMode.NoEscape;
        }

        return configuration;
    }
}



