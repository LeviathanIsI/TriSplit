namespace TriSplit.Core.Models;

public class HubSpotContact
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string LifecycleStage { get; set; } = string.Empty;
    public string LeadStatus { get; set; } = string.Empty;
    public Dictionary<string, object> Properties { get; set; } = new();
}

public class HubSpotProperty
{
    public string? Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}

public class HubSpotPhoneNumber
{
    public string? Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Type { get; set; } = "mobile";
    public bool IsPrimary { get; set; }
    public string? ContactId { get; set; }
}

public class HubSpotAssociation
{
    public string FromObjectType { get; set; } = string.Empty;
    public string FromObjectId { get; set; } = string.Empty;
    public string ToObjectType { get; set; } = string.Empty;
    public string ToObjectId { get; set; } = string.Empty;
    public string AssociationType { get; set; } = string.Empty;
}