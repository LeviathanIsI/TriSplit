using TriSplit.Core.Models;

namespace TriSplit.Core.Interfaces;

public interface IHubSpotService
{
    // Contact operations
    Task<HubSpotContact> CreateContactAsync(HubSpotContact contact);
    Task<HubSpotContact> UpdateContactAsync(HubSpotContact contact);
    Task<HubSpotContact?> GetContactAsync(string id);
    Task<IEnumerable<HubSpotContact>> SearchContactsAsync(string query);

    // Property operations
    Task<HubSpotProperty> CreatePropertyAsync(HubSpotProperty property);
    Task<HubSpotProperty> UpdatePropertyAsync(HubSpotProperty property);
    Task<HubSpotProperty?> GetPropertyAsync(string id);

    // Phone operations
    Task<HubSpotPhoneNumber> CreatePhoneNumberAsync(HubSpotPhoneNumber phone);
    Task<HubSpotPhoneNumber> UpdatePhoneNumberAsync(HubSpotPhoneNumber phone);

    // Association operations
    Task CreateAssociationAsync(HubSpotAssociation association);
    Task DeleteAssociationAsync(HubSpotAssociation association);
    Task<IEnumerable<HubSpotAssociation>> GetAssociationsAsync(string objectType, string objectId);

    // Merge operations
    Task<MergeResult> MergeContactsAsync(string primaryId, string secondaryId);
    Task<MergeResult> MergePropertiesAsync(string primaryId, string secondaryId);
}

public class MergeResult
{
    public bool Success { get; set; }
    public string MergedId { get; set; } = string.Empty;
    public List<string> AuditNotes { get; set; } = new();
    public Dictionary<string, object> MergedData { get; set; } = new();
}