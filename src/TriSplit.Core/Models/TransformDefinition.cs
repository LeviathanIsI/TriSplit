using System.Collections.Generic;

namespace TriSplit.Core.Models;

public class TransformDefinition
{
    public string Raw { get; set; } = string.Empty;
    public TransformVerb Verb { get; set; } = TransformVerb.Trim;
    public List<string> Arguments { get; set; } = new();
}

public enum TransformVerb
{
    Trim,
    Upper,
    Lower,
    Zip5,
    Phone10,
    Left,
    Right,
    Replace,
    Concat
}
