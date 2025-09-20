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
    private readonly HashSet<string> _additionalPropertyFieldSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _additionalPropertyFieldOrder = new();

    private const string MailingAssociationLabel = "Mailing Address";

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex OwnerIndexRegex = new(@"owner\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Dictionary<string, int> OwnerOrdinalLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1,
        ["primary"] = 1,
        ["second"] = 2,
        ["third"] = 3,
        ["fourth"] = 4,
        ["1st"] = 1,
        ["2nd"] = 2,
        ["3rd"] = 3,
        ["4th"] = 4
    };


    private static readonly HashSet<string> PrimaryAssociationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Owner",
        "Executor"
    };

    private static readonly HashSet<string> SecondaryAssociationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Relative",
        "Associate"
    };

    public UnifiedProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)
    {
        _profile = profile;
        _inputReader = inputReader;
        _progress = progress;

        
    }

    public async Task<ProcessingResult> ProcessAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ReportProgress("Starting processing...", 0);

        _contacts.Clear();
        _phones.Clear();
        _properties.Clear();
        _contactImportIds.Clear();

        InitializeAdditionalPropertyFieldRegistry();

        string outputPath;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            var baseExportPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TriSplit",
                "Exports");

            var sourceFolderName = Path.GetFileNameWithoutExtension(inputFilePath);
            if (string.IsNullOrWhiteSpace(sourceFolderName))
            {
                sourceFolderName = "Export";
            }

            sourceFolderName = SanitizePathSegment(sourceFolderName);
            if (string.IsNullOrWhiteSpace(sourceFolderName))
            {
                sourceFolderName = "Export";
            }

            var preferredPath = Path.Combine(baseExportPath, sourceFolderName);
            outputPath = Directory.Exists(preferredPath)
                ? MakeUniqueDirectoryPath(preferredPath)
                : preferredPath;
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

            ReportProgress("Writing primary contacts file...", 80);
            var primaryContactsFile = await WriteContactsFileAsync(outputPath, "01_Primary_Contacts_Import.csv", false, cancellationToken);

            ReportProgress("Writing primary phone numbers file...", 85);
            var primaryPhonesFile = await WritePhonesFileAsync(outputPath, "02_Primary_Phone_Numbers_Import.csv", false, cancellationToken);

            ReportProgress("Writing primary properties file...", 90);
            var primaryPropertiesFile = await WritePropertiesFileAsync(outputPath, "03_Primary_Properties_Import.csv", false, cancellationToken);

            ReportProgress("Writing secondary contacts file...", 94);
            var secondaryContactsFile = await WriteContactsFileAsync(outputPath, "04_Secondary_Contacts_Import.csv", true, cancellationToken);

            ReportProgress("Writing secondary phone numbers file...", 96);
            var secondaryPhonesFile = await WritePhonesFileAsync(outputPath, "05_Secondary_Phone_Numbers_Import.csv", true, cancellationToken);

            ReportProgress("Writing secondary properties file...", 98);
            var secondaryPropertiesFile = await WritePropertiesFileAsync(outputPath, "06_Secondary_Properties_Import.csv", true, cancellationToken);

            ReportProgress("Processing complete!", 100);

            var primaryContactsCount = _contacts.Values.Count(c => !c.IsSecondary);
            var secondaryContactsCount = _contacts.Values.Count(c => c.IsSecondary);
            var primaryPhonesCount = _phones.Sum(p => p.Value.Count(phone => !phone.IsSecondary));
            var secondaryPhonesCount = _phones.Sum(p => p.Value.Count(phone => phone.IsSecondary));
            var primaryPropertiesCount = _properties.Values.Count(p => !p.IsSecondary);
            var secondaryPropertiesCount = _properties.Values.Count(p => p.IsSecondary);

            return new ProcessingResult
            {
                Success = true,
                ContactsFile = primaryContactsFile,
                SecondaryContactsFile = secondaryContactsFile,
                PhonesFile = primaryPhonesFile,
                SecondaryPhonesFile = secondaryPhonesFile,
                PropertiesFile = primaryPropertiesFile,
                SecondaryPropertiesFile = secondaryPropertiesFile,
                TotalRecordsProcessed = processedRows,
                ContactsCreated = primaryContactsCount,
                SecondaryContactsCreated = secondaryContactsCount,
                PropertiesCreated = primaryPropertiesCount,
                SecondaryPropertiesCreated = secondaryPropertiesCount,
                PhonesCreated = primaryPhonesCount,
                SecondaryPhonesCreated = secondaryPhonesCount
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

    private Task ProcessRowAsync(Dictionary<string, object> row)
    {
        var contexts = BuildContactContexts(row);
        if (contexts.Count == 0)
            return Task.CompletedTask;

        ApplyMailingInheritance(contexts);

        var primaryContext = DeterminePrimaryContext(contexts);
        if (primaryContext != null)
        {
            AssignImportId(primaryContext);
        }

        foreach (var context in contexts)
        {
            if (primaryContext == null || !ReferenceEquals(context, primaryContext))
            {
                AssignImportId(context);
            }
        }

        primaryContext ??= contexts[0];
        var primaryImportId = primaryContext.ImportId;

        foreach (var context in contexts)
        {
            context.IsPrimary = ReferenceEquals(context, primaryContext);
            context.IsSecondary = !context.IsPrimary && IsSecondaryAssociation(context.Association);
            context.LinkedContactId = context.IsSecondary && !string.IsNullOrWhiteSpace(primaryImportId)
                ? primaryImportId
                : null;
        }

        ProcessPhoneNumbers(row, contexts);

        foreach (var context in contexts)
        {
            PersistContact(context);
            PersistProperties(context, primaryContext);
        }

        return Task.CompletedTask;
    }

    private List<ContactContext> BuildContactContexts(Dictionary<string, object> row)
    {
        var contexts = new List<ContactContext>();

        var contactMappings = GetMappingsByObjectType(MappingObjectTypes.Contact).ToList();
        if (contactMappings.Count == 0)
            return contexts;

        var associationOrder = new List<string>();

        foreach (var mapping in contactMappings)
        {
            var association = (mapping.AssociationType ?? string.Empty).Trim();
            if (!associationOrder.Any(existing => string.Equals(existing, association, StringComparison.OrdinalIgnoreCase)))
            {
                associationOrder.Add(association);
            }
        }

        var mailingSnapshot = BuildPropertySnapshot(row, MailingAssociationLabel);

        for (var index = 0; index < associationOrder.Count; index++)
        {
            var association = associationOrder[index];
            var context = new ContactContext(association, index + 1)
            {
                IsPrimary = index == 0,
                FirstName = CleanName(GetMappedValue(row, association, "First Name", MappingObjectTypes.Contact)),
                LastName = CleanName(GetMappedValue(row, association, "Last Name", MappingObjectTypes.Contact)),
                Email = (GetMappedValue(row, association, "Email", MappingObjectTypes.Contact) ?? string.Empty).Trim(),
                Company = (GetMappedValue(row, association, "Company", MappingObjectTypes.Contact) ?? string.Empty).Trim(),
                Property = BuildPropertySnapshot(row, association),
                Mailing = mailingSnapshot
            };

            contexts.Add(context);
        }

        return contexts;
    }
    private void ApplyMailingInheritance(List<ContactContext> contexts)
    {
        if (contexts.Count <= 1)
            return;

        var primary = contexts[0];

        foreach (var context in contexts.Skip(1))
        {
            if (!HasSameSurname(context, primary))
                continue;

            if (!context.Mailing.HasCoreAddress && primary.Mailing.HasCoreAddress)
            {
                context.Mailing = primary.Mailing;
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
            if (string.IsNullOrWhiteSpace(existing.LinkedContactId) && !string.IsNullOrWhiteSpace(context.LinkedContactId))
                existing.LinkedContactId = context.LinkedContactId;

            existing.IsSecondary = context.IsSecondary;
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
                LinkedContactId = context.LinkedContactId ?? string.Empty,
                IsSecondary = context.IsSecondary,
                AssociationLabel = context.Association,
                Notes = string.Empty
            };

            _contacts[context.ImportId] = record;
        }
    }

    private void PersistProperties(ContactContext context, ContactContext primaryContext)
    {
        var associationLabel = DeterminePropertyAssociationLabel(context, primaryContext);

        if (!context.IsSecondary && context.Property.HasCoreAddress)
        {
            PersistPropertySnapshot(context.ImportId, context.Property, associationLabel, context.IsSecondary);
        }

        if (context.Mailing.HasCoreAddress)
        {
            PersistPropertySnapshot(context.ImportId, context.Mailing, MailingAssociationLabel, context.IsSecondary);
        }
        else if (context.IsSecondary && context.Property.HasCoreAddress)
        {
            PersistPropertySnapshot(context.ImportId, context.Property, MailingAssociationLabel, context.IsSecondary);
        }
    }
    private void ProcessPhoneNumbers(Dictionary<string, object> row, List<ContactContext> contexts)
    {
        if (contexts.Count == 0)
            return;

        var phoneMappings = GetMappingsByObjectType(MappingObjectTypes.PhoneNumber)
            .Where(m => string.Equals(m.HubSpotProperty, "Phone Number", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (phoneMappings.Count == 0)
            return;

        var primaryContext = DeterminePrimaryContext(contexts) ?? contexts[0];

        foreach (var mapping in phoneMappings)
        {
            var phone = CleanPhoneNumber(GetValueFromMapping(row, mapping));
            if (string.IsNullOrWhiteSpace(phone) || phone.Length < 10)
                continue;

            var owner = ResolvePhoneOwner(mapping, contexts) ?? primaryContext;
            var formatted = FormatPhoneNumber(phone);

            AddPhoneRecord(owner, formatted);
        }
    }

    private void AddPhoneRecord(ContactContext context, string phoneNumber)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.ImportId) || string.IsNullOrWhiteSpace(phoneNumber))
            return;

        var importId = context.ImportId;
        if (!_phones.TryGetValue(importId, out var existing))
        {
            existing = new List<PhoneRecord>();
            _phones[importId] = existing;
        }

        var existingRecord = existing.FirstOrDefault(p => p.PhoneNumber.Equals(phoneNumber, StringComparison.OrdinalIgnoreCase));
        if (existingRecord != null)
        {
            existingRecord.IsSecondary |= context.IsSecondary;
            return;
        }

        existing.Add(new PhoneRecord
        {
            ImportId = importId,
            PhoneNumber = phoneNumber,
            IsSecondary = context.IsSecondary
        });
    }

    private ContactContext? ResolvePhoneOwner(FieldMapping mapping, List<ContactContext> contexts)
    {
        if (!string.IsNullOrWhiteSpace(mapping.AssociationType))
        {
            var association = mapping.AssociationType.Trim();
            var explicitMatch = contexts.FirstOrDefault(c =>
                string.Equals(c.Association, association, StringComparison.OrdinalIgnoreCase));
            if (explicitMatch != null)
            {
                return explicitMatch;
            }
        }

        var sourceColumn = mapping.SourceColumn ?? string.Empty;

        if (TryGetOwnerIndexFromHeader(sourceColumn, out var index))
        {
            var matchedByIndex = contexts.FirstOrDefault(c => c.OwnerIndex == index);
            if (matchedByIndex != null)
            {
                return matchedByIndex;
            }
        }

        return contexts.FirstOrDefault();
    }

    private static IEnumerable<string> SplitAssociationTokens(string association)
    {
        if (string.IsNullOrWhiteSpace(association))
            return Array.Empty<string>();

        return association.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token));
    }

    private static bool AssociationContainsToken(string association, string token)
    {
        if (string.IsNullOrWhiteSpace(association) || string.IsNullOrWhiteSpace(token))
            return false;

        return SplitAssociationTokens(association)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrimaryAssociation(string association)
    {
        return SplitAssociationTokens(association).Any(token =>
            PrimaryAssociationTokens.Contains(token));
    }

    private static bool IsSecondaryAssociation(string association)
    {
        return SplitAssociationTokens(association).Any(token => SecondaryAssociationTokens.Contains(token));
    }

    private static ContactContext? DeterminePrimaryContext(List<ContactContext> contexts)
    {
        if (contexts.Count == 0)
            return null;

        var explicitPrimary = contexts.FirstOrDefault(c => IsPrimaryAssociation(c.Association));
        if (explicitPrimary != null)
            return explicitPrimary;

        var mailingPrimary = contexts.FirstOrDefault(c => AssociationContainsToken(c.Association, MailingAssociationLabel));
        if (mailingPrimary != null)
            return mailingPrimary;

        return contexts[0];
    }

    private static bool TryGetOwnerIndexFromHeader(string header, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(header))
            return false;

        var match = OwnerIndexRegex.Match(header);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            index = parsed;
            return true;
        }

        var lower = header.ToLowerInvariant();

        foreach (var pair in OwnerOrdinalLookup)
        {
            var token = pair.Key.ToLowerInvariant();
            if (lower.Contains(token + " owner") || lower.Contains("owner " + token))
            {
                index = pair.Value;
                return true;
            }
        }

        return false;
    }
    private PropertySnapshot BuildPropertySnapshot(Dictionary<string, object> row, string? association)
    {
        var normalizedAssociation = (association ?? string.Empty).Trim();

        var address = string.Empty;
        var city = string.Empty;
        var state = string.Empty;
        var zip = string.Empty;
        var county = string.Empty;
        var propertyType = string.Empty;
        var propertyValue = string.Empty;

        var additionalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var propertyMappings = GetMappingsByObjectType(MappingObjectTypes.Property)
            .Where(m => !string.IsNullOrWhiteSpace(m.HubSpotProperty))
            .ToList();

        if (propertyMappings.Count == 0)
        {
            return new PropertySnapshot(address, city, state, zip, county, propertyType, propertyValue, additionalFields);
        }

        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in propertyMappings)
        {
            var propertyName = mapping.HubSpotProperty?.Trim();
            if (string.IsNullOrWhiteSpace(propertyName) || !seenProperties.Add(propertyName))
            {
                continue;
            }

            var rawValue = GetMappedValue(row, normalizedAssociation, propertyName, MappingObjectTypes.Property);
            var value = rawValue ?? string.Empty;

            if (TryAssignCoreProperty(propertyName, value, ref address, ref city, ref state, ref zip, ref county, ref propertyType, ref propertyValue))
            {
                continue;
            }

            RegisterAdditionalPropertyField(propertyName);
            additionalFields[propertyName] = value.Trim();
        }

        return new PropertySnapshot(address, city, state, zip, county, propertyType, propertyValue, additionalFields);
    }


    private void PersistPropertySnapshot(string importId, PropertySnapshot snapshot, string associationLabel, bool isSecondary)
    {
        if (!snapshot.HasCoreAddress)
            return;

        var key = BuildPropertyKey(importId, snapshot);

        if (_properties.TryGetValue(key, out var existing))
        {
            existing.AssociationLabel = MergeAssociationLabels(existing.AssociationLabel, associationLabel);
            existing.IsSecondary = existing.IsSecondary || isSecondary;

            if (string.IsNullOrWhiteSpace(existing.Address))
                existing.Address = snapshot.Address;
            if (string.IsNullOrWhiteSpace(existing.City))
                existing.City = snapshot.City;
            if (string.IsNullOrWhiteSpace(existing.State))
                existing.State = snapshot.State;
            if (string.IsNullOrWhiteSpace(existing.Zip))
                existing.Zip = snapshot.Zip;
            if (string.IsNullOrWhiteSpace(existing.County))
                existing.County = snapshot.County;
            if (string.IsNullOrWhiteSpace(existing.PropertyType))
                existing.PropertyType = snapshot.PropertyType;
            if (string.IsNullOrWhiteSpace(existing.PropertyValue))
                existing.PropertyValue = snapshot.PropertyValue;

            foreach (var pair in snapshot.AdditionalFields)
            {
                RegisterAdditionalPropertyField(pair.Key);

                if (existing.AdditionalFields.TryGetValue(pair.Key, out var existingValue))
                {
                    if (string.IsNullOrWhiteSpace(existingValue) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        existing.AdditionalFields[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    existing.AdditionalFields[pair.Key] = pair.Value;
                }
            }

            return;
        }

        var record = snapshot.ToPropertyRecord(importId, associationLabel, isSecondary);
        foreach (var fieldName in record.AdditionalFields.Keys)
        {
            RegisterAdditionalPropertyField(fieldName);
        }

        _properties[key] = record;
    }


    private void InitializeAdditionalPropertyFieldRegistry()
    {
        _additionalPropertyFieldSet.Clear();
        _additionalPropertyFieldOrder.Clear();

        foreach (var mapping in GetMappingsByObjectType(MappingObjectTypes.Property))
        {
            var fieldName = mapping.HubSpotProperty?.Trim();
            if (string.IsNullOrWhiteSpace(fieldName))
                continue;

            if (GetCorePropertyKey(fieldName) != null)
                continue;

            RegisterAdditionalPropertyField(fieldName);
        }
    }

    private void RegisterAdditionalPropertyField(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return;

        if (GetCorePropertyKey(fieldName) != null)
            return;

        var trimmed = fieldName.Trim();
        if (_additionalPropertyFieldSet.Add(trimmed))
        {
            _additionalPropertyFieldOrder.Add(trimmed);
        }
    }

    private static bool TryAssignCoreProperty(string fieldName, string rawValue, ref string address, ref string city, ref string state, ref string zip, ref string county, ref string propertyType, ref string propertyValue)
    {
        var key = GetCorePropertyKey(fieldName);
        if (key is null)
            return false;

        var normalizedInput = rawValue ?? string.Empty;
        var trimmedValue = normalizedInput.Trim();
        var hasValue = !string.IsNullOrWhiteSpace(trimmedValue);

        switch (key)
        {
            case "Address":
                if (hasValue || string.IsNullOrWhiteSpace(address))
                {
                    address = CleanAddress(normalizedInput);
                }
                break;
            case "City":
                if (hasValue || string.IsNullOrWhiteSpace(city))
                {
                    city = trimmedValue;
                }
                break;
            case "State":
                if (hasValue || string.IsNullOrWhiteSpace(state))
                {
                    state = trimmedValue.ToUpperInvariant();
                }
                break;
            case "Zip":
                if (hasValue || string.IsNullOrWhiteSpace(zip))
                {
                    zip = CleanZip(normalizedInput);
                }
                break;
            case "County":
                if (hasValue || string.IsNullOrWhiteSpace(county))
                {
                    county = trimmedValue;
                }
                break;
            case "PropertyType":
                if (hasValue || string.IsNullOrWhiteSpace(propertyType))
                {
                    propertyType = trimmedValue;
                }
                break;
            case "PropertyValue":
                if (hasValue || string.IsNullOrWhiteSpace(propertyValue))
                {
                    propertyValue = trimmedValue;
                }
                break;
        }

        return true;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);

        foreach (var ch in trimmed)
        {
            if (invalidChars.Contains(ch) || ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString().Trim();
        sanitized = sanitized.TrimEnd('.');

        return sanitized;
    }

    private static string MakeUniqueDirectoryPath(string preferredPath)
    {
        if (!Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        var counter = 1;
        while (true)
        {
            var candidate = $"{preferredPath}_{counter}";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }

    private static string? GetCorePropertyKey(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return null;

        var normalized = fieldName.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "address":
            case "property address":
            case "mailing address":
            case "street address":
                return "Address";
            case "city":
            case "property city":
            case "mailing city":
                return "City";
            case "state":
            case "property state":
            case "mailing state":
                return "State";
            case "zip":
            case "postal code":
            case "zip code":
            case "property zip":
            case "mailing zip":
                return "Zip";
            case "county":
            case "property county":
            case "mailing county":
                return "County";
            case "property type":
                return "PropertyType";
            case "property value":
                return "PropertyValue";
            default:
                return null;
        }
    }

    private static string BuildPropertyKey(string importId, PropertySnapshot snapshot)
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

    private string DeterminePropertyAssociationLabel(ContactContext context, ContactContext primaryContext)
    {
        if (context.IsSecondary)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(context.Association))
        {
            return context.Association.Trim();
        }

        if (!ReferenceEquals(context, primaryContext) && HasSameSurname(context, primaryContext))
        {
            return MailingAssociationLabel;
        }

        return string.Empty;
    }

    private static bool HasSameSurname(ContactContext context, ContactContext primaryContext)
    {
        if (string.IsNullOrWhiteSpace(context.LastName) || string.IsNullOrWhiteSpace(primaryContext.LastName))
            return false;

        return string.Equals(context.LastName.Trim(), primaryContext.LastName.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private async Task<string> WriteContactsFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, fileName);

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
        csv.WriteField("Linked Contact ID");
        await csv.NextRecordAsync();

        var contacts = _contacts.Values
            .Where(c => c.IsSecondary == isSecondary)
            .OrderBy(c => c.LastName)
            .ThenBy(c => c.FirstName);

        foreach (var contact in contacts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            csv.WriteField(contact.ImportId);
            csv.WriteField(contact.FirstName);
            csv.WriteField(contact.LastName);
            csv.WriteField(contact.Email);
            csv.WriteField(contact.Company);
            csv.WriteField(contact.LinkedContactId ?? string.Empty);
            await csv.NextRecordAsync();
        }

        await writer.FlushAsync();
        return filePath;
    }

    private async Task<string> WritePhonesFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, fileName);

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        csv.WriteField("Import ID");
        csv.WriteField("Phone Number");
        await csv.NextRecordAsync();

        var phones = _phones
            .SelectMany(entry => entry.Value)
            .Where(phone => phone.IsSecondary == isSecondary)
            .OrderBy(phone => phone.ImportId)
            .ThenBy(phone => phone.PhoneNumber);

        foreach (var phone in phones)
        {
            cancellationToken.ThrowIfCancellationRequested();

            csv.WriteField(phone.ImportId);
            csv.WriteField(phone.PhoneNumber);
            await csv.NextRecordAsync();
        }

        await writer.FlushAsync();
        return filePath;
    }

    private async Task<string> WritePropertiesFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, fileName);

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
        csv.WriteField("Property Type");
        csv.WriteField("Property Value");

        foreach (var fieldName in _additionalPropertyFieldOrder)
        {
            csv.WriteField(fieldName);
        }

        csv.WriteField("Association Label");
        await csv.NextRecordAsync();

        var properties = _properties.Values
            .Where(p => p.IsSecondary == isSecondary)
            .OrderBy(p => p.Address)
            .ThenBy(p => p.ImportId);

        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            csv.WriteField(property.ImportId);
            csv.WriteField(property.Address);
            csv.WriteField(property.City);
            csv.WriteField(property.State);
            csv.WriteField(property.Zip);
            csv.WriteField(property.County);
            csv.WriteField(property.PropertyType);
            csv.WriteField(property.PropertyValue);

            foreach (var fieldName in _additionalPropertyFieldOrder)
            {
                property.AdditionalFields.TryGetValue(fieldName, out var value);
                csv.WriteField(value ?? string.Empty);
            }

            csv.WriteField(property.AssociationLabel);
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
        public ContactContext(string association, int ownerIndex)
        {
            Association = association;
            OwnerIndex = ownerIndex;
        }

        public string Association { get; }
        public int OwnerIndex { get; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public bool IsSecondary { get; set; }
        public string? LinkedContactId { get; set; }
        public string ImportId { get; set; } = string.Empty;
        public PropertySnapshot Property { get; set; } = PropertySnapshot.Empty;
        public PropertySnapshot Mailing { get; set; } = PropertySnapshot.Empty;

        public bool HasContactData => !string.IsNullOrWhiteSpace(FirstName) || !string.IsNullOrWhiteSpace(LastName) || !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(Company);
    }

    private sealed record PropertySnapshot
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyAdditionalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PropertySnapshot(string address, string city, string state, string zip, string county, string propertyType, string propertyValue, IReadOnlyDictionary<string, string>? additionalFields = null)
        {
            Address = address;
            City = city;
            State = state;
            Zip = zip;
            County = county;
            PropertyType = propertyType;
            PropertyValue = propertyValue;
            AdditionalFields = additionalFields ?? EmptyAdditionalFields;
        }

        public static PropertySnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, EmptyAdditionalFields);

        public string Address { get; }
        public string City { get; }
        public string State { get; }
        public string Zip { get; }
        public string County { get; }
        public string PropertyType { get; }
        public string PropertyValue { get; }
        public IReadOnlyDictionary<string, string> AdditionalFields { get; }

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

        public PropertyRecord ToPropertyRecord(string importId, string associationLabel, bool isSecondary)
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
                PropertyValue = PropertyValue,
                IsSecondary = isSecondary,
                AdditionalFields = new Dictionary<string, string>(AdditionalFields, StringComparer.OrdinalIgnoreCase)
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
    public string LinkedContactId { get; set; } = string.Empty;
    public bool IsSecondary { get; set; }
    public string AssociationLabel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class PhoneRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsSecondary { get; set; }
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
    public bool IsSecondary { get; set; }
    public Dictionary<string, string> AdditionalFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProcessingResult
{
    public bool Success { get; set; }
    public string ContactsFile { get; set; } = string.Empty;
    public string PhonesFile { get; set; } = string.Empty;
    public string SecondaryContactsFile { get; set; } = string.Empty;
    public string SecondaryPhonesFile { get; set; } = string.Empty;
    public string SecondaryPropertiesFile { get; set; } = string.Empty;
    public string PropertiesFile { get; set; } = string.Empty;
    public int TotalRecordsProcessed { get; set; }
    public int ContactsCreated { get; set; }
    public int PropertiesCreated { get; set; }
    public int PhonesCreated { get; set; }
    public int SecondaryContactsCreated { get; set; }
    public int SecondaryPropertiesCreated { get; set; }
    public int SecondaryPhonesCreated { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProcessingProgress
{
    public string Message { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
    public DateTime Timestamp { get; set; }
}

