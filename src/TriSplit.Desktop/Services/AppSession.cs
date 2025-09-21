using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Desktop.Services;

public class AppSession : IAppSession
{
    private readonly string _sessionFilePath;
    private bool _isLoadingSnapshot;

    private Profile? _selectedProfile;
    private SampleData? _currentSampleData;
    private string? _loadedFilePath;
    private bool _outputCsv = true;
    private bool _outputExcel;
    private bool _outputJson;
    private Guid? _lastProfileId;
    private string? _activeNewSourceKey;
    private bool _isNewSourcePromptActive;

    public AppSession()
    {
        _sessionFilePath = Path.Combine(ApplicationPaths.AppDataPath, "session-state.json");
        Directory.CreateDirectory(ApplicationPaths.AppDataPath);
        LoadSnapshot();
    }

    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value)
            {
                return;
            }

            _selectedProfile = value;
            _lastProfileId = value?.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastProfileId));
            PersistSnapshot();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public SampleData? CurrentSampleData
    {
        get => _currentSampleData;
        set
        {
            if (_currentSampleData == value)
            {
                return;
            }

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
            if (_loadedFilePath == value)
            {
                return;
            }

            _loadedFilePath = value;
            OnPropertyChanged();
            PersistSnapshot();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool OutputCsv
    {
        get => _outputCsv;
        set
        {
            if (_outputCsv == value)
            {
                return;
            }

            _outputCsv = value;
            OnPropertyChanged();
            PersistSnapshot();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool OutputExcel
    {
        get => _outputExcel;
        set
        {
            if (_outputExcel == value)
            {
                return;
            }

            _outputExcel = value;
            OnPropertyChanged();
            PersistSnapshot();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool OutputJson
    {
        get => _outputJson;
        set
        {
            if (_outputJson == value)
            {
                return;
            }

            _outputJson = value;
            OnPropertyChanged();
            PersistSnapshot();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public Guid? LastProfileId => _lastProfileId;

    public event EventHandler? SessionUpdated;
    public event Action<AppTab>? NavigationRequested;
    public event EventHandler<NewSourceRequestedEventArgs>? NewSourceRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool TryEnterNewSourcePrompt(string filePath)
    {
        var key = NormalizeNewSourceKey(filePath);
        if (_isNewSourcePromptActive && string.Equals(_activeNewSourceKey, key, StringComparison.Ordinal))
        {
            return false;
        }

        _isNewSourcePromptActive = true;
        _activeNewSourceKey = key;
        return true;
    }

    public void CompleteNewSourcePrompt()
    {
        _isNewSourcePromptActive = false;
        _activeNewSourceKey = null;
    }

    public void RequestNavigation(AppTab tab)
    {
        NavigationRequested?.Invoke(tab);
    }

    public void NotifyNewSourceRequested(string filePath, IReadOnlyList<string> headers)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var headerSnapshot = headers ?? Array.Empty<string>();
        LoadedFilePath = filePath;
        NewSourceRequested?.Invoke(this, new NewSourceRequestedEventArgs(filePath, headerSnapshot));
    }

    private void LoadSnapshot()
    {
        if (!File.Exists(_sessionFilePath))
        {
            return;
        }

        try
        {
            _isLoadingSnapshot = true;
            var json = File.ReadAllText(_sessionFilePath);
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(json);
            if (snapshot == null)
            {
                return;
            }

            _loadedFilePath = snapshot.LoadedFilePath;
            _outputCsv = snapshot.OutputCsv;
            _outputExcel = snapshot.OutputExcel;
            _outputJson = snapshot.OutputJson;
            _lastProfileId = snapshot.SelectedProfileId;

            OnPropertyChanged(nameof(LoadedFilePath));
            OnPropertyChanged(nameof(OutputCsv));
            OnPropertyChanged(nameof(OutputExcel));
            OnPropertyChanged(nameof(OutputJson));
            OnPropertyChanged(nameof(LastProfileId));
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Ignore corrupt snapshots
        }
        finally
        {
            _isLoadingSnapshot = false;
        }
    }

    private static string NormalizeNewSourceKey(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().ToLowerInvariant();
    }


    private void PersistSnapshot()
    {
        if (_isLoadingSnapshot)
        {
            return;
        }

        try
        {
            var snapshot = new SessionSnapshot
            {
                SelectedProfileId = _lastProfileId,
                LoadedFilePath = _loadedFilePath,
                OutputCsv = _outputCsv,
                OutputExcel = _outputExcel,
                OutputJson = _outputJson
            };

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_sessionFilePath, json);
        }
        catch
        {
            // Ignore persistence failures
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class SessionSnapshot
    {
        public Guid? SelectedProfileId { get; set; }
        public string? LoadedFilePath { get; set; }
        public bool OutputCsv { get; set; } = true;
        public bool OutputExcel { get; set; }
        public bool OutputJson { get; set; }
    }
}
