using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    private const double PartialMatchThreshold = 0.65;

    public ProfileDetectionService(IProfileSignatureService signatureService, IDialogService dialogService)
    {
        _signatureService = signatureService;
        _dialogService = dialogService;
    }

    public async Task<ProfileDetectionResult> DetectProfileAsync(IReadOnlyList<string> headers, string? sourceFilePath, CancellationToken cancellationToken = default)
    {
        var matchResult = await _signatureService.FindBestMatchAsync(headers, cancellationToken).ConfigureAwait(false);

        if (matchResult.UniqueExactMatch is { } exactMatch)
        {
            return ProfileDetectionResult.Matched(exactMatch.Profile, shouldUpdateMetadata: false,
                $"Matched saved source '{exactMatch.Profile.Name}'. Profile loaded automatically.");
        }

        if (matchResult.ExactMatches.Count > 1)
        {
            var selection = await _dialogService.ShowProfileSelectionDialogAsync(matchResult.ExactMatches).ConfigureAwait(false);
            if (selection != null)
            {
                return ProfileDetectionResult.Matched(selection.Profile, shouldUpdateMetadata: false,
                    $"Matched saved source '{selection.Profile.Name}'. Profile loaded automatically.");
            }

            return ProfileDetectionResult.Cancelled("Profile detection cancelled.");
        }

        var bestCandidate = matchResult.Candidates.FirstOrDefault();
        if (bestCandidate != null && bestCandidate.Score >= PartialMatchThreshold &&
            (bestCandidate.MissingHeaders.Count > 0 || bestCandidate.AdditionalHeaders.Count > 0))
        {
            var decision = await _dialogService.ShowPartialMatchDialogAsync(bestCandidate).ConfigureAwait(false);
            switch (decision)
            {
                case PartialMatchDecision.UpdateExisting:
                    return ProfileDetectionResult.Matched(bestCandidate.Profile, shouldUpdateMetadata: true,
                        $"Header differences detected. '{bestCandidate.Profile.Name}' will be updated when you save.");
                case PartialMatchDecision.CreateNew:
                    break;
                default:
                    return ProfileDetectionResult.Cancelled("Profile detection cancelled.");
            }
        }

        var fileLabel = string.IsNullOrWhiteSpace(sourceFilePath)
            ? "New source detected. Select the columns you want to map."
            : $"New source detected from {System.IO.Path.GetFileName(sourceFilePath)}. Select the columns you want to map.";

        return ProfileDetectionResult.NewSource(fileLabel);
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
