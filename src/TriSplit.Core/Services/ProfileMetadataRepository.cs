using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Core.Services;

public class ProfileMetadataRepository : IProfileMetadataRepository
{
    private readonly string _metadataDirectory;

    public ProfileMetadataRepository(string? metadataDirectory = null)
    {
        _metadataDirectory = metadataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TriSplit",
            "Profiles",
            "ProfileMetadata");

        Directory.CreateDirectory(_metadataDirectory);
    }

    public async Task<ProfileMetadata?> GetMetadataAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var filePath = ResolveExistingFile(profile);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var document = JsonConvert.DeserializeObject<ProfileMetadataDocument>(json);
        if (document == null || document.Headers == null)
        {
            return null;
        }

        return new ProfileMetadata
        {
            ProfileId = document.ProfileId,
            ProfileName = document.ProfileName ?? string.Empty,
            Headers = document.Headers,
            UpdatedAt = document.UpdatedAt,
            FilePath = filePath
        };
    }

    public async Task SaveMetadataAsync(Profile profile, IEnumerable<string> headers, CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (headers == null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        var headerList = headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var metadata = new ProfileMetadataDocument
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Headers = headerList,
            UpdatedAt = DateTime.UtcNow
        };

        var filePath = GetMetadataFilePath(profile);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonConvert.SerializeObject(metadata, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);

        profile.MetadataFileName = Path.GetFileName(filePath);
    }

    public async Task<IReadOnlyList<ProfileMetadata>> GetAllMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_metadataDirectory))
        {
            return Array.Empty<ProfileMetadata>();
        }

        var results = new List<ProfileMetadata>();
        var files = Directory.GetFiles(_metadataDirectory, "*.json", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var document = JsonConvert.DeserializeObject<ProfileMetadataDocument>(json);
                if (document?.Headers == null)
                {
                    continue;
                }

                results.Add(new ProfileMetadata
                {
                    ProfileId = document.ProfileId,
                    ProfileName = document.ProfileName ?? string.Empty,
                    Headers = document.Headers,
                    UpdatedAt = document.UpdatedAt,
                    FilePath = file
                });
            }
            catch
            {
                // Skip corrupt metadata entries
            }
        }

        return results;
    }

    public Task DeleteMetadataAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var filePath = ResolveExistingFile(profile);
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    public string GetMetadataFilePath(Profile profile)
    {
        if (profile == null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (!string.IsNullOrWhiteSpace(profile.MetadataFileName))
        {
            return Path.Combine(_metadataDirectory, profile.MetadataFileName);
        }

        var fileName = BuildFileName(profile);
        profile.MetadataFileName = fileName;
        return Path.Combine(_metadataDirectory, fileName);
    }

    private string? ResolveExistingFile(Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.MetadataFileName))
        {
            var directPath = Path.Combine(_metadataDirectory, profile.MetadataFileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }
        }

        // Fallback to profile id search
        var pattern = $"{profile.Id:N}";
        if (Directory.Exists(_metadataDirectory))
        {
            var match = Directory.GetFiles(_metadataDirectory, "*.json")
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                profile.MetadataFileName = Path.GetFileName(match);
                return match;
            }
        }

        return null;
    }

    private static string BuildFileName(Profile profile)
    {
        var safeName = SanitizeForFileName(string.IsNullOrWhiteSpace(profile.Name)
            ? "profile"
            : profile.Name);

        return $"{safeName}-{profile.Id:N}.json";
    }

    private static string SanitizeForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            sanitized[index++] = invalid.Contains(ch) ? '_' : ch;
        }

        var result = new string(sanitized).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "profile" : result;
    }

    private sealed class ProfileMetadataDocument
    {
        public Guid ProfileId { get; set; }
        public string? ProfileName { get; set; }
        public List<string> Headers { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
