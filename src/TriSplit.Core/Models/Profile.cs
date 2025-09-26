using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TriSplit.Core.Models;

public class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ProfileMapping> Mappings { get; set; } = new();
    public ProfileGroupConfiguration Groups { get; set; } = new();
    public OwnerMailingConfiguration OwnerMailing { get; set; } = new();
    public MissingHeaderBehavior MissingHeaderBehavior { get; set; } = MissingHeaderBehavior.Error;
    public ProfileHeaderSignature? HeaderSignature { get; set; }
    public bool CreateSecondaryContactsFile { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string MetadataFileName { get; set; } = string.Empty;

    [JsonIgnore]
    public IReadOnlyList<string> SourceHeaders { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;
}
