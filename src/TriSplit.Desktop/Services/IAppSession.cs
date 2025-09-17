using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Desktop.Services;

public interface IAppSession
{
    Profile? SelectedProfile { get; set; }
    SampleData? CurrentSampleData { get; set; }
    string? LoadedFilePath { get; set; }
    event EventHandler? SessionUpdated;
}