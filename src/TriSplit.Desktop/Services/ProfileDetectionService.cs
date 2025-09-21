using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Desktop.Services;

public interface IProfileDetectionService
{
    Task<ProfileDetectionResult> DetectProfileAsync(IReadOnlyList<string> headers, string? sourceFilePath, CancellationToken cancellationToken = default);
}

public class ProfileDetectionService : IProfileDetectionService
{
    private readonly IProfileSignatureService _signatureService;
    private readonly IDialogService _dialogService;

    private string? _lastDetectionSignature;
    private ProfileDetectionResult? _lastDetectionResult;
    private string? _inFlightDetectionSignature;
    private Task<ProfileDetectionResult>? _inFlightDetectionTask;


    private const double PartialMatchThreshold = 0.65;

    public ProfileDetectionService(IProfileSignatureService signatureService, IDialogService dialogService)
    {
        _signatureService = signatureService;
        _dialogService = dialogService;
    }

    public Task<ProfileDetectionResult> DetectProfileAsync(IReadOnlyList<string> headers, string? sourceFilePath, CancellationToken cancellationToken = default)
    {
        var signature = BuildDetectionSignature(headers, sourceFilePath);

        if (_lastDetectionSignature != null && _lastDetectionResult != null &&
            string.Equals(_lastDetectionSignature, signature, StringComparison.Ordinal))
        {
            return Task.FromResult(_lastDetectionResult);
        }

        if (_inFlightDetectionTask != null &&
            string.Equals(_inFlightDetectionSignature, signature, StringComparison.Ordinal))
        {
            return _inFlightDetectionTask;
        }

        return StartDetectionAsync(signature, headers, sourceFilePath, cancellationToken);
    }

    private Task<ProfileDetectionResult> StartDetectionAsync(string signature, IReadOnlyList<string> headers, string? sourceFilePath, CancellationToken cancellationToken)
    {
        async Task<ProfileDetectionResult> ExecuteAsync()
        {
            try
            {
                var matchResult = await _signatureService.FindBestMatchAsync(headers, cancellationToken)
                    .ConfigureAwait(false);

                if (matchResult.UniqueExactMatch is { } exactMatch)
                {
                    var shouldSwitch = await _dialogService.ShowConfirmationDialogAsync(
                        "Profile Match Found",
                        $"The uploaded file matches the saved profile '{exactMatch.Profile.Name}'.\n\nWould you like to switch to that profile? Choose No to create a new profile instead.")
                        .ConfigureAwait(false);

                    if (shouldSwitch)
                    {
                        return CacheResult(signature, ProfileDetectionResult.Matched(exactMatch.Profile, shouldUpdateMetadata: false,
                            $"Matched saved source '{exactMatch.Profile.Name}'. Profile loaded automatically."));
                    }

                    return CacheResult(signature, ProfileDetectionResult.NewSource(BuildNewSourceMessage(sourceFilePath)));
                }

                if (matchResult.ExactMatches.Count > 1)
                {
                    var selection = await _dialogService.ShowProfileSelectionDialogAsync(matchResult.ExactMatches)
                        .ConfigureAwait(false);
                    if (selection != null)
                    {
                        return CacheResult(signature, ProfileDetectionResult.Matched(selection.Profile, shouldUpdateMetadata: false,
                            $"Matched saved source '{selection.Profile.Name}'. Profile loaded automatically."));
                    }

                    return CacheResult(signature, ProfileDetectionResult.Cancelled("Profile detection cancelled."));
                }

                var bestCandidate = matchResult.Candidates.FirstOrDefault();
                if (bestCandidate != null && bestCandidate.Score >= PartialMatchThreshold &&
                    (bestCandidate.MissingHeaders.Count > 0 || bestCandidate.AdditionalHeaders.Count > 0))
                {
                    var decision = await _dialogService.ShowPartialMatchDialogAsync(bestCandidate)
                        .ConfigureAwait(false);
                    switch (decision)
                    {
                        case PartialMatchDecision.UpdateExisting:
                            return CacheResult(signature, ProfileDetectionResult.Matched(bestCandidate.Profile, shouldUpdateMetadata: true,
                                $"Header differences detected. '{bestCandidate.Profile.Name}' will be updated when you save."));
                        case PartialMatchDecision.CreateNew:
                            break;
                        default:
                            return CacheResult(signature, ProfileDetectionResult.Cancelled("Profile detection cancelled."));
                    }
                }

                return CacheResult(signature, ProfileDetectionResult.NewSource(BuildNewSourceMessage(sourceFilePath)));
            }
            finally
            {
                _inFlightDetectionSignature = null;
                _inFlightDetectionTask = null;
            }
        }

        var task = ExecuteAsync();
        _inFlightDetectionSignature = signature;
        _inFlightDetectionTask = task;
        return task;
    }

    private ProfileDetectionResult CacheResult(string signature, ProfileDetectionResult result)
    {
        _lastDetectionSignature = signature;
        _lastDetectionResult = result;
        return result;
    }

    private static string BuildDetectionSignature(IReadOnlyList<string> headers, string? sourceFilePath)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(sourceFilePath))
        {
            builder.Append(sourceFilePath.Trim().ToLowerInvariant());
        }

        builder.Append('|');

        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                builder.Append(header.Trim().ToLowerInvariant());
                builder.Append('|');
            }
        }

        return builder.ToString();
    }

    private static string BuildNewSourceMessage(string? sourceFilePath)
    {
        return string.IsNullOrWhiteSpace(sourceFilePath)
            ? "New source detected. Select the columns you want to map."
            : $"New source detected from {System.IO.Path.GetFileName(sourceFilePath)}. Select the columns you want to map.";
    }

}

public enum ProfileDetectionOutcome
{
    Matched,
    NewSource,
    Cancelled
}

public sealed class ProfileDetectionResult
{
    private ProfileDetectionResult(ProfileDetectionOutcome outcome, Profile? profile, bool shouldUpdateMetadata, string statusMessage)
    {
        Outcome = outcome;
        Profile = profile;
        ShouldUpdateMetadata = shouldUpdateMetadata;
        StatusMessage = statusMessage;
    }

    public ProfileDetectionOutcome Outcome { get; }
    public Profile? Profile { get; }
    public bool ShouldUpdateMetadata { get; }
    public string StatusMessage { get; }

    public static ProfileDetectionResult Matched(Profile profile, bool shouldUpdateMetadata, string statusMessage)
        => new(ProfileDetectionOutcome.Matched, profile, shouldUpdateMetadata, statusMessage);

    public static ProfileDetectionResult NewSource(string statusMessage)
        => new(ProfileDetectionOutcome.NewSource, null, false, statusMessage);

    public static ProfileDetectionResult Cancelled(string statusMessage)
        => new(ProfileDetectionOutcome.Cancelled, null, false, statusMessage);
}
