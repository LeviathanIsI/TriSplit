using System.Collections.Generic;

namespace TriSplit.Core.Models;

public class GroupDefaults
{
    public string AssociationLabel { get; set; } = string.Empty;
    public string DataSource { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}
