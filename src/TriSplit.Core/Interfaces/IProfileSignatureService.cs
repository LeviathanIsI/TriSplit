using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TriSplit.Core.Models;

namespace TriSplit.Core.Interfaces;

public interface IProfileSignatureService
{
    Task<ProfileSignatureMatchResult> FindBestMatchAsync(IEnumerable<string> headers, CancellationToken cancellationToken = default);
    IReadOnlyList<string> NormalizeHeaders(IEnumerable<string> headers);
}

public class ProfileMatchCandidate
{
    public ProfileMatchCandidate(Profile profile, double score, IReadOnlyList<string> missingHeaders, IReadOnlyList<string> additionalHeaders)
    {
        Profile = profile;
        Score = score;
        MissingHeaders = missingHeaders;
        AdditionalHeaders = additionalHeaders;
    }

    public Profile Profile { get; }
    public double Score { get; }
    public IReadOnlyList<string> MissingHeaders { get; }
    public IReadOnlyList<string> AdditionalHeaders { get; }
    public bool IsExact => MissingHeaders.Count == 0 && AdditionalHeaders.Count == 0;
}

public class ProfileSignatureMatchResult
{
    public ProfileSignatureMatchResult(IEnumerable<ProfileMatchCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Candidates = ordered;
        ExactMatches = ordered.Where(c => c.IsExact).ToList();
    }

    public IReadOnlyList<ProfileMatchCandidate> Candidates { get; }
    public IReadOnlyList<ProfileMatchCandidate> ExactMatches { get; }
    public ProfileMatchCandidate? UniqueExactMatch => ExactMatches.Count == 1 ? ExactMatches[0] : null;
}
