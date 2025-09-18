using TriSplit.Core.Models;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Processors;

/// <summary>
/// Factory for creating the appropriate processor based on profile type or configuration
/// </summary>
public static class ProcessorFactory
{
    public static BaseProcessor CreateProcessor(
        Profile profile,
        IInputReader inputReader,
        IProgress<ProcessingProgress>? progress = null,
        string? processorType = null)
    {
        // Determine processor type from profile or explicit type
        var type = processorType ?? DetermineProcessorType(profile);

        return type?.ToLower() switch
        {
            "tlo" => new TloProcessor(profile, inputReader, progress),
            "taxlien" => new TloProcessor(profile, inputReader, progress),
            "generic" => new GenericProcessor(profile, inputReader, progress),
            _ => new TloProcessor(profile, inputReader, progress) // Default to TLO
        };
    }

    private static string DetermineProcessorType(Profile profile)
    {
        // Check profile name for hints
        if (profile.Name.Contains("TLO", StringComparison.OrdinalIgnoreCase) ||
            profile.Name.Contains("Tax Lien", StringComparison.OrdinalIgnoreCase))
        {
            return "tlo";
        }

        // Check if profile has owner/co-owner mappings (TLO characteristic)
        var hasOwnerMappings = profile.ContactMappings.Any(m =>
            m.AssociationType?.Contains("Owner", StringComparison.OrdinalIgnoreCase) == true);

        var hasPropertyMappings = profile.PropertyMappings.Any(m =>
            m.AssociationType?.Contains("Property", StringComparison.OrdinalIgnoreCase) == true ||
            m.AssociationType?.Contains("Mailing", StringComparison.OrdinalIgnoreCase) == true);

        if (hasOwnerMappings && hasPropertyMappings)
        {
            return "tlo";
        }

        // Default to generic processor
        return "generic";
    }
}

/// <summary>
/// Generic processor for non-TLO data sources
/// </summary>
public class GenericProcessor : BaseProcessor
{
    public GenericProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)
        : base(profile, inputReader, progress)
    {
    }

    protected override async Task ProcessRowAsync(Dictionary<string, object> row)
    {
        // Generate unique Import ID for this record
        var importId = GenerateImportId();

        // Create contact from mapped fields
        var contact = new ContactRecord
        {
            ImportId = importId,
            FirstName = GetMappedValue(row, "Contact", "firstname") ?? string.Empty,
            LastName = GetMappedValue(row, "Contact", "lastname") ?? string.Empty,
            Email = GetMappedValue(row, "Contact", "email") ?? string.Empty,
            Company = GetMappedValue(row, "Contact", "company") ?? string.Empty,
            AssociationLabel = "Contact"
        };

        // Only add contact if it has some data
        if (!string.IsNullOrWhiteSpace(contact.FirstName) ||
            !string.IsNullOrWhiteSpace(contact.LastName) ||
            !string.IsNullOrWhiteSpace(contact.Email))
        {
            _contacts[importId] = contact;

            // Process associated phone numbers
            await ProcessPhonesAsync(row, importId);

            // Process associated properties
            await ProcessPropertiesAsync(row, importId);
        }
    }

    private async Task ProcessPhonesAsync(Dictionary<string, object> row, string importId)
    {
        var phoneList = new List<PhoneRecord>();

        // Process phone mappings
        foreach (var mapping in _profile.PhoneMappings)
        {
            if (!string.IsNullOrEmpty(mapping.SourceColumn) && row.ContainsKey(mapping.SourceColumn))
            {
                var phoneValue = row.GetValueOrDefault(mapping.SourceColumn)?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(phoneValue))
                {
                    phoneList.Add(new PhoneRecord
                    {
                        ImportId = importId, // Link to contact
                        PhoneNumber = phoneValue,
                        PhoneType = mapping.HubSpotProperty ?? "Primary"
                    });
                }
            }
        }

        if (phoneList.Any())
        {
            _phones[importId] = phoneList;
        }

        await Task.CompletedTask;
    }

    private async Task ProcessPropertiesAsync(Dictionary<string, object> row, string importId)
    {
        // Process property mappings
        var hasPropertyData = false;
        var property = new PropertyRecord
        {
            ImportId = importId // Link to contact
        };

        foreach (var mapping in _profile.PropertyMappings)
        {
            if (!string.IsNullOrEmpty(mapping.SourceColumn) && row.ContainsKey(mapping.SourceColumn))
            {
                var value = row.GetValueOrDefault(mapping.SourceColumn)?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    hasPropertyData = true;

                    switch (mapping.HubSpotProperty?.ToLower())
                    {
                        case "address":
                            property.Address = value;
                            break;
                        case "city":
                            property.City = value;
                            break;
                        case "state":
                            property.State = value;
                            break;
                        case "zip":
                            property.Zip = value;
                            break;
                        case "county":
                            property.County = value;
                            break;
                        default:
                            if (mapping.AssociationType == "Mailing Address" ||
                                mapping.AssociationType == "Property Address")
                            {
                                property.AssociationLabel = mapping.AssociationType;
                            }
                            break;
                    }
                }
            }
        }

        if (hasPropertyData)
        {
            _properties[importId] = property;
        }

        await Task.CompletedTask;
    }
}