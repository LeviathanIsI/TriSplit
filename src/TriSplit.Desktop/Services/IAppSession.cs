using System;
using System.ComponentModel;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Desktop.Services;

public interface IAppSession : INotifyPropertyChanged
{
    Profile? SelectedProfile { get; set; }
    SampleData? CurrentSampleData { get; set; }
    string? LoadedFilePath { get; set; }
    bool OutputCsv { get; set; }
    bool OutputExcel { get; set; }
    bool OutputJson { get; set; }
    Guid? LastProfileId { get; }
    event EventHandler? SessionUpdated;
}
