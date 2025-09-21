using System;
using System.ComponentModel;
using System.Collections.Generic;
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
    event Action<AppTab>? NavigationRequested;
    void RequestNavigation(AppTab tab);
    event EventHandler<NewSourceRequestedEventArgs>? NewSourceRequested;
    void NotifyNewSourceRequested(string filePath, IReadOnlyList<string> headers);
}
