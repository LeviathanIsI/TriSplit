using System.Collections.Generic;

namespace TriSplit.Core.Models;

public sealed class GroupAssociation
{
    public ProfileObjectType TargetType { get; set; } = ProfileObjectType.Contact;
    public int TargetIndex { get; set; }
    public List<string> Labels { get; set; } = new();
}
