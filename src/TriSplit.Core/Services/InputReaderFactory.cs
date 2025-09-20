using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class InputReaderFactory : IInputReaderFactory
{
    private readonly IReadOnlyList<IInputReader> _readers;

    public InputReaderFactory(IEnumerable<IInputReader> readers)
    {
        _readers = readers?.ToArray() ?? throw new ArgumentNullException(nameof(readers));

        if (_readers.Count == 0)
        {
            throw new InvalidOperationException("At least one input reader must be registered.");
        }
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
