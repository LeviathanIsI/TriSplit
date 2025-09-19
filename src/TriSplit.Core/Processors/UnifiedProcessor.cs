using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Core.Processors;

public class UnifiedProcessor
{
    private readonly Profile _profile;
    private readonly IInputReader _inputReader;
    private readonly IProgress<ProcessingProgress>? _progress;

    private readonly Dictionary<string, ContactRecord> _contacts = new();
    private readonly Dictionary<string, List<PhoneRecord>> _phones = new();
    private readonly Dictionary<string, PropertyRecord> _properties = new();

    private readonly Dictionary<string, string> _contactImportIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _mailingKeysByContact = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _mailingAssociationLabel;
    private readonly bool _dedupeMailing;
    private readonly bool _assumeSharedMailing;
    private readonly bool _createCompoundLabels;
    private readonly bool _skipRedundantMailing;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public UnifiedProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)
    {
        _profile = profile;
        _inputReader = inputReader;
        _progress = progress;

        _mailingAssociationLabel = GetMappingsByObjectType(MappingObjectTypes.Property)
            .Select(m => m.AssociationType?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.IndexOf("mail", StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        _dedupeMailing = GetRule("DeduplicateMailingPerContact", defaultValue: true);
        _assumeSharedMailing = GetRule("AssumeCoOwnerSharedMailing", defaultValue: true);
        _createCompoundLabels = GetRule("CreateCompoundLabels", defaultValue: true);
        _skipRedundantMailing = GetRule("SkipRedundantMailing", defaultValue: true);
    }

    public async Task<ProcessingResult> ProcessAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ReportProgress("Starting processing...", 0);

        _contacts.Clear();
        _phones.Clear();
        _properties.Clear();
        _contactImportIds.Clear();
        _mailingKeysByContact.Clear();

        string outputPath;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TriSplit",
                "Exports",
                timestamp);
        }
        else
        {
            outputPath = outputDirectory;
        }

        Directory.CreateDirectory(outputPath);

        try
        {
            ReportProgress("Reading input file...", 10);
            var inputData = await _inputReader.ReadAsync(inputFilePath);

            if (inputData.Rows.Count == 0)
            {
                throw new InvalidOperationException("No data found in input file");
            }

            var totalRows = inputData.Rows.Count;
            var processedRows = 0;

            foreach (var row in inputData.Rows)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await ProcessRowAsync(row);

                processedRows++;
                var percentComplete = 10 + (int)((processedRows / (double)totalRows) * 60);
                ReportProgress($"Processing row {processedRows}/{totalRows}...", percentComplete);
            }

            ReportProgress("Writing contacts file...", 80);
            var contactsFile = await WriteContactsFileAsync(outputPath, cancellationToken);

            ReportProgress("Writing phone numbers file...", 85);
            var phonesFile = await WritePhonesFileAsync(outputPath, cancellationToken);

            ReportProgress("Writing properties file...", 95);
            var propertiesFile = await WritePropertiesFileAsync(outputPath, cancellationToken);

            ReportProgress("Processing complete!", 100);

            return new ProcessingResult
            {
                Success = true,
                ContactsFile = contactsFile,
                PhonesFile = phonesFile,
                PropertiesFile = propertiesFile,
                TotalRecordsProcessed = processedRows,
                ContactsCreated = _contacts.Count,
                PropertiesCreated = _properties.Count,
                PhonesCreated = _phones.Sum(p => p.Value.Count)
            };
        }
        catch (Exception ex)
        {
            ReportProgress($"Error: {ex.Message}", -1);
            return new ProcessingResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task ProcessRowAsync(Dictionary<string, object> row)
    {
        var contexts = BuildContactContexts(row);
        if (contexts.Count == 0)
            return;

        ApplyMailingRules(contexts);

        foreach (var context in contexts)
        {
            AssignImportId(context);

            await ProcessPhoneNumbersAsync(row, context);

            PersistContact(context);
            PersistProperties(context);
        }
    }

    private List<ContactContext> BuildContactContexts(Dictionary<string, object> row)
    {
        var contexts = new List<ContactContext>();

        var groupedMappings = GetMappingsByObjectType(MappingObjectTypes.Contact)
            .Where(m => !string.IsNullOrWhiteSpace(m.AssociationType))
            .GroupBy(m => m.AssociationType!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var mailingSnapshot = BuildPropertySnapshot(row, _mailingAssociationLabel);

        var isFirst = true;
        foreach (var group in groupedMappings)
        {
            var association = group.Key;
            var context = new ContactContext(association)
            {
                IsPrimary = isFirst,
                FirstName = CleanName(GetMappedValue(row, association, "First Name", MappingObjectTypes.Contact)),
                LastName = CleanName(GetMappedValue(row, association, "Last Name", MappingObjectTypes.Contact)),
                Email = (GetMappedValue(row, association, "Email", MappingObjectTypes.Contact) ?? string.Empty).Trim(),
                Company = (GetMappedValue(row, association, "Company", MappingObjectTypes.Contact) ?? string.Empty).Trim(),
                Property = BuildPropertySnapshot(row, association),
                Mailing = mailingSnapshot
            };

            contexts.Add(context);
            isFirst = false;
        }

        return contexts;
    }
    private void ApplyMailingRules(List<ContactContext> contexts)
    {
        if (contexts.Count == 0)
            return;

        if (_createCompoundLabels)
        {
            foreach (var context in contexts)
            {
                if (context.PropertyMatchesMailing)
                {
                    context.TreatAsMailing = true;
                }
            }
        }

        if (_assumeSharedMailing)
        {
            var groups = contexts
                .Where(c => !string.IsNullOrWhiteSpace(c.LastName))
                .GroupBy(c => c.LastName, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                if (group.Count() < 2)
                    continue;

                if (!group.Any(c => c.Mailing.HasCoreAddress))
                    continue;

                foreach (var context in group)
                {
                    context.TreatAsMailing = true;
                }
            }
        }
    }

    private void AssignImportId(ContactContext context)
    {
        var key = BuildContactKey(context);
        if (!_contactImportIds.TryGetValue(key, out var importId))
        {
            importId = GenerateImportId();
            _contactImportIds[key] = importId;
        }

        context.ImportId = importId;
    }

    private void PersistContact(ContactContext context)
    {
        if (!context.HasContactData)
            return;

        if (_contacts.TryGetValue(context.ImportId, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.FirstName) && !string.IsNullOrWhiteSpace(context.FirstName))
                existing.FirstName = context.FirstName;
            if (string.IsNullOrWhiteSpace(existing.LastName) && !string.IsNullOrWhiteSpace(context.LastName))
                existing.LastName = context.LastName;
            if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(context.Email))
                existing.Email = context.Email;
            if (string.IsNullOrWhiteSpace(existing.Company) && !string.IsNullOrWhiteSpace(context.Company))
                existing.Company = context.Company;

            existing.AssociationLabel = MergeAssociationLabels(existing.AssociationLabel, context.Association);
        }
        else
        {
            var record = new ContactRecord
            {
                ImportId = context.ImportId,
                FirstName = context.FirstName,
                LastName = context.LastName,
                Email = context.Email,
                Company = context.Company,
                AssociationLabel = context.Association,
                Notes = string.Empty
            };

            _contacts[context.ImportId] = record;
        }
    }

    private void PersistProperties(ContactContext context)
    {
        if (context.Property.HasCoreAddress)
        {
            var associationLabel = context.TreatAsMailing && _createCompoundLabels
                ? MergeAssociationLabels(context.Association, _mailingAssociationLabel ?? "Mailing Address")
                : context.Association;

            var propertyRecord = context.Property.ToPropertyRecord(context.ImportId, associationLabel);
            var propertyKey = $"{context.ImportId}|PROPERTY|{Guid.NewGuid():N}";
            _properties[propertyKey] = propertyRecord;
        }

        var shouldAddMailingRecord = (context.IsPrimary || context.TreatAsMailing)
            && context.Mailing.HasCoreAddress
            && !(_skipRedundantMailing && context.TreatAsMailing && context.PropertyMatchesMailing);

        if (!shouldAddMailingRecord)
            return;

        var mailingKey = BuildMailingKey(context.ImportId, context.Mailing);
        if (_dedupeMailing && !RegisterMailingKey(context.ImportId, mailingKey))
        {
            return;
        }

        var mailingRecord = context.Mailing.ToPropertyRecord(context.ImportId, _mailingAssociationLabel ?? "Mailing Address");
        var mailingPropertyKey = $"{context.ImportId}|MAILING|{Guid.NewGuid():N}";
        _properties[mailingPropertyKey] = mailingRecord;
    }
    private async Task ProcessPhoneNumbersAsync(Dictionary<string, object> row, ContactContext context)
    {
        var phoneMappingCandidates = GetMappingsByObjectType(MappingObjectTypes.PhoneNumber)
            .ToList();

        if (phoneMappingCandidates.Count == 0)
        {
            await Task.CompletedTask;
            return;
        }

        var phoneMappings = phoneMappingCandidates
            .Where(m => string.Equals(m.HubSpotProperty, "Phone Number", StringComparison.OrdinalIgnoreCase))
            .Where(m => string.Equals(m.AssociationType?.Trim(), context.Association, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (phoneMappings.Count == 0)
        {
            phoneMappings = phoneMappingCandidates
                .Where(m => string.Equals(m.HubSpotProperty, "Phone Number", StringComparison.OrdinalIgnoreCase))
                .Where(m => string.IsNullOrWhiteSpace(m.AssociationType))
                .ToList();
        }

        if (phoneMappings.Count == 0)
        {
            await Task.CompletedTask;
            return;
        }

        var phoneTypeMappings = phoneMappingCandidates
            .Where(m => string.Equals(m.HubSpotProperty, "Phone Type", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var phoneRecords = new List<PhoneRecord>();

        foreach (var mapping in phoneMappings)
        {
            var phone = CleanPhoneNumber(GetValueFromMapping(row, mapping));
            if (string.IsNullOrWhiteSpace(phone) || phone.Length < 10)
                continue;

            var phoneType = "Primary";
            var possibleTypeColumns = new[]
            {
                mapping.SourceColumn?.Replace("Number", "Type", StringComparison.OrdinalIgnoreCase),
                mapping.SourceColumn + "PhoneType",
                mapping.SourceColumn + "Type",
                mapping.SourceColumn + "_Type"
            };

            var typeMapping = phoneTypeMappings.FirstOrDefault(m =>
                string.Equals(m.AssociationType?.Trim(), mapping.AssociationType?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                possibleTypeColumns.Contains(m.SourceColumn));

            if (typeMapping != null)
            {
                var typeValue = GetValueFromMapping(row, typeMapping);
                if (!string.IsNullOrWhiteSpace(typeValue))
                {
                    phoneType = typeValue.Trim();
                }
            }

            phoneRecords.Add(new PhoneRecord
            {
                ImportId = context.ImportId,
                PhoneNumber = FormatPhoneNumber(phone),
                PhoneType = phoneType
            });
        }

        if (phoneRecords.Count == 0)
        {
            await Task.CompletedTask;
            return;
        }

        if (_phones.TryGetValue(context.ImportId, out var existing))
        {
            foreach (var phone in phoneRecords)
            {
                if (!existing.Any(p => p.PhoneNumber.Equals(phone.PhoneNumber, StringComparison.OrdinalIgnoreCase)))
                {
                    existing.Add(phone);
                }
            }
        }
        else
        {
            _phones[context.ImportId] = phoneRecords;
        }

        await Task.CompletedTask;
    }
    private PropertySnapshot BuildPropertySnapshot(Dictionary<string, object> row, string? association)
    {
        if (string.IsNullOrWhiteSpace(association))
        {
            return PropertySnapshot.Empty;
        }

        var address = CleanAddress(GetMappedValue(row, association, "Address", MappingObjectTypes.Property));
        var city = (GetMappedValue(row, association, "City", MappingObjectTypes.Property) ?? string.Empty).Trim();
        var state = (GetMappedValue(row, association, "State", MappingObjectTypes.Property) ?? string.Empty).Trim().ToUpperInvariant();

        var zip = GetMappedValue(row, association, "Postal Code", MappingObjectTypes.Property);
        if (string.IsNullOrWhiteSpace(zip))
        {
            zip = GetMappedValue(row, association, "Zip", MappingObjectTypes.Property);
        }
        zip = CleanZip(zip);

        var county = (GetMappedValue(row, association, "County", MappingObjectTypes.Property) ?? string.Empty).Trim();
        var propertyType = (GetMappedValue(row, association, "Property Type", MappingObjectTypes.Property) ?? string.Empty).Trim();
        var propertyValue = (GetMappedValue(row, association, "Property Value", MappingObjectTypes.Property) ?? string.Empty).Trim();

        return new PropertySnapshot(address, city, state, zip, county, propertyType, propertyValue);
    }
    private IEnumerable<(FieldMapping Mapping, string ObjectType)> EnumerateResolvedMappings()
    {
        foreach (var mapping in _profile.ContactMappings)
        {
            yield return (mapping, ResolveObjectType(mapping, MappingObjectTypes.Contact));
        }

        foreach (var mapping in _profile.PropertyMappings)
        {
            yield return (mapping, ResolveObjectType(mapping, MappingObjectTypes.Property));
        }

        foreach (var mapping in _profile.PhoneMappings)
        {
            yield return (mapping, ResolveObjectType(mapping, MappingObjectTypes.PhoneNumber));
        }
    }

    private static string ResolveObjectType(FieldMapping mapping, string fallbackType)
    {
        if (!string.IsNullOrWhiteSpace(mapping.ObjectType))
        {
            return MappingObjectTypes.Normalize(mapping.ObjectType);
        }

        return fallbackType;
    }

    private IEnumerable<FieldMapping> GetMappingsByObjectType(string objectType)
    {
        return EnumerateResolvedMappings()
            .Where(pair => string.Equals(pair.ObjectType, objectType, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Mapping);
    }

    private IEnumerable<FieldMapping> GetAllMappings()
    {
        return EnumerateResolvedMappings().Select(pair => pair.Mapping);
    }

    private IEnumerable<FieldMapping> GetMappings(string? associationType, string hubSpotProperty, string? objectType = null)
    {
        var normalizedAssociation = associationType?.Trim();

        var candidates = EnumerateResolvedMappings()
            .Where(pair => string.Equals(pair.Mapping.HubSpotProperty, hubSpotProperty, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(objectType))
        {
            candidates = candidates.Where(pair => string.Equals(pair.ObjectType, objectType, StringComparison.OrdinalIgnoreCase));
        }

        var candidateList = candidates.ToList();

        var associationMatches = candidateList
            .Where(pair => string.Equals(pair.Mapping.AssociationType?.Trim(), normalizedAssociation, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Mapping)
            .ToList();

        if (associationMatches.Count > 0)
        {
            return associationMatches;
        }

        return candidateList
            .Where(pair => string.IsNullOrWhiteSpace(pair.Mapping.AssociationType))
            .Select(pair => pair.Mapping);
    }

    private string GetMappedValue(Dictionary<string, object> row, string? associationType, string hubSpotProperty, string? objectType = null)
    {
        if (string.IsNullOrWhiteSpace(hubSpotProperty))
            return string.Empty;

        string? fallback = null;

        foreach (var mapping in GetMappings(associationType, hubSpotProperty, objectType))
        {
            var value = GetValueFromMapping(row, mapping);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            fallback ??= value;
        }

        return fallback ?? string.Empty;
    }

    private static string GetValueFromMapping(Dictionary<string, object> row, FieldMapping mapping)
    {
        if (mapping == null || string.IsNullOrWhiteSpace(mapping.SourceColumn))
            return string.Empty;

        if (!row.TryGetValue(mapping.SourceColumn, out var rawValue) || rawValue is null)
            return string.Empty;

        var value = rawValue.ToString() ?? string.Empty;

        foreach (var transform in mapping.Transforms ?? new List<Transform>())
        {
            value = ApplyTransform(value, transform);
        }

        return value;
    }
    private string BuildContactKey(ContactContext context)
    {
        var keyParts = new List<string>();
        var dedupeKeys = _profile.DedupeSettings?.DedupeKeys ?? new List<string>();

        if (dedupeKeys.Count > 0)
        {
            foreach (var key in dedupeKeys)
            {
                keyParts.Add(NormalizeKeyPart(GetDedupeValue(context, key)));
            }
        }
        else
        {
            keyParts.Add(NormalizeKeyPart(context.Association));
            keyParts.Add(NormalizeKeyPart(context.FirstName));
            keyParts.Add(NormalizeKeyPart(context.LastName));
            keyParts.Add(NormalizeKeyPart(!string.IsNullOrWhiteSpace(context.Mailing.Address) ? context.Mailing.Address : context.Property.Address));
            keyParts.Add(NormalizeKeyPart(!string.IsNullOrWhiteSpace(context.Mailing.City) ? context.Mailing.City : context.Property.City));
            keyParts.Add(NormalizeKeyPart(!string.IsNullOrWhiteSpace(context.Mailing.State) ? context.Mailing.State : context.Property.State));
            keyParts.Add(NormalizeKeyPart(!string.IsNullOrWhiteSpace(context.Mailing.Zip) ? context.Mailing.Zip : context.Property.Zip));
        }

        return string.Join("|", keyParts);
    }

    private string GetDedupeValue(ContactContext context, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var token = key.Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();

        return token switch
        {
            "association" => context.Association,
            "firstname" => context.FirstName,
            "lastname" => context.LastName,
            "email" => context.Email,
            "company" => context.Company,
            "mailingaddress" => context.Mailing.Address,
            "mailingcity" => context.Mailing.City,
            "mailingstate" => context.Mailing.State,
            "mailingzip" => context.Mailing.Zip,
            "propertyaddress" => context.Property.Address,
            "propertycity" => context.Property.City,
            "propertystate" => context.Property.State,
            "propertyzip" => context.Property.Zip,
            _ => string.Empty
        };
    }

    private static string BuildMailingKey(string importId, PropertySnapshot snapshot)
    {
        return string.Join("|", new[]
        {
            NormalizeKeyPart(importId),
            NormalizeKeyPart(snapshot.Address),
            NormalizeKeyPart(snapshot.City),
            NormalizeKeyPart(snapshot.State),
            NormalizeKeyPart(snapshot.Zip)
        });
    }

    private bool RegisterMailingKey(string importId, string mailingKey)
    {
        if (!_mailingKeysByContact.TryGetValue(importId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _mailingKeysByContact[importId] = set;
        }

        return set.Add(mailingKey);
    }

    private bool GetRule(string ruleName, bool defaultValue = false)
    {
        if (_profile.ProcessingRules != null && _profile.ProcessingRules.TryGetValue(ruleName, out var value))
        {
            return value;
        }

        return defaultValue;
    }

    private static string GenerateImportId() => Guid.NewGuid().ToString();

    private static string NormalizeKeyPart(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string CleanName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = WhitespaceRegex.Replace(value, " ").Trim();
        if (IsCorporateName(value))
        {
            return value;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower());
    }

    private static bool IsCorporateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var upper = value.ToUpperInvariant();
        var corporateIndicators = new[]
        {
            " LLC", " LP", " LLP", " INC", " CORP", " CO ", " COMPANY",
            " TRUST", " ESTATE", " PARTNERSHIP", " HOLDINGS", " GROUP",
            " ASSOCIATES", " PROPERTIES", " INVESTMENTS", " CAPITAL"
        };

        return corporateIndicators.Any(indicator => upper.Contains(indicator));
    }

    private static string CleanAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = WhitespaceRegex.Replace(value, " ").Trim();

        value = Regex.Replace(value, @"\bST\b", "Street", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bAVE\b", "Avenue", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bRD\b", "Road", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bDR\b", "Drive", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bLN\b", "Lane", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bCT\b", "Court", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bPL\b", "Place", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bBLVD\b", "Boulevard", RegexOptions.IgnoreCase);

        return value;
    }

    private static string CleanZip(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = Regex.Replace(value, @"[^0-9-]", string.Empty);

        if (value.Length > 5 && !value.Contains('-'))
        {
            value = value.Substring(0, 5) + "-" + value.Substring(5);
        }

        return value;
    }

    private static string CleanPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        return Regex.Replace(phone, @"[^0-9]", string.Empty);
    }

    private static string FormatPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        if (phone.Length == 10)
        {
            return $"({phone.Substring(0, 3)}) {phone.Substring(3, 3)}-{phone.Substring(6, 4)}";
        }

        if (phone.Length == 11 && phone.StartsWith("1"))
        {
            return $"+1 ({phone.Substring(1, 3)}) {phone.Substring(4, 3)}-{phone.Substring(7, 4)}";
        }

        return phone;
    }

    private static string ApplyTransform(string value, Transform transform)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (transform == null || string.IsNullOrWhiteSpace(transform.Type))
            return value;

        switch (transform.Type.ToLowerInvariant())
        {
            case "regex":
                if (!string.IsNullOrEmpty(transform.Pattern))
                {
                    var regex = new Regex(transform.Pattern);
                    value = regex.Replace(value, transform.Replacement ?? string.Empty);
                }
                break;
            case "format":
                if (!string.IsNullOrEmpty(transform.Pattern))
                {
                    try
                    {
                        value = string.Format(transform.Pattern, value);
                    }
                    catch
                    {
                        // ignore formatting errors
                    }
                }
                break;
            case "normalize":
                value = value.Trim().ToUpperInvariant();
                break;
        }

        return value;
    }

    private static string MergeAssociationLabels(string existingLabels, string newLabel)
    {
        var labels = new List<string>();

        if (!string.IsNullOrWhiteSpace(existingLabels))
        {
            foreach (var part in existingLabels.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!labels.Any(l => string.Equals(l, trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    labels.Add(trimmed);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(newLabel))
        {
            var trimmed = newLabel.Trim();
            if (!labels.Any(l => string.Equals(l, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                labels.Add(trimmed);
            }
        }

        return string.Join("; ", labels);
    }
    private async Task<string> WriteContactsFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "01_Contacts_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        csv.WriteField("Import ID");
        csv.WriteField("First Name");
        csv.WriteField("Last Name");
        csv.WriteField("Email");
        csv.WriteField("Company");
        await csv.NextRecordAsync();

        foreach (var contact in _contacts.Values.OrderBy(c => c.LastName).ThenBy(c => c.FirstName))
        {
            csv.WriteField(contact.ImportId);
            csv.WriteField(contact.FirstName);
            csv.WriteField(contact.LastName);
            csv.WriteField(contact.Email);
            csv.WriteField(contact.Company);
            await csv.NextRecordAsync();
        }

        await writer.FlushAsync();
        return filePath;
    }

    private async Task<string> WritePhonesFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "02_Phone_Numbers_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        csv.WriteField("Import ID");
        csv.WriteField("Phone Number");
        csv.WriteField("Phone Type");
        await csv.NextRecordAsync();

        foreach (var phoneEntry in _phones.OrderBy(p => p.Key))
        {
            foreach (var phone in phoneEntry.Value)
            {
                csv.WriteField(phone.ImportId);
                csv.WriteField(phone.PhoneNumber);
                csv.WriteField(phone.PhoneType);
                await csv.NextRecordAsync();
            }
        }

        await writer.FlushAsync();
        return filePath;
    }

    private async Task<string> WritePropertiesFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "03_Properties_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        csv.WriteField("Import ID");
        csv.WriteField("Property Address");
        csv.WriteField("City");
        csv.WriteField("State");
        csv.WriteField("Zip");
        csv.WriteField("County");
        csv.WriteField("Association Label");
        csv.WriteField("Property Type");
        csv.WriteField("Property Value");
        await csv.NextRecordAsync();

        foreach (var property in _properties.Values.OrderBy(p => p.Address))
        {
            csv.WriteField(property.ImportId);
            csv.WriteField(property.Address);
            csv.WriteField(property.City);
            csv.WriteField(property.State);
            csv.WriteField(property.Zip);
            csv.WriteField(property.County);
            csv.WriteField(property.AssociationLabel);
            csv.WriteField(property.PropertyType);
            csv.WriteField(property.PropertyValue);
            await csv.NextRecordAsync();
        }

        await writer.FlushAsync();
        return filePath;
    }

    private void ReportProgress(string message, int percentComplete)
    {
        _progress?.Report(new ProcessingProgress
        {
            Message = message,
            PercentComplete = percentComplete,
            Timestamp = DateTime.Now
        });
    }

    private sealed class ContactContext
    {
        public ContactContext(string association)
        {
            Association = association;
        }

        public string Association { get; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public bool TreatAsMailing { get; set; }
        public string ImportId { get; set; } = string.Empty;
        public PropertySnapshot Property { get; set; } = PropertySnapshot.Empty;
        public PropertySnapshot Mailing { get; set; } = PropertySnapshot.Empty;

        public bool HasContactData => !string.IsNullOrWhiteSpace(FirstName) || !string.IsNullOrWhiteSpace(LastName) || !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(Company);
        public bool PropertyMatchesMailing => Property.HasCoreAddress && Mailing.HasCoreAddress && Property.EqualsCore(Mailing);

    }

    private sealed record PropertySnapshot(string Address, string City, string State, string Zip, string County, string PropertyType, string PropertyValue)
    {
        public static PropertySnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        public bool HasCoreAddress => !string.IsNullOrWhiteSpace(Address) || !string.IsNullOrWhiteSpace(City) || !string.IsNullOrWhiteSpace(State) || !string.IsNullOrWhiteSpace(Zip);

        public bool EqualsCore(PropertySnapshot other)
        {
            if (other is null)
                return false;

            return string.Equals(Normalize(Address), Normalize(other.Address), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(City), Normalize(other.City), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(State), Normalize(other.State), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(Zip), Normalize(other.Zip), StringComparison.OrdinalIgnoreCase);
        }

        public PropertyRecord ToPropertyRecord(string importId, string associationLabel)
        {
            return new PropertyRecord
            {
                ImportId = importId,
                Address = Address,
                City = City,
                State = State,
                Zip = Zip,
                County = County,
                AssociationLabel = associationLabel,
                PropertyType = PropertyType,
                PropertyValue = PropertyValue
            };
        }

        private static string Normalize(string? value) => (value ?? string.Empty).Trim();
    }
}

public class ContactRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string AssociationLabel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class PhoneRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string PhoneType { get; set; } = "Mobile";
}

public class PropertyRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty;
    public string AssociationLabel { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string PropertyValue { get; set; } = string.Empty;
}

public class ProcessingResult
{
    public bool Success { get; set; }
    public string ContactsFile { get; set; } = string.Empty;
    public string PhonesFile { get; set; } = string.Empty;
    public string PropertiesFile { get; set; } = string.Empty;
    public int TotalRecordsProcessed { get; set; }
    public int ContactsCreated { get; set; }
    public int PropertiesCreated { get; set; }
    public int PhonesCreated { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProcessingProgress
{
    public string Message { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
    public DateTime Timestamp { get; set; }
}

