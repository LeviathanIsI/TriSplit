using TriSplit.Core.Interfaces;

namespace TriSplit.Desktop.Services;

public interface IDialogService
{
    Task<string?> ShowOpenFileDialogAsync(string title, string filter);
    Task<string?> ShowSaveFileDialogAsync(string title, string filter, string defaultFileName = "");
    Task<bool> ShowConfirmationDialogAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);
    Task<ProfileMatchCandidate?> ShowProfileSelectionDialogAsync(IReadOnlyList<ProfileMatchCandidate> candidates);
    Task<PartialMatchDecision> ShowPartialMatchDialogAsync(ProfileMatchCandidate candidate);
    Task<string?> ShowInputDialogAsync(string title, string prompt, string defaultValue = "");
    Task<NewSourceDecision> ShowNewSourceDecisionAsync(string sourceFileName);
}

public enum PartialMatchDecision
{
    Cancel,
    UpdateExisting,
    CreateNew
}
