using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TriSplit.Core.Models;

namespace TriSplit.Core.Interfaces;

public interface IProfileMetadataRepository
{
    Task<ProfileMetadata?> GetMetadataAsync(Profile profile, CancellationToken cancellationToken = default);
    Task SaveMetadataAsync(Profile profile, IEnumerable<string> headers, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileMetadata>> GetAllMetadataAsync(CancellationToken cancellationToken = default);
    Task DeleteMetadataAsync(Profile profile, CancellationToken cancellationToken = default);
    string GetMetadataFilePath(Profile profile);
}

public class ProfileMetadata
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public List<string> Headers { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string FilePath { get; set; } = string.Empty;
}
