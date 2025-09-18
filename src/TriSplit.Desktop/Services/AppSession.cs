using System.ComponentModel;
using System.Runtime.CompilerServices;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Desktop.Services;

public class AppSession : IAppSession
{
    private Profile? _selectedProfile;
    private SampleData? _currentSampleData;
    private string? _loadedFilePath;

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            _selectedProfile = value;
            OnPropertyChanged();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public SampleData? CurrentSampleData
    {
        get => _currentSampleData;
        set
        {
            _currentSampleData = value;
            OnPropertyChanged();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? LoadedFilePath
    {
        get => _loadedFilePath;
        set
        {
            _loadedFilePath = value;
            OnPropertyChanged();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SessionUpdated;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}