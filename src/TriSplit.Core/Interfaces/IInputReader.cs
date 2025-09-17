namespace TriSplit.Core.Interfaces;

public interface IInputReader
{
    Task<SampleData> ReadAsync(string filePath, int? limit = null);
    string[] SupportedExtensions { get; }
}

public interface IInputReaderFactory
{
    IInputReader GetReader(string filePath);
    bool IsSupported(string filePath);
}