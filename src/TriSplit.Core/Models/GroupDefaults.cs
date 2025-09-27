using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace TriSplit.Core.Models;

public class GroupDefaults
{
    private string? _legacyAssociationLabel;

    public ProfileObjectType Type { get; set; } = ProfileObjectType.Property;
    public int Index { get; set; }
    public string DataSource { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<GroupAssociation> Associations { get; set; } = new();

    [JsonIgnore]
    public string? LegacyAssociationLabel => _legacyAssociationLabel;

    [JsonProperty("AssociationLabel", NullValueHandling = NullValueHandling.Ignore)]
    private string? AssociationLabelLegacyProxy
    {
        get => null;
        set => _legacyAssociationLabel = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        Tags ??= new List<string>();
        Associations ??= new List<GroupAssociation>();
    }

    public GroupDefaults Clone()
    {
        return new GroupDefaults
        {
            Type = Type,
            Index = Index,
            DataSource = DataSource,
            DataType = DataType,
            Tags = Tags is null ? new List<string>() : new List<string>(Tags),
            Associations = Associations is null
                ? new List<GroupAssociation>()
                : Associations.Select(CloneAssociation).ToList()
        };
    }

    private static GroupAssociation CloneAssociation(GroupAssociation association)
    {
        return new GroupAssociation
        {
            TargetType = association.TargetType,
            TargetIndex = association.TargetIndex,
            Labels = association.Labels is null ? new List<string>() : new List<string>(association.Labels)
        };
    }
}
