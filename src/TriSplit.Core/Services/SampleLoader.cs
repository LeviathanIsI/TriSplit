using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Services;

public class SampleLoader : ISampleLoader
{
    private readonly IInputReaderFactory _readerFactory;
    private readonly Dictionary<string, SampleData> _cache = new();

    public SampleLoader(IInputReaderFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public async Task<SampleData> LoadSampleAsync(string filePath)
    {
        if (_cache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        var reader = _readerFactory.GetReader(filePath);
        var data = await reader.ReadAsync(filePath);
        _cache[filePath] = data;
        return data;
    }

    public async Task<IEnumerable<string>> GetColumnHeadersAsync(string filePath)
    {
        var data = await LoadSampleWithLimitAsync(filePath, 1);
        return data.Headers;
    }

    public async Task<SampleData> LoadSampleWithLimitAsync(string filePath, int limit = 100)
    {
        var reader = _readerFactory.GetReader(filePath);
        return await reader.ReadAsync(filePath, limit);
    }
}