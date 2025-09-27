using System.Collections.Generic;

namespace TriSplit.Core.Models;

public class ProfileGroupConfiguration
{
    public Dictionary<int, GroupDefaults> PropertyGroups { get; set; } = new();
    public Dictionary<int, GroupDefaults> ContactGroups { get; set; } = new();
    public Dictionary<int, GroupDefaults> PhoneGroups { get; set; } = new();

    public GroupDefaults GetOrAdd(ProfileObjectType objectType, int groupIndex)
    {
        var target = objectType switch
        {
            ProfileObjectType.Contact => ContactGroups,
            ProfileObjectType.Phone => PhoneGroups,
            _ => PropertyGroups
        };

        if (!target.TryGetValue(groupIndex, out var defaults))
        {
            defaults = new GroupDefaults
            {
                Type = objectType,
                Index = groupIndex
            };
            target[groupIndex] = defaults;
        }
        else
        {
            defaults.Type = objectType;
            defaults.Index = groupIndex;
        }

        defaults.Tags ??= new List<string>();
        defaults.Associations ??= new List<GroupAssociation>();

        return defaults;
    }
}
