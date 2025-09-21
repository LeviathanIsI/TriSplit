using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Core.Services;

public class ProfileSignatureService : IProfileSignatureService
{
    private readonly IProfileStore _profileStore;
    private readonly IProfileMetadataRepository _metadataRepository;

    public ProfileSignatureService(IProfileStore profileStore, IProfileMetadataRepository metadataRepository)
    {
        _profileStore = profileStore;
        _metadataRepository = metadataRepository;
    }

    public async Task<ProfileSignatureMatchResult> FindBestMatchAsync(IEnumerable<string> headers, CancellationToken cancellationToken = default)
    {
        if (headers == null)
        {
            return new ProfileSignatureMatchResult(Array.Empty<ProfileMatchCandidate>());
        }

        var incomingMap = NormalizeToDictionary(headers);
        if (incomingMap.Count == 0)
        {
            return new ProfileSignatureMatchResult(Array.Empty<ProfileMatchCandidate>());
        }

        var incomingSet = new HashSet<string>(incomingMap.Keys, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ProfileMatchCandidate>();

        var profiles = await _profileStore.GetAllProfilesAsync().ConfigureAwait(false);
        var metadataItems = await _metadataRepository.GetAllMetadataAsync(cancellationToken).ConfigureAwait(false);
        var metadataLookup = metadataItems.ToDictionary(m => m.ProfileId);

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!metadataLookup.TryGetValue(profile.Id, out var metadata) || metadata.Headers.Count == 0)
            {
                continue;
            }

            var storedMap = NormalizeToDictionary(metadata.Headers);
            if (storedMap.Count == 0)
            {
                continue;
            }

            var storedSet = new HashSet<string>(storedMap.Keys, StringComparer.OrdinalIgnoreCase);

            var intersection = new HashSet<string>(storedSet, StringComparer.OrdinalIgnoreCase);
            intersection.IntersectWith(incomingSet);
            var intersectionCount = intersection.Count;

            var union = new HashSet<string>(storedSet, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(incomingSet);
            var unionCount = union.Count;

            var missingSet = new HashSet<string>(storedSet, StringComparer.OrdinalIgnoreCase);
            missingSet.ExceptWith(incomingSet);

            var additionalSet = new HashSet<string>(incomingSet, StringComparer.OrdinalIgnoreCase);
            additionalSet.ExceptWith(storedSet);

            var missing = missingSet
                .Select(normalized => storedMap.TryGetValue(normalized, out var original) ? original : normalized)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var additional = additionalSet
                .Select(normalized => incomingMap.TryGetValue(normalized, out var original) ? original : normalized)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var score = unionCount == 0 ? 0 : (double)intersectionCount / unionCount;

            if (!string.IsNullOrWhiteSpace(metadata.FilePath))
            {
                var fileName = Path.GetFileName(metadata.FilePath);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    profile.MetadataFileName = fileName;
                }
            }

            profile.SourceHeaders = metadata.Headers;

            candidates.Add(new ProfileMatchCandidate(profile, score, missing, additional));
        }

        return new ProfileSignatureMatchResult(candidates);
    }

    public IReadOnlyList<string> NormalizeHeaders(IEnumerable<string> headers)
    {
        if (headers == null)
        {
            return Array.Empty<string>();
        }

        return headers
            .Select(NormalizeHeader)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string> NormalizeToDictionary(IEnumerable<string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var normalized = NormalizeHeader(header);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (!result.ContainsKey(normalized))
            {
                result[normalized] = header ?? string.Empty;
            }
        }

        return result;
    }

    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var collapsed = Regex.Replace(header.Trim(), "\\s+", " ");
        return collapsed.ToUpperInvariant();
    }
}
