namespace TriSplit.Core.Interfaces;

public interface ISampleLoader
{
    Task<SampleData> LoadSampleAsync(string filePath);
    Task<IEnumerable<string>> GetColumnHeadersAsync(string filePath);
    Task<SampleData> LoadSampleWithLimitAsync(string filePath, int limit = 100);
}

public class SampleData
{
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, object>> Rows { get; set; } = new();
    public int TotalRows { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}