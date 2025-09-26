using System.Collections.Generic;

namespace TriSplit.Core.Models;

public class ProfileMapping
{
    public string SourceField { get; set; } = string.Empty;
    public ProfileObjectType ObjectType { get; set; } = ProfileObjectType.Property;
    public int GroupIndex { get; set; }
    public string HubSpotHeader { get; set; } = string.Empty;
    public TransformDefinition? Transform { get; set; }
    public string? AssociationLabelOverride { get; set; }
    public string? DataSourceOverride { get; set; }
    public List<string> TagsOverride { get; set; } = new();

    public bool HasAssociationOverride => !string.IsNullOrWhiteSpace(AssociationLabelOverride);
    public bool HasDataSourceOverride => !string.IsNullOrWhiteSpace(DataSourceOverride);
    public bool HasTagsOverride => TagsOverride.Count > 0;
}
