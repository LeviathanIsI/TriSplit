using System.Collections.Generic;

namespace TriSplit.Core.Models;

public class ProfileHeaderSignature
{
    public List<string> Headers { get; set; } = new();
    public string Hash { get; set; } = string.Empty;
}
