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

    public List<FieldMapping> ContactMappings { get; set; } = new();
    public List<FieldMapping> PropertyMappings { get; set; } = new();
    public List<FieldMapping> PhoneMappings { get; set; } = new();

    public DedupeSettings DedupeSettings { get; set; } = new();
    public List<Transform> Transforms { get; set; } = new();
    public Dictionary<string, bool> ProcessingRules { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;
}

public class FieldMapping
{
    public string SourceColumn { get; set; } = string.Empty;
    public string HubSpotProperty { get; set; } = string.Empty;
    public string AssociationType { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public List<Transform> Transforms { get; set; } = new();
}

public class Transform
{
    public string Type { get; set; } = string.Empty; // "regex", "format", "normalize"
    public string Pattern { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public Dictionary<string, string> Options { get; set; } = new();
}

public class DedupeSettings
{
    public List<string> DedupeKeys { get; set; } = new();
    public bool UsePhoneNormalization { get; set; } = true;
    public bool UseAddressNormalization { get; set; } = true;
    public MergeStrategy MergeStrategy { get; set; } = MergeStrategy.PreferNewer;
}

public enum MergeStrategy
{
    PreferNewer,
    PreferOlder,
    Manual
}