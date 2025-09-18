using System.Text.RegularExpressions;
using TriSplit.Core.Models;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Processors;

/// <summary>
/// Processor for TLO (Tax Lien Optimizer) data
/// Creates separate contacts for Owner 1 and Owner 2 with unique Import IDs
/// Links properties and phones using the Import ID
/// </summary>
public class TloProcessor : BaseProcessor
{
    public TloProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)
        : base(profile, inputReader, progress)
    {
    }

    protected override async Task ProcessRowAsync(Dictionary<string, object> row)
    {
        // Extract property information (shared between owners)
        var propertyAddress = GetMappedValue(row, "Property", "address") ??
                            row.GetValueOrDefault("Property Address")?.ToString() ?? string.Empty;
        var propertyCity = GetMappedValue(row, "Property", "city") ??
                         row.GetValueOrDefault("Property City")?.ToString() ?? string.Empty;
        var propertyState = GetMappedValue(row, "Property", "state") ??
                          row.GetValueOrDefault("Property State")?.ToString() ?? string.Empty;
        var propertyZip = GetMappedValue(row, "Property", "zip") ??
                        row.GetValueOrDefault("Property Zip")?.ToString() ?? string.Empty;
        var county = row.GetValueOrDefault("County")?.ToString() ?? string.Empty;

        // Process Owner 1 if present
        var owner1FirstName = GetMappedValue(row, "Owner", "firstname") ??
                            row.GetValueOrDefault("First Name")?.ToString() ?? string.Empty;
        var owner1LastName = GetMappedValue(row, "Owner", "lastname") ??
                           row.GetValueOrDefault("Last Name")?.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(owner1FirstName) || !string.IsNullOrWhiteSpace(owner1LastName))
        {
            var owner1ImportId = await ProcessOwnerAsync(
                row,
                owner1FirstName,
                owner1LastName,
                "Owner",
                true, // Is primary owner
                propertyAddress,
                propertyCity,
                propertyState,
                propertyZip,
                county
            );
        }

        // Process Owner 2 if present (check for co-owner fields)
        var owner2FirstName = row.GetValueOrDefault("Owner 2 First Name")?.ToString() ??
                            row.GetValueOrDefault("Co-Owner First Name")?.ToString() ?? string.Empty;
        var owner2LastName = row.GetValueOrDefault("Owner 2 Last Name")?.ToString() ??
                           row.GetValueOrDefault("Co-Owner Last Name")?.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(owner2FirstName) || !string.IsNullOrWhiteSpace(owner2LastName))
        {
            var owner2ImportId = await ProcessOwnerAsync(
                row,
                owner2FirstName,
                owner2LastName,
                "Co-Owner",
                false, // Not primary owner
                propertyAddress,
                propertyCity,
                propertyState,
                propertyZip,
                county
            );
        }

        await Task.CompletedTask;
    }

    private async Task<string> ProcessOwnerAsync(
        Dictionary<string, object> row,
        string firstName,
        string lastName,
        string ownerType,
        bool isPrimaryOwner,
        string propertyAddress,
        string propertyCity,
        string propertyState,
        string propertyZip,
        string county)
    {
        // Generate unique Import ID for this contact
        var importId = GenerateImportId();

        // Handle corporate names (full name in first, empty last)
        if (IsCorporateName(firstName) && string.IsNullOrWhiteSpace(lastName))
        {
            // Corporate entity - full name goes in First Name
            lastName = string.Empty;
        }

        // Create contact record with Import ID
        var contact = new ContactRecord
        {
            ImportId = importId,
            FirstName = CleanName(firstName),
            LastName = CleanName(lastName),
            AssociationLabel = ownerType,
            Notes = $"Property: {propertyAddress}, {propertyCity}, {propertyState} {propertyZip}"
        };

        // Add email if primary owner
        if (isPrimaryOwner)
        {
            contact.Email = GetMappedValue(row, "Owner", "email") ??
                          row.GetValueOrDefault("Email")?.ToString() ?? string.Empty;
        }

        _contacts[importId] = contact;

        // Process phone numbers with same Import ID for linking
        if (isPrimaryOwner)
        {
            await ProcessPhoneNumbersAsync(row, importId);
        }

        // Create property record linked by Import ID
        var property = new PropertyRecord
        {
            ImportId = importId, // Same Import ID links property to contact
            Address = CleanAddress(propertyAddress),
            City = propertyCity.Trim(),
            State = propertyState.Trim().ToUpper(),
            Zip = CleanZip(propertyZip),
            County = county.Trim(),
            AssociationLabel = isPrimaryOwner ? "Mailing Address" : "Property Address",
            PropertyType = row.GetValueOrDefault("Property Type")?.ToString() ?? "Residential",
            PropertyValue = row.GetValueOrDefault("Property Value")?.ToString() ?? string.Empty
        };

        _properties[importId] = property;

        // If primary owner, also add mailing address if different
        if (isPrimaryOwner)
        {
            var mailingAddress = GetMappedValue(row, "Mailing", "address") ??
                               row.GetValueOrDefault("Mailing Address")?.ToString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(mailingAddress) &&
                !mailingAddress.Equals(propertyAddress, StringComparison.OrdinalIgnoreCase))
            {
                // Create a separate property record for mailing address
                var mailingImportId = importId + "_mailing";
                var mailingProperty = new PropertyRecord
                {
                    ImportId = importId, // Still uses same Import ID to link to contact
                    Address = CleanAddress(mailingAddress),
                    City = GetMappedValue(row, "Mailing", "city") ??
                         row.GetValueOrDefault("Mailing City")?.ToString() ?? string.Empty,
                    State = (GetMappedValue(row, "Mailing", "state") ??
                           row.GetValueOrDefault("Mailing State")?.ToString() ?? string.Empty).Trim().ToUpper(),
                    Zip = CleanZip(GetMappedValue(row, "Mailing", "zip") ??
                        row.GetValueOrDefault("Mailing Zip")?.ToString() ?? string.Empty),
                    County = county.Trim(),
                    AssociationLabel = "Mailing Address"
                };
                _properties[mailingImportId] = mailingProperty;
            }
        }

        return importId;
    }

    private async Task ProcessPhoneNumbersAsync(Dictionary<string, object> row, string importId)
    {
        var phoneNumbers = new List<PhoneRecord>();

        // Check for multiple phone columns
        var phoneColumns = new[] { "Phone", "Phone Number", "Mobile", "Home Phone", "Work Phone", "Cell Phone" };

        foreach (var column in phoneColumns)
        {
            if (row.ContainsKey(column))
            {
                var phone = row.GetValueOrDefault(column)?.ToString() ?? string.Empty;
                phone = CleanPhoneNumber(phone);

                if (!string.IsNullOrWhiteSpace(phone) && phone.Length >= 10)
                {
                    var phoneType = column.Contains("Mobile") || column.Contains("Cell") ? "Mobile" :
                                  column.Contains("Home") ? "Home" :
                                  column.Contains("Work") ? "Work" : "Primary";

                    phoneNumbers.Add(new PhoneRecord
                    {
                        ImportId = importId, // Same Import ID links phone to contact
                        PhoneNumber = FormatPhoneNumber(phone),
                        PhoneType = phoneType
                    });
                }
            }
        }

        if (phoneNumbers.Any())
        {
            _phones[importId] = phoneNumbers;
        }

        await Task.CompletedTask;
    }

    private bool IsCorporateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var corporateIndicators = new[]
        {
            " LLC", " LP", " LLP", " INC", " CORP", " CO ", " COMPANY",
            " TRUST", " ESTATE", " PARTNERSHIP", " HOLDINGS", " GROUP",
            " ASSOCIATES", " PROPERTIES", " INVESTMENTS", " CAPITAL"
        };

        var upperName = name.ToUpper();
        return corporateIndicators.Any(indicator => upperName.Contains(indicator));
    }

    private string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Remove extra spaces and trim
        name = Regex.Replace(name, @"\s+", " ").Trim();

        // Proper case for non-corporate names
        if (!IsCorporateName(name))
        {
            name = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
        }

        return name;
    }

    private string CleanAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;

        // Remove extra spaces and trim
        address = Regex.Replace(address, @"\s+", " ").Trim();

        // Standardize common abbreviations
        address = Regex.Replace(address, @"\bST\b", "Street", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bAVE\b", "Avenue", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bRD\b", "Road", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bDR\b", "Drive", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bLN\b", "Lane", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bCT\b", "Court", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bPL\b", "Place", RegexOptions.IgnoreCase);
        address = Regex.Replace(address, @"\bBLVD\b", "Boulevard", RegexOptions.IgnoreCase);

        return address;
    }

    private string CleanPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        // Remove all non-numeric characters
        return Regex.Replace(phone, @"[^\d]", "");
    }

    private string FormatPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        phone = CleanPhoneNumber(phone);

        // Format as (XXX) XXX-XXXX if 10 digits
        if (phone.Length == 10)
        {
            return $"({phone.Substring(0, 3)}) {phone.Substring(3, 3)}-{phone.Substring(6, 4)}";
        }

        // Format as +X (XXX) XXX-XXXX if 11 digits
        if (phone.Length == 11 && phone.StartsWith("1"))
        {
            return $"+1 ({phone.Substring(1, 3)}) {phone.Substring(4, 3)}-{phone.Substring(7, 4)}";
        }

        return phone;
    }

    private string CleanZip(string zip)
    {
        if (string.IsNullOrWhiteSpace(zip))
            return string.Empty;

        // Remove all non-numeric and non-hyphen characters
        zip = Regex.Replace(zip, @"[^\d-]", "");

        // Ensure proper format (XXXXX or XXXXX-XXXX)
        if (zip.Length > 5 && !zip.Contains("-"))
        {
            zip = zip.Substring(0, 5) + "-" + zip.Substring(5);
        }

        return zip;
    }
}