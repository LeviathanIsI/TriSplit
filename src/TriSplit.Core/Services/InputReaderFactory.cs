using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class InputReaderFactory : IInputReaderFactory
{
    private readonly IEnumerable<IInputReader> _readers;

    public InputReaderFactory()
    {
        _readers = new List<IInputReader>
        {
            new CsvInputReader(),
            new ExcelInputReader()
        };
    }

    public IInputReader GetReader(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var reader = _readers.FirstOrDefault(r => r.SupportedExtensions.Contains(extension));

        if (reader == null)
        {
            throw new NotSupportedException($"File type '{extension}' is not supported");
        }

        return reader;
    }

    public bool IsSupported(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return _readers.Any(r => r.SupportedExtensions.Contains(extension));
    }
}