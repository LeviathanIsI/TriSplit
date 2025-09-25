using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    private readonly IExcelExporter _excelExporter;
    private readonly IProgress<ProcessingProgress>? _progress;

    private readonly Dictionary<string, ContactRecord> _contacts = new();
    private readonly Dictionary<string, List<PhoneRecord>> _phones = new();
    private readonly Dictionary<string, PropertyRecord> _properties = new();
    private readonly HashSet<string> _flaggedPhoneMessages = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _contactImportIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _additionalPropertyFieldSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _additionalPropertyFieldOrder = new();
    private readonly HashSet<string> _additionalPhoneFieldSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _additionalPhoneFieldOrder = new();
    private string? _activeTag;
    private string _activeDataSource = string.Empty;
    private string _activePhoneDataSource = string.Empty;
    private string _activeDataType = string.Empty;
    private readonly bool _createSecondaryContacts;
    private readonly string _defaultAssociationLabel;

    private const string MailingAssociationLabel = "Mailing Address";
    private const string ProfileDefaultPropertyGroupLabel = "Profile Default";
    private static readonly HashSet<string> MailingCoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Address",
        "City",
        "State",
        "Zip",
        "County"
    };


    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonDigitRegex = new(@"[^0-9]", RegexOptions.Compiled);
    private static readonly Regex OrdinalRegex = new(@"\b(\d+)(ST|ND|RD|TH)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DirectionRegex = new(@"\b(North|South|East|West)\s+(?!St\.?|Street|Ave\.?|Avenue|Dr\.?|Drive|Blvd\.?|Boulevard|Ln\.?|Lane|Rd\.?|Road|Ct\.?|Court|Pl\.?|Place)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreetAbbreviationPeriodRegex = new(@"\b(St|Ave|Dr|Blvd|Ln|Rd|Ct|Pl)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreetTypeWordRegex = new(@"\b(street|drive|avenue|boulevard|lane|road|court|place)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OwnerIndexRegex = new(@"owner\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhoneIndexRegex = new(@"(\d+)(?!.*\d)", RegexOptions.Compiled);
    private static readonly Regex PhoneQualifierRegex = new(@"(type|status|tags?|label)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    private static readonly Dictionary<string, string> DirectionAbbreviationLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["North"] = "N",
        ["South"] = "S",
        ["East"] = "E",
        ["West"] = "W"
    };

    private static readonly Dictionary<string, string> StreetTypeAbbreviationLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["street"] = "St",
        ["drive"] = "Dr",
        ["avenue"] = "Ave",
        ["boulevard"] = "Blvd",
        ["lane"] = "Ln",
        ["road"] = "Rd",
        ["court"] = "Ct",
        ["place"] = "Pl"
    };

    private static readonly Dictionary<string, string> StateAbbreviationLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alabama"] = "AL",
        ["alaska"] = "AK",
        ["arizona"] = "AZ",
        ["arkansas"] = "AR",
        ["california"] = "CA",
        ["colorado"] = "CO",
        ["connecticut"] = "CT",
        ["delaware"] = "DE",
        ["florida"] = "FL",
        ["georgia"] = "GA",
        ["hawaii"] = "HI",
        ["idaho"] = "ID",
        ["illinois"] = "IL",
        ["indiana"] = "IN",
        ["iowa"] = "IA",
        ["kansas"] = "KS",
        ["kentucky"] = "KY",
        ["louisiana"] = "LA",
        ["maine"] = "ME",
        ["maryland"] = "MD",
        ["massachusetts"] = "MA",
        ["michigan"] = "MI",
        ["minnesota"] = "MN",
        ["mississippi"] = "MS",
        ["missouri"] = "MO",
        ["montana"] = "MT",
        ["nebraska"] = "NE",
        ["nevada"] = "NV",
        ["new hampshire"] = "NH",
        ["new jersey"] = "NJ",
        ["new mexico"] = "NM",
        ["new york"] = "NY",
        ["north carolina"] = "NC",
        ["north dakota"] = "ND",
        ["ohio"] = "OH",
        ["oklahoma"] = "OK",
        ["oregon"] = "OR",
        ["pennsylvania"] = "PA",
        ["rhode island"] = "RI",
        ["south carolina"] = "SC",
        ["south dakota"] = "SD",
        ["tennessee"] = "TN",
        ["texas"] = "TX",
        ["utah"] = "UT",
        ["vermont"] = "VT",
        ["virginia"] = "VA",
        ["washington"] = "WA",
        ["west virginia"] = "WV",
        ["wisconsin"] = "WI",
        ["wyoming"] = "WY",
        ["district of columbia"] = "DC",
        ["puerto rico"] = "PR"
    };

    public UnifiedProcessor(Profile profile, IInputReader inputReader, IExcelExporter excelExporter, IProgress<ProcessingProgress>? progress = null)
    {
        _profile = profile;
        _inputReader = inputReader;
        _excelExporter = excelExporter;
        _progress = progress;
        _createSecondaryContacts = profile?.CreateSecondaryContactsFile ?? false;
        _defaultAssociationLabel = NormalizeAssociation(ResolveLegacyDefaultAssociationLabel(profile));
    }

    public async Task<ProcessingResult> ProcessAsync(string inputFilePath, string outputDirectory, ProcessingOptions options, CancellationToken cancellationToken = default)
    {
        options ??= new ProcessingOptions();
        if (!options.OutputCsv && !options.OutputExcel && !options.OutputJson)
        {
            throw new InvalidOperationException("At least one output format must be selected.");
        }

        ReportProgress("Starting processing...", 0);

        _contacts.Clear();
        _phones.Clear();
        _properties.Clear();
        _contactImportIds.Clear();

        InitializeAdditionalPhoneFieldRegistry();
        InitializeAdditionalPropertyFieldRegistry();

        var outputPath = ResolveOutputPath(inputFilePath, outputDirectory);
        Directory.CreateDirectory(outputPath);

        string currentStage = "initializing";

        try
        {
            currentStage = "configuring data sources";
            _activeDataSource = string.Empty;
            _activePhoneDataSource = string.Empty;
            _activeDataType = string.Empty;
            _activeTag = NormalizeTag(options.Tag);
            currentStage = "reading input file";
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
                cancellationToken.ThrowIfCancellationRequested();

                var rowNumber = processedRows + 1;
                currentStage = $"processing row {rowNumber}";
                await ProcessRowAsync(row);

                processedRows++;
                var percentComplete = 10 + (int)((processedRows / (double)totalRows) * 60);
                ReportProgress($"Processing row {processedRows}/{totalRows}...", percentComplete);
            }

            var csvFiles = new List<string>();
            var excelFiles = new List<string>();
            var jsonFiles = new List<string>();

            if (options.OutputCsv)
            {
                currentStage = "writing primary contacts CSV";
                ReportProgress("Writing CSV exports...", 80);
                csvFiles.Add(await WriteContactsFileAsync(outputPath, "01_Primary_Contacts_Import.csv", false, cancellationToken));
                currentStage = "writing primary phone CSV";
                csvFiles.Add(await WritePhonesFileAsync(outputPath, "02_Primary_Phone_Numbers_Import.csv", false, cancellationToken));
                currentStage = "writing primary property CSV";
                csvFiles.Add(await WritePropertiesFileAsync(outputPath, "03_Primary_Properties_Import.csv", false, cancellationToken));
                if (_createSecondaryContacts)
                {
                    currentStage = "writing secondary contacts CSV";
                    var secondaryContactsCsv = await WriteContactsFileAsync(outputPath, "04_Secondary_Contacts_Import.csv", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryContactsCsv))
                    {
                        csvFiles.Add(secondaryContactsCsv);
                    }
                    currentStage = "writing secondary phone CSV";
                    var secondaryPhonesCsv = await WritePhonesFileAsync(outputPath, "05_Secondary_Phone_Numbers_Import.csv", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryPhonesCsv))
                    {
                        csvFiles.Add(secondaryPhonesCsv);
                    }
                    currentStage = "writing secondary property CSV";
                    var secondaryPropertiesCsv = await WritePropertiesFileAsync(outputPath, "06_Secondary_Properties_Import.csv", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryPropertiesCsv))
                    {
                        csvFiles.Add(secondaryPropertiesCsv);
                    }
                }
            }
            if (options.OutputExcel)
            {
                currentStage = "writing primary contacts Excel";
                ReportProgress("Writing Excel exports...", 85);
                excelFiles.Add(await WriteContactsExcelAsync(outputPath, "Contacts_Primary.xlsx", false, cancellationToken));
                currentStage = "writing primary phone Excel";
                excelFiles.Add(await WritePhonesExcelAsync(outputPath, "Phones_Primary.xlsx", false, cancellationToken));
                currentStage = "writing primary property Excel";
                excelFiles.Add(await WritePropertiesExcelAsync(outputPath, "Properties_Primary.xlsx", false, cancellationToken));
                if (_createSecondaryContacts)
                {
                    currentStage = "writing secondary contacts Excel";
                    var secondaryContactsExcel = await WriteContactsExcelAsync(outputPath, "Contacts_Secondary.xlsx", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryContactsExcel))
                    {
                        excelFiles.Add(secondaryContactsExcel);
                    }
                    currentStage = "writing secondary phone Excel";
                    var secondaryPhonesExcel = await WritePhonesExcelAsync(outputPath, "Phones_Secondary.xlsx", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryPhonesExcel))
                    {
                        excelFiles.Add(secondaryPhonesExcel);
                    }
                    currentStage = "writing secondary property Excel";
                    var secondaryPropertiesExcel = await WritePropertiesExcelAsync(outputPath, "Properties_Secondary.xlsx", true, cancellationToken);
                    if (!string.IsNullOrEmpty(secondaryPropertiesExcel))
                    {
                        excelFiles.Add(secondaryPropertiesExcel);
                    }
                }
            }
            if (options.OutputJson)
            {
                currentStage = "writing primary contacts JSON";
                ReportProgress("Writing JSON exports...", 90);
                jsonFiles.Add(await WriteContactsJsonAsync(outputPath, "Contacts_Primary.json", false, cancellationToken));
                currentStage = "writing primary phone JSON";
                jsonFiles.Add(await WritePhonesJsonAsync(outputPath, "Phones_Primary.json", false, cancellationToken));
                currentStage = "writing primary property JSON";
                jsonFiles.Add(await WritePropertiesJsonAsync(outputPath, "Properties_Primary.json", false, cancellationToken));
                if (_createSecondaryContacts)
                {
                    currentStage = "writing secondary contacts JSON";
                    jsonFiles.Add(await WriteContactsJsonAsync(outputPath, "Contacts_Secondary.json", true, cancellationToken));
                    currentStage = "writing secondary phone JSON";
                    jsonFiles.Add(await WritePhonesJsonAsync(outputPath, "Phones_Secondary.json", true, cancellationToken));
                    currentStage = "writing secondary property JSON";
                    jsonFiles.Add(await WritePropertiesJsonAsync(outputPath, "Properties_Secondary.json", true, cancellationToken));
                }
            }
            currentStage = "finalizing run";
            ReportProgress("Processing complete!", 100);

            var primaryContactsCount = _contacts.Values.Count(c => !c.IsSecondary);
            var secondaryContactsCount = _contacts.Values.Count(c => c.IsSecondary);
            var primaryPhonesCount = _phones.Sum(p => p.Value.Count(phone => !phone.IsSecondary));
            var secondaryPhonesCount = _phones.Sum(p => p.Value.Count(phone => phone.IsSecondary));
            var primaryPropertiesCount = _properties.Values.Count(p => !p.IsSecondary);
            var secondaryPropertiesCount = _properties.Values.Count(p => p.IsSecondary);

            currentStage = "writing summary report";
            var summaryPath = await WriteSummaryReportAsync(outputPath, processedRows, primaryContactsCount, secondaryContactsCount, primaryPhonesCount, secondaryPhonesCount, primaryPropertiesCount, secondaryPropertiesCount).ConfigureAwait(false);

            return new ProcessingResult
            {
                Success = true,
                CsvFiles = csvFiles,
                ExcelFiles = excelFiles,
                JsonFiles = jsonFiles,
                ContactsFile = csvFiles.ElementAtOrDefault(0) ?? string.Empty,
                PhonesFile = csvFiles.ElementAtOrDefault(1) ?? string.Empty,
                PropertiesFile = csvFiles.ElementAtOrDefault(2) ?? string.Empty,
                SecondaryContactsFile = csvFiles.ElementAtOrDefault(3) ?? string.Empty,
                SecondaryPhonesFile = csvFiles.ElementAtOrDefault(4) ?? string.Empty,
                SecondaryPropertiesFile = csvFiles.ElementAtOrDefault(5) ?? string.Empty,
                SummaryReportPath = summaryPath,
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
            var detailedMessage = $"Error during {currentStage}: {ex.GetType().Name} - {ex.Message}";
            ReportProgress(detailedMessage, -1, ProcessingProgressSeverity.Error);
            await WriteFailureLogAsync(outputPath, ex, currentStage).ConfigureAwait(false);
            return new ProcessingResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _activeTag = null;
        }
    }

    private static string ResolveOutputPath(string inputFilePath, string requestedOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputDirectory))
        {
            return requestedOutputDirectory;
        }

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
        return Directory.Exists(preferredPath)
            ? MakeUniqueDirectoryPath(preferredPath)
            : preferredPath;
    }

    private Task ProcessRowAsync(Dictionary<string, object> row)
    {
        var sharedMailingSnapshot = BuildPropertySnapshot(row, MailingAssociationLabel);
        var contexts = BuildContactContexts(row);
        if (contexts.Count == 0)
            return Task.CompletedTask;

        var primaryContext = DeterminePrimaryContext(contexts) ?? contexts[0];

        if (sharedMailingSnapshot.HasCoreAddress)
        {
            primaryContext.Mailing = sharedMailingSnapshot;
        }

        ApplyMailingInheritance(contexts, primaryContext);
        EnsureMailingSnapshots(contexts);

        AssignImportId(primaryContext);

        foreach (var context in contexts)
        {
            if (!ReferenceEquals(context, primaryContext))
            {
                AssignImportId(context);
            }
        }

        var primaryImportId = primaryContext.ImportId;

        foreach (var context in contexts)
        {
            context.IsPrimary = ReferenceEquals(context, primaryContext);

            if (context.IsPrimary)
            {
                context.SharesMailingWithPrimary = false;
                context.IsSecondary = false;
                context.LinkedContactId = null;
                continue;
            }

            var sharesMailing = ShouldShareMailingWithPrimary(context, primaryContext);
            context.SharesMailingWithPrimary = sharesMailing;
            var isSecondaryAssociation = _createSecondaryContacts && IsSecondaryAssociation(context.Association);
            context.IsSecondary = isSecondaryAssociation;
            context.LinkedContactId = _createSecondaryContacts && (sharesMailing || isSecondaryAssociation) && !string.IsNullOrWhiteSpace(primaryImportId)
                ? primaryImportId
                : null;
        }

        LogHouseholdAssignments(primaryContext, contexts);

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
        var associationBuckets = new Dictionary<string, List<ContactContext>>(StringComparer.OrdinalIgnoreCase);

        var contactMappings = GetMappingsByObjectType(MappingObjectTypes.Contact).ToList();
        if (contactMappings.Count == 0)
            return contexts;

        foreach (var mapping in contactMappings)
        {
            var association = ResolveAssociation(mapping.AssociationType);
            var normalizedProperty = NormalizeContactProperty(mapping.HubSpotProperty);
            var value = GetValueFromMapping(row, mapping);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!associationBuckets.TryGetValue(association, out var bucket))
            {
                bucket = new List<ContactContext>();
                associationBuckets[association] = bucket;
            }

            var target = FindContactTarget(bucket, normalizedProperty);

            if (target == null && IsCoreIdentityProperty(normalizedProperty))
            {
                target = new ContactContext(association, bucket.Count + 1);
                bucket.Add(target);
                contexts.Add(target);
            }

            if (target == null)
            {
                if (bucket.Count == 0)
                {
                    target = new ContactContext(association, 1);
                    bucket.Add(target);
                    contexts.Add(target);
                }
                else
                {
                    target = bucket[^1];
                }
            }

            AssignContactFieldValue(target, normalizedProperty, value);
        }

        foreach (var pair in associationBuckets)
        {
            var association = pair.Key;
            var propertyGroups = GetPropertyGroupKeys(association);

            foreach (var context in pair.Value)
            {
                context.PropertyGroups.Clear();

                foreach (var group in propertyGroups)
                {
                    var normalizedGroup = NormalizePropertyGroup(group);
                    var snapshot = BuildPropertySnapshot(row, association, group);
                    context.PropertyGroups[normalizedGroup] = snapshot;

                    if (string.IsNullOrEmpty(normalizedGroup))
                    {
                        context.Property = snapshot;
                    }
                }

                if (!context.PropertyGroups.TryGetValue(string.Empty, out var defaultSnapshot))
                {
                    var firstSnapshot = context.PropertyGroups.Values.FirstOrDefault(p => p.HasCoreAddress) ?? PropertySnapshot.Empty;
                    context.Property = firstSnapshot;
                }
            }
        }

        return contexts;
    }
    private void ApplyMailingInheritance(List<ContactContext> contexts, ContactContext? primaryContext)
    {
        if (primaryContext == null || contexts.Count <= 1)
            return;

        foreach (var context in contexts)
        {
            if (ReferenceEquals(context, primaryContext))
                continue;

            if (!HasSameSurname(context, primaryContext))
                continue;

            if (!context.Mailing.HasCoreAddress && primaryContext.Mailing.HasCoreAddress)
            {
                context.Mailing = primaryContext.Mailing;
            }
        }
    }

    private static void EnsureMailingSnapshots(IEnumerable<ContactContext> contexts)
    {
        foreach (var context in contexts)
        {
            if (!context.Mailing.HasCoreAddress && context.Property.HasCoreAddress)
            {
                context.Mailing = context.Property;
            }
        }
    }

    private static ContactContext? FindContactTarget(List<ContactContext> contexts, string property)
    {
        if (contexts.Count == 0)
            return null;

        if (IsFirstNameProperty(property))
        {
            var candidate = contexts.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.FirstName));
            if (candidate != null)
                return candidate;
        }
        else if (IsLastNameProperty(property))
        {
            var candidate = contexts.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.LastName));
            if (candidate != null)
                return candidate;
        }
        else if (IsEmailProperty(property))
        {
            var candidate = contexts.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Email));
            if (candidate != null)
                return candidate;
        }
        else if (IsCompanyProperty(property))
        {
            var candidate = contexts.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Company));
            if (candidate != null)
                return candidate;
        }

        return contexts.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.FirstName) || !string.IsNullOrWhiteSpace(c.LastName));
    }

    private static string NormalizeContactProperty(string? property)
    {
        if (string.IsNullOrWhiteSpace(property))
            return string.Empty;

        var normalized = property.Trim().ToLowerInvariant();
        var spaced = WhitespaceRegex.Replace(normalized.Replace('_', ' ').Replace('-', ' '), " " ).Trim();

        bool ContainsToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (spaced.Contains(token))
                return true;

            var compact = token.Replace(" ", string.Empty);
            return normalized.Contains(compact);
        }

        if (ContainsToken("email"))
            return "email";

        if (ContainsToken("company") || ContainsToken("business name"))
            return "company";

        if (ContainsToken("first name"))
            return "first name";

        if (ContainsToken("last name") || ContainsToken("surname"))
            return "last name";

        if (ContainsToken("full name"))
            return "full name";

        if (spaced.EndsWith(" name") && !ContainsToken("company") && !ContainsToken("business"))
            return "name";

        return normalized;
    }

    private static void AssignContactFieldValue(ContactContext context, string property, string value)
    {
        switch (property)
        {
            case "first name":
                context.FirstName = CleanName(value);
                break;
            case "last name":
                context.LastName = CleanName(value);
                break;
            case "email":
                context.Email = value.Trim();
                break;
            case "company":
                context.Company = value.Trim();
                break;
            case "full name":
            case "name":
                var cleaned = CleanName(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1)
                    {
                        context.FirstName = string.Join(' ', parts.Take(parts.Length - 1));
                        context.LastName = parts[^1];
                    }
                    else
                    {
                        context.FirstName = cleaned;
                    }
                }
                break;
            default:
                context.AdditionalContactFields[property] = value;
                break;
        }
    }

    private static bool IsFirstNameProperty(string property) => property.Equals("first name", StringComparison.OrdinalIgnoreCase);
    private static bool IsLastNameProperty(string property) => property.Equals("last name", StringComparison.OrdinalIgnoreCase);
    private static bool IsEmailProperty(string property) => property.Equals("email", StringComparison.OrdinalIgnoreCase);
    private static bool IsCompanyProperty(string property) => property.Equals("company", StringComparison.OrdinalIgnoreCase);
    private static bool IsCoreIdentityProperty(string property) => IsFirstNameProperty(property) || IsLastNameProperty(property) || property.Equals("full name", StringComparison.OrdinalIgnoreCase) || property.Equals("name", StringComparison.OrdinalIgnoreCase);

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
            ReconcilePrimaryState(existing, context);
            if (string.IsNullOrWhiteSpace(existing.FirstName) && !string.IsNullOrWhiteSpace(context.FirstName))
                existing.FirstName = context.FirstName;
            if (string.IsNullOrWhiteSpace(existing.LastName) && !string.IsNullOrWhiteSpace(context.LastName))
                existing.LastName = context.LastName;
            if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(context.Email))
                existing.Email = context.Email;
            if (string.IsNullOrWhiteSpace(existing.Company) && !string.IsNullOrWhiteSpace(context.Company))
                existing.Company = context.Company;
            if (string.IsNullOrWhiteSpace(existing.LinkedContactId) && !string.IsNullOrWhiteSpace(context.LinkedContactId))
                existing.LinkedContactId = NormalizeLinkedContactId(context.LinkedContactId, existing.ImportId);

            existing.AssociationLabel = MergeAssociationLabels(existing.AssociationLabel, BuildContactAssociationLabel(context));
            ApplyContactMetadata(existing);
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
                LinkedContactId = NormalizeLinkedContactId(context.LinkedContactId, context.ImportId),
                IsSecondary = context.IsSecondary,
                AssociationLabel = BuildContactAssociationLabel(context)
            };

            _contacts[context.ImportId] = record;
            ApplyContactMetadata(record);
        }
    }

    private void ReconcilePrimaryState(ContactRecord existing, ContactContext incoming)
    {
        if (!existing.IsSecondary && !incoming.IsSecondary)
        {
            if (MatchesIdentity(existing, incoming))
            {
                incoming.LinkedContactId ??= existing.ImportId;
                return;
            }

            if (incoming.SharesMailingWithPrimary)
            {
                incoming.LinkedContactId ??= existing.ImportId;
                return;
            }

            LogPrimaryConflict(existing, incoming);
            incoming.LinkedContactId ??= existing.ImportId;
            return;
        }

        if (existing.IsSecondary && !incoming.IsSecondary)
        {
            existing.IsSecondary = false;
            return;
        }

        if (!existing.IsSecondary && incoming.IsSecondary)
        {
            return;
        }

        existing.IsSecondary = true;
    }

    private static bool MatchesIdentity(ContactRecord record, ContactContext context)
    {
        bool EqualsOrdinal(string? left, string? right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var namesMatch = !string.IsNullOrWhiteSpace(record.FirstName) && !string.IsNullOrWhiteSpace(record.LastName)
            && EqualsOrdinal(record.FirstName, context.FirstName)
            && EqualsOrdinal(record.LastName, context.LastName);

        var emailMatch = !string.IsNullOrWhiteSpace(record.Email) && EqualsOrdinal(record.Email, context.Email);
        var companyMatch = !string.IsNullOrWhiteSpace(record.Company) && EqualsOrdinal(record.Company, context.Company);

        if (namesMatch)
            return true;

        if (!string.IsNullOrWhiteSpace(record.Email) || !string.IsNullOrWhiteSpace(context.Email))
            return emailMatch;

        if (!string.IsNullOrWhiteSpace(record.Company) || !string.IsNullOrWhiteSpace(context.Company))
            return companyMatch;

        return false;
    }

    private void LogPrimaryConflict(ContactRecord existing, ContactContext incoming)
    {
        var existingLabel = string.IsNullOrWhiteSpace(existing.AssociationLabel) ? "Primary" : existing.AssociationLabel;
        var incomingLabel = string.IsNullOrWhiteSpace(incoming.Association) ? "Primary" : incoming.Association;
        var message = $"Primary contact conflict detected for Import ID {existing.ImportId}. Kept '{existingLabel}' as primary and demoted '{incomingLabel}' (Import ID {incoming.ImportId}) to secondary.";
        ReportProgress(message, 0, ProcessingProgressSeverity.Warning);
    }

    private void PersistProperties(ContactContext context, ContactContext primaryContext)
    {
        var associationLabel = DeterminePropertyAssociationLabel(context, primaryContext);

        if (!context.IsSecondary && context.Property.HasCoreAddress)
        {
            PersistPropertySnapshot(context.ImportId, context.Property, associationLabel, context.IsSecondary);
        }

        if (!context.IsSecondary && context.PropertyGroups.Count > 0)
        {
            foreach (var pair in context.PropertyGroups)
            {
                if (string.IsNullOrEmpty(pair.Key))
                {
                    continue;
                }

                var snapshot = pair.Value;
                var hasAdditional = snapshot.AdditionalFields.Any(kvp => !string.IsNullOrWhiteSpace(kvp.Value));
                if (!snapshot.HasCoreAddress && !hasAdditional)
                {
                    continue;
                }

                PersistPropertySnapshot(context.ImportId, snapshot, associationLabel, context.IsSecondary);
            }
        }

        var mailingSnapshot = context.Mailing.HasCoreAddress ? context.Mailing : context.Property;
        if (mailingSnapshot.HasCoreAddress)
        {
            PersistPropertySnapshot(context.ImportId, mailingSnapshot, MailingAssociationLabel, context.IsSecondary);
        }
    }
    private bool ShouldShareMailingWithPrimary(ContactContext context, ContactContext primaryContext)
    {
        if (primaryContext == null || ReferenceEquals(context, primaryContext))
        {
            return false;
        }

        if (!HasSameSurname(context, primaryContext))
        {
            return false;
        }

        var primaryMailing = primaryContext.Mailing;
        if (!primaryMailing.HasCoreAddress)
        {
            return false;
        }

        if (!context.Mailing.HasCoreAddress)
        {
            return true;
        }

        if (context.Mailing.EqualsCore(primaryMailing))
        {
            return true;
        }

        if (primaryContext.Property.HasCoreAddress && context.Mailing.EqualsCore(primaryContext.Property))
        {
            return true;
        }

        return false;
    }

    private void LogHouseholdAssignments(ContactContext primaryContext, List<ContactContext> contexts)
    {
        if (primaryContext == null || contexts.Count <= 1)
        {
            return;
        }

        var ownerCandidates = contexts
            .Where(c => !ReferenceEquals(c, primaryContext) && !c.IsSecondary)
            .ToList();

        if (ownerCandidates.Count == 0)
        {
            return;
        }

        var sharedMailing = ownerCandidates
            .Where(c => c.SharesMailingWithPrimary)
            .ToList();

        if (sharedMailing.Count > 0)
        {
            var message = $"Shared mailing address applied for {FormatContactSummary(primaryContext)} with {string.Join(", ", sharedMailing.Select(FormatContactSummary))}. Primary keeps Owner; Mailing Address and household members are marked Mailing Address (expected).";
            ReportProgress(message, 0, ProcessingProgressSeverity.Info);
        }

        var missingSurname = ownerCandidates
            .Where(c => !c.SharesMailingWithPrimary
                && (string.IsNullOrWhiteSpace(c.LastName) || string.IsNullOrWhiteSpace(primaryContext.LastName)))
            .ToList();

        if (missingSurname.Count > 0)
        {
            var message = $"Additional owners associated but missing surname data for {string.Join(", ", missingSurname.Select(FormatContactSummary))}. Applied profile mappings without mailing label.";
            ReportProgress(message, 0, ProcessingProgressSeverity.Info);
        }

        var differentSurname = ownerCandidates
            .Where(c => !c.SharesMailingWithPrimary
                && !string.IsNullOrWhiteSpace(c.LastName)
                && !string.IsNullOrWhiteSpace(primaryContext.LastName)
                && !HasSameSurname(c, primaryContext))
            .ToList();

        if (differentSurname.Count > 0)
        {
            var message = $"Multiple owners with different surnames linked to property {primaryContext.ImportId}. Primary {FormatContactSummary(primaryContext)} retains Owner association; secondary owners {string.Join(", ", differentSurname.Select(FormatContactSummary))} associated without mailing labels (expected).";
            ReportProgress(message, 0, ProcessingProgressSeverity.Info);
        }
    }

    private static string FormatContactSummary(ContactContext context)
    {
        var nameParts = new[] { context.FirstName, context.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        var name = nameParts.Count > 0 ? string.Join(" ", nameParts) : $"Owner {context.OwnerIndex}";
        return $"{name} (Import ID {context.ImportId})";
    }

    private static string BuildContactAssociationLabel(ContactContext context)
    {
        if (context.IsPrimary)
        {
            return string.Empty;
        }

        return NormalizeAssociation(context.Association);
    }

    private void ProcessPhoneNumbers(Dictionary<string, object> row, List<ContactContext> contexts)
    {
        if (contexts.Count == 0)
            return;

        var phoneMappings = GetMappingsByObjectType(MappingObjectTypes.PhoneNumber)
            .Where(m => !string.IsNullOrWhiteSpace(m.HubSpotProperty))
            .ToList();

        if (phoneMappings.Count == 0)
            return;

        var primaryContext = DeterminePrimaryContext(contexts) ?? contexts[0];
        var slotBuilders = new Dictionary<string, PhoneSlotBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in phoneMappings)
        {
            var owner = ResolvePhoneOwner(mapping, contexts) ?? primaryContext;
            if (owner == null || string.IsNullOrWhiteSpace(owner.ImportId))
                continue;

            var slotKey = BuildPhoneSlotKey(owner, mapping);
            if (string.IsNullOrWhiteSpace(slotKey))
                continue;

            if (!slotBuilders.TryGetValue(slotKey, out var builder))
            {
                builder = new PhoneSlotBuilder(owner);
                slotBuilders[slotKey] = builder;
            }

            var value = GetValueFromMapping(row, mapping);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var hubSpotProperty = mapping.HubSpotProperty?.Trim() ?? string.Empty;

            if (IsPhoneNumberField(hubSpotProperty))
            {
                var cleaned = CleanPhoneNumber(value);
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 10)
                    continue;

                builder.PhoneNumber = FormatPhoneNumber(cleaned);
                continue;
            }

            var trimmedValue = NormalizeWhitespace(value);
            if (string.IsNullOrWhiteSpace(trimmedValue))
            {
                continue;
            }

            builder.SetAdditionalField(hubSpotProperty, trimmedValue);
            RegisterAdditionalPhoneField(hubSpotProperty);
        }

        foreach (var builder in slotBuilders.Values)
        {
            if (string.IsNullOrWhiteSpace(builder.PhoneNumber))
                continue;

            AddPhoneRecord(builder.Owner, builder.PhoneNumber, builder.AdditionalFields);
        }
    }

    private void AddPhoneRecord(ContactContext context, string phoneNumber, IReadOnlyDictionary<string, string> additionalFields)
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
            MergePhoneAdditionalFields(existingRecord, additionalFields);
            ApplyPhoneMetadata(existingRecord);
            return;
        }

        var record = new PhoneRecord
        {
            ImportId = importId,
            PhoneNumber = phoneNumber,
            IsSecondary = context.IsSecondary,
            AdditionalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var kvp in additionalFields)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                continue;

            record.AdditionalFields[kvp.Key] = kvp.Value;
        }

        existing.Add(record);

        ApplyPhoneMetadata(record);
    }

    private static void MergePhoneAdditionalFields(PhoneRecord record, IReadOnlyDictionary<string, string> additionalFields)
    {
        if (additionalFields == null || additionalFields.Count == 0)
            return;

        foreach (var kvp in additionalFields)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                continue;

            record.AdditionalFields[kvp.Key] = kvp.Value;
        }
    }

    private string BuildPhoneSlotKey(ContactContext owner, FieldMapping mapping)
    {
        var identifier = ExtractPhoneSlotIdentifier(mapping);
        if (string.IsNullOrWhiteSpace(identifier))
            return string.Empty;

        return $"{owner.ImportId}|{identifier}";
    }

    private static string ExtractPhoneSlotIdentifier(FieldMapping mapping)
    {
        var identifier = ExtractPhoneIdentifier(mapping.SourceColumn);
        if (!string.IsNullOrWhiteSpace(identifier))
            return identifier;

        identifier = ExtractPhoneIdentifier(mapping.HubSpotProperty);
        return string.IsNullOrWhiteSpace(identifier) ? string.Empty : identifier;
    }

    private static string ExtractPhoneIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var match = PhoneIndexRegex.Match(value);
        if (match.Success)
            return match.Value;

        var sanitized = RemovePhoneQualifiers(value);
        return string.IsNullOrWhiteSpace(sanitized) ? string.Empty : sanitized;
    }

    private static string RemovePhoneQualifiers(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetter(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        var collapsed = builder.ToString();
        collapsed = PhoneQualifierRegex.Replace(collapsed, string.Empty);

        return collapsed.Trim();
    }

    private ContactContext? ResolvePhoneOwner(FieldMapping mapping, List<ContactContext> contexts)
    {
        var association = ResolveAssociation(mapping.AssociationType);
        if (!string.IsNullOrWhiteSpace(association))
        {
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
    private PropertySnapshot BuildPropertySnapshot(Dictionary<string, object> row, string? association, string propertyGroup = "")
    {
        var normalizedAssociation = ResolveAssociation(association);
        var normalizedGroup = NormalizePropertyGroup(propertyGroup);
        var isMailing = string.Equals(normalizedAssociation, MailingAssociationLabel, StringComparison.OrdinalIgnoreCase);

        var address = string.Empty;
        var city = string.Empty;
        var state = string.Empty;
        var zip = string.Empty;
        var county = string.Empty;
        var propertyType = string.Empty;
        var propertyValue = string.Empty;

        var additionalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var propertyMappings = GetMappingsByObjectType(MappingObjectTypes.Property)
            .Where(m => string.Equals(NormalizePropertyGroup(m.PropertyGroup), normalizedGroup, StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.IsNullOrWhiteSpace(m.HubSpotProperty))
            .ToList();

        if (propertyMappings.Count == 0)
        {
            return new PropertySnapshot(address, city, state, zip, county, propertyType, propertyValue, additionalFields, normalizedGroup);
        }

        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in propertyMappings)
        {
            var propertyName = mapping.HubSpotProperty?.Trim();
            if (string.IsNullOrWhiteSpace(propertyName) || !seenProperties.Add(propertyName))
            {
                continue;
            }

            var rawValue = GetMappedValue(row, normalizedAssociation, propertyName, MappingObjectTypes.Property, normalizedGroup);
            var value = rawValue ?? string.Empty;

            var coreKey = GetCorePropertyKey(propertyName);
            if (isMailing && coreKey is not null && !MailingCoreFields.Contains(coreKey))
            {
                continue;
            }

            if (coreKey is not null && TryAssignCoreProperty(propertyName, value, ref address, ref city, ref state, ref zip, ref county, ref propertyType, ref propertyValue))
            {
                continue;
            }

            if (isMailing)
            {
                continue;
            }

            var trimmedValue = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmedValue))
            {
                continue;
            }

            RegisterAdditionalPropertyField(propertyName);
            additionalFields[propertyName] = trimmedValue;
        }

        return new PropertySnapshot(address, city, state, zip, county, propertyType, propertyValue, additionalFields, normalizedGroup);
    }

    private List<string> GetPropertyGroupKeys(string? association)
    {
        var normalizedAssociation = ResolveAssociation(association);

        var groups = GetMappingsByObjectType(MappingObjectTypes.Property)
            .Where(m => string.Equals(ResolveAssociation(m.AssociationType), normalizedAssociation, StringComparison.OrdinalIgnoreCase))
            .Select(m => NormalizePropertyGroup(m.PropertyGroup))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrEmpty(group))
            .ToList();

        if (!groups.Contains(string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            groups.Insert(0, string.Empty);
        }

        return groups;
    }

    private void PersistPropertySnapshot(string importId, PropertySnapshot snapshot, string associationLabel, bool isSecondary)
    {
        var effectiveSnapshot = string.Equals(associationLabel, MailingAssociationLabel, StringComparison.OrdinalIgnoreCase)
            ? snapshot.ForMailingOnly()
            : snapshot;

        if (!effectiveSnapshot.HasCoreAddress)
        {
            var hasAdditionalData = effectiveSnapshot.AdditionalFields.Any(kvp => !string.IsNullOrWhiteSpace(kvp.Value));
            if (!hasAdditionalData)
            {
                return;
            }
        }

        var key = BuildPropertyKey(importId, effectiveSnapshot);
        var groupLabel = FormatPropertyGroup(effectiveSnapshot.PropertyGroup, associationLabel);
        if (!TryGetExistingPropertyRecord(importId, effectiveSnapshot, key, associationLabel, out var existingKey, out var existing))
        {
            var record = effectiveSnapshot.ToPropertyRecord(importId, associationLabel, isSecondary);
            record.PropertyGroup = groupLabel;
            foreach (var fieldName in record.AdditionalFields.Keys)
            {
                RegisterAdditionalPropertyField(fieldName);
            }

            _properties[key] = record;
            ApplyPropertyMetadata(record);
            return;
        }

        var target = existing ?? _properties[existingKey];

        if (!string.Equals(existingKey, key, StringComparison.Ordinal) && existing != null)
        {
            _properties.Remove(existingKey);
            _properties[key] = target;
        }

        if (string.IsNullOrWhiteSpace(target.PropertyGroup))
        {
            target.PropertyGroup = groupLabel;
        }
        else if (string.Equals(target.PropertyGroup, ProfileDefaultPropertyGroupLabel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(groupLabel, ProfileDefaultPropertyGroupLabel, StringComparison.OrdinalIgnoreCase))
        {
            target.PropertyGroup = groupLabel;
        }

        target.AssociationLabel = MergeAssociationLabels(target.AssociationLabel, associationLabel);
        target.IsSecondary = target.IsSecondary || isSecondary;

        if (string.IsNullOrWhiteSpace(target.Address))
            target.Address = effectiveSnapshot.Address;
        if (string.IsNullOrWhiteSpace(target.City))
            target.City = effectiveSnapshot.City;
        if (string.IsNullOrWhiteSpace(target.State))
            target.State = effectiveSnapshot.State;
        if (string.IsNullOrWhiteSpace(target.Zip))
            target.Zip = effectiveSnapshot.Zip;
        if (string.IsNullOrWhiteSpace(target.County))
            target.County = effectiveSnapshot.County;
        if (string.IsNullOrWhiteSpace(target.PropertyType))
            target.PropertyType = effectiveSnapshot.PropertyType;
        if (string.IsNullOrWhiteSpace(target.PropertyValue))
            target.PropertyValue = effectiveSnapshot.PropertyValue;

        foreach (var pair in effectiveSnapshot.AdditionalFields)
        {
            RegisterAdditionalPropertyField(pair.Key);

            if (target.AdditionalFields.TryGetValue(pair.Key, out var existingValue))
            {
                if (string.IsNullOrWhiteSpace(existingValue) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    target.AdditionalFields[pair.Key] = pair.Value;
                }
            }
            else
            {
                target.AdditionalFields[pair.Key] = pair.Value;
            }
        }
        UpdatePropertyDedupeKeys(target);
        ApplyPropertyMetadata(target);
    }

    private bool TryGetExistingPropertyRecord(string importId, PropertySnapshot snapshot, string key, string associationLabel, out string existingKey, out PropertyRecord? record)
    {
        if (_properties.TryGetValue(key, out var exactMatch))
        {
            existingKey = key;
            record = exactMatch;
            return true;
        }

        var snapshotGroupLabel = FormatPropertyGroup(snapshot.PropertyGroup, associationLabel);

        foreach (var entry in _properties)
        {
            var candidate = entry.Value;

            if (!string.Equals(candidate.ImportId, importId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(candidate.PropertyGroup, snapshotGroupLabel, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!CoreLocationMatches(snapshot, candidate))
                continue;

            existingKey = entry.Key;
            record = candidate;
            return true;
        }

        existingKey = string.Empty;
        record = null;
        return false;
    }

    private static string ResolveLegacyDefaultAssociationLabel(Profile profile)
    {
        if (profile == null)
        {
            return string.Empty;
        }

        foreach (var candidate in EnumerateAssociationCandidates(profile))
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateAssociationCandidates(Profile profile)
    {
        if (profile.PropertyMappings != null)
        {
            foreach (var mapping in profile.PropertyMappings)
            {
                yield return mapping.AssociationType;
            }
        }

        if (profile.ContactMappings != null)
        {
            foreach (var mapping in profile.ContactMappings)
            {
                yield return mapping.AssociationType;
            }
        }
    }

    private string ResolveAssociation(string? association)
    {
        var normalized = NormalizeAssociation(association);

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return _defaultAssociationLabel;
    }

    private static string NormalizeAssociation(string? association)
    {
        return string.IsNullOrWhiteSpace(association) ? string.Empty : association.Trim();
    }

    private static string NormalizePropertyGroup(string? propertyGroup)
    {
        return string.IsNullOrWhiteSpace(propertyGroup) ? string.Empty : propertyGroup.Trim();
    }
    private string FormatPropertyGroup(string propertyGroup, string associationLabel)
    {
        if (!string.IsNullOrWhiteSpace(propertyGroup))
        {
            return propertyGroup.Trim();
        }

        if (string.Equals(associationLabel, MailingAssociationLabel, StringComparison.OrdinalIgnoreCase))
        {
            return MailingAssociationLabel;
        }

        return ProfileDefaultPropertyGroupLabel;
    }



    private static bool CoreLocationMatches(PropertySnapshot snapshot, PropertyRecord record)
    {
        return CoreFieldMatches(snapshot.Address, record.Address)
            && CoreFieldMatches(snapshot.City, record.City)
            && CoreFieldMatches(snapshot.State, record.State)
            && CoreFieldMatches(snapshot.Zip, record.Zip);
    }

    private static bool CoreFieldMatches(string snapshotValue, string recordValue)
    {
        var normalizedSnapshot = NormalizeKeyPart(snapshotValue);
        var normalizedRecord = NormalizeKeyPart(recordValue);

        if (string.IsNullOrEmpty(normalizedSnapshot) || string.IsNullOrEmpty(normalizedRecord))
            return true;

        return string.Equals(normalizedSnapshot, normalizedRecord, StringComparison.Ordinal);
    }


    private void InitializeAdditionalPhoneFieldRegistry()
    {
        _additionalPhoneFieldSet.Clear();
        _additionalPhoneFieldOrder.Clear();
    }

    private void RegisterAdditionalPhoneField(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return;

        var trimmed = fieldName.Trim();
        if (IsCorePhoneField(trimmed))
            return;

        if (_additionalPhoneFieldSet.Add(trimmed))
        {
            _additionalPhoneFieldOrder.Add(trimmed);
        }
    }

    private static bool IsCorePhoneField(string fieldName)
    {
        return string.Equals(fieldName, "Phone Number", StringComparison.OrdinalIgnoreCase);
    }
    private void InitializeAdditionalPropertyFieldRegistry()
    {
        _additionalPropertyFieldSet.Clear();
        _additionalPropertyFieldOrder.Clear();
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
                    city = CleanCity(normalizedInput);
                }
                break;
            case "State":
                if (hasValue || string.IsNullOrWhiteSpace(state))
                {
                    state = CleanState(normalizedInput);
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
                    county = CleanCounty(normalizedInput);
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
            NormalizeKeyPart(snapshot.PropertyGroup),
            NormalizeKeyPart(snapshot.Address),
            NormalizeKeyPart(snapshot.City),
            NormalizeKeyPart(snapshot.State),
            NormalizeKeyPart(snapshot.Zip)
        });
    }

    private string DeterminePropertyAssociationLabel(ContactContext context, ContactContext primaryContext)
    {
        var contactLabel = BuildContactAssociationLabel(context);
        if (!string.IsNullOrWhiteSpace(contactLabel))
        {
            return contactLabel;
        }

        if (!ReferenceEquals(context, primaryContext) && HasSameSurname(context, primaryContext))
        {
            return MailingAssociationLabel;
        }

        if (!string.IsNullOrWhiteSpace(context.Association))
        {
            return context.Association.Trim();
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

    private IEnumerable<FieldMapping> GetMappings(string? associationType, string hubSpotProperty, string? objectType = null, string? propertyGroup = null)
    {
        var normalizedAssociation = NormalizeAssociation(associationType);
        if (string.IsNullOrWhiteSpace(normalizedAssociation))
        {
            normalizedAssociation = _defaultAssociationLabel;
        }

        var candidates = EnumerateResolvedMappings()
            .Where(pair => string.Equals(pair.Mapping.HubSpotProperty, hubSpotProperty, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(objectType))
        {
            candidates = candidates.Where(pair => string.Equals(pair.ObjectType, objectType, StringComparison.OrdinalIgnoreCase));
        }

        if (propertyGroup != null)
        {
            var normalizedGroup = NormalizePropertyGroup(propertyGroup);
            candidates = candidates.Where(pair => string.Equals(NormalizePropertyGroup(pair.Mapping.PropertyGroup), normalizedGroup, StringComparison.OrdinalIgnoreCase));
        }

        var candidateList = candidates.ToList();

        var associationMatches = candidateList
            .Where(pair => string.Equals(ResolveAssociation(pair.Mapping.AssociationType), normalizedAssociation, StringComparison.OrdinalIgnoreCase))
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

    private string GetMappedValue(Dictionary<string, object> row, string? associationType, string hubSpotProperty, string? objectType = null, string? propertyGroup = null)
    {
        if (string.IsNullOrWhiteSpace(hubSpotProperty))
            return string.Empty;

        string? fallback = null;

        foreach (var mapping in GetMappings(associationType, hubSpotProperty, objectType, propertyGroup))
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

    private string GetValueFromMapping(Dictionary<string, object> row, FieldMapping mapping)
    {
        if (mapping == null || string.IsNullOrWhiteSpace(mapping.SourceColumn))
            return string.Empty;

        if (!row.TryGetValue(mapping.SourceColumn, out var rawValue) || rawValue is null)
            return string.Empty;

        var value = rawValue.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = NormalizeWhitespace(value);

        if (IsPhoneField(mapping))
        {
            return NormalizePhoneValue(mapping, value);
        }

        if (IsZipField(mapping))
        {
            return NormalizeZipValue(value);
        }

        if (ShouldTitleCase(mapping))
        {
            value = ToTitleCase(value);
        }

        return value;
    }

    private static string NormalizeWhitespace(string value)
    {
        var trimmed = value.Trim();
        return WhitespaceRegex.Replace(trimmed, " ");
    }

    private static bool IsPhoneField(FieldMapping mapping)
    {
        if (mapping is null)
        {
            return false;
        }

        var objectType = mapping.ObjectType?.Trim();
        if (string.Equals(objectType, MappingObjectTypes.Property, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hubSpotProperty = mapping.HubSpotProperty;

        if (IsPhoneNumberField(hubSpotProperty))
        {
            return true;
        }

        if (ContainsKeyword(hubSpotProperty, "phone"))
        {
            if (ContainsKeyword(hubSpotProperty, "type", "status", "tag", "tags", "label"))
            {
                return false;
            }

            return true;
        }

        var sourceColumn = mapping.SourceColumn;
        if (ContainsKeyword(sourceColumn, "phone"))
        {
            if (ContainsKeyword(sourceColumn, "type", "status", "tag", "tags", "label"))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool IsPhoneNumberField(string? hubSpotProperty)
    {
        return string.Equals(hubSpotProperty?.Trim(), "Phone Number", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsZipField(FieldMapping mapping)
    {
        return ContainsKeyword(mapping?.HubSpotProperty, "zip", "postal") ||
               ContainsKeyword(mapping?.SourceColumn, "zip", "postal");
    }

    private static bool ShouldTitleCase(FieldMapping mapping)
    {
        return ContainsKeyword(mapping?.HubSpotProperty, "name", "title") ||
               ContainsKeyword(mapping?.SourceColumn, "name", "title");
    }

    private string NormalizePhoneValue(FieldMapping mapping, string value)
    {
        var digits = NonDigitRegex.Replace(value, string.Empty);

        if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal))
        {
            digits = digits[1..];
        }

        if (digits.Length == 10)
        {
            return digits;
        }

        string message;
        var displayName = GetMappingDisplayName(mapping);

        if (digits.Length > 10)
        {
            var truncated = digits[^10..];
            message = $"Flagged phone number for '{displayName}': '{value}' -> truncated to '{truncated}' (expected 10 digits)";
            if (_flaggedPhoneMessages.Add(message))
            {
                ReportProgress(message, 0, ProcessingProgressSeverity.Warning);
            }
            return truncated;
        }

        message = $"Flagged phone number for '{displayName}': '{value}' (only {digits.Length} digits after cleaning)";
        if (_flaggedPhoneMessages.Add(message))
        {
            ReportProgress(message, 0, ProcessingProgressSeverity.Warning);
        }

        return digits;
    }

    private static string NormalizeZipValue(string value)
    {
        var digits = NonDigitRegex.Replace(value, string.Empty);
        if (digits.Length >= 5)
        {
            return digits[..5];
        }
        return digits;
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var lower = value.ToLower(CultureInfo.CurrentCulture);
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(lower);
    }

    private static bool ContainsKeyword(string? text, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords.Length == 0)
            return false;

        var normalized = text.Replace('_', ' ').ToLowerInvariant();
        foreach (var keyword in keywords)
        {
            if (normalized.Contains(keyword.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetMappingDisplayName(FieldMapping mapping)
    {
        if (!string.IsNullOrWhiteSpace(mapping.SourceColumn))
        {
            return mapping.SourceColumn;
        }

        if (!string.IsNullOrWhiteSpace(mapping.HubSpotProperty))
        {
            return mapping.HubSpotProperty;
        }

        return "Unknown";
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

    private void ApplyContactMetadata(ContactRecord record)
    {
        record.DataSource = FormatAppendValue(_activeDataSource);
        record.DataType = FormatAppendValue(_activeDataType);
        record.Tags = FormatAppendValue(_activeTag);
    }

    private void ApplyPropertyMetadata(PropertyRecord record)
    {
        record.DataSource = FormatAppendValue(_activeDataSource);
        record.DataType = FormatAppendValue(_activeDataType);
        record.Tags = FormatAppendValue(_activeTag);
    }

    private void ApplyPhoneMetadata(PhoneRecord record)
    {
        record.DataSource = FormatAppendValue(_activePhoneDataSource);
    }

    private static string NormalizeLinkedContactId(string? linkedId, string importId)
    {
        if (string.IsNullOrWhiteSpace(linkedId))
            return string.Empty;

        return string.Equals(linkedId, importId, StringComparison.OrdinalIgnoreCase) ? string.Empty : linkedId.Trim();
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

    private static string? NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return WhitespaceRegex.Replace(value.Trim(), " ");
    }

    private static string FormatAppendValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();

        return trimmed.StartsWith(";", StringComparison.Ordinal) ? trimmed : $";{trimmed}";
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

        var standardized = StandardizeOrdinals(value);
        standardized = standardized.Replace("#", string.Empty);

        standardized = DirectionRegex.Replace(standardized, match =>
        {
            var token = match.Groups[1].Value;
            return DirectionAbbreviationLookup.TryGetValue(token, out var abbreviation)
                ? abbreviation + " "
                : token + " ";
        });

        standardized = StreetAbbreviationPeriodRegex.Replace(standardized, match => match.Groups[1].Value);
        standardized = StreetTypeWordRegex.Replace(standardized, match => StreetTypeAbbreviationLookup.TryGetValue(match.Value, out var abbreviation) ? abbreviation : match.Value);

        standardized = ToHubSpotTitleCase(standardized);
        standardized = WhitespaceRegex.Replace(standardized, " ");

        return standardized.Trim();
    }

    private static string CleanZip(string value)
    {
        var digits = string.IsNullOrWhiteSpace(value) ? string.Empty : NonDigitRegex.Replace(value, string.Empty);
        if (digits.Length > 5)
        {
            digits = digits[..5];
        }

        return digits.PadLeft(5, '0');
    }

    private static string CleanCity(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = value.Replace("(", string.Empty).Replace(")", string.Empty);
        sanitized = WhitespaceRegex.Replace(sanitized, " ").Trim();

        return sanitized.Length == 0 ? string.Empty : ToHubSpotTitleCase(sanitized);
    }

    private static string CleanCounty(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = value.Replace("(", string.Empty).Replace(")", string.Empty);
        sanitized = WhitespaceRegex.Replace(sanitized, " ").Trim();

        return sanitized.Length == 0 ? string.Empty : ToHubSpotTitleCase(sanitized);
    }

    private static string CleanState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = value.Replace("(", string.Empty).Replace(")", string.Empty);
        sanitized = WhitespaceRegex.Replace(sanitized, " ").Trim();

        if (sanitized.Length == 0)
            return string.Empty;

        return StateAbbreviationLookup.TryGetValue(sanitized, out var abbreviation)
            ? abbreviation
            : sanitized.ToUpperInvariant();
    }

    private static void UpdatePropertyDedupeKeys(PropertyRecord record)
    {
        if (record is null)
            return;

        record.DedupeKeyAddressCityState = BuildDedupeKey(record.Address, record.City, record.State);
        record.DedupeKeyAddressZip = BuildDedupeKey(record.Address, record.Zip);
    }

    private static string BuildDedupeKey(params string[] parts)
    {
        if (parts.Length == 0)
            return string.Empty;

        var segments = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                segments.Add(trimmed);
            }
        }

        return segments.Count == 0 ? string.Empty : string.Join(" | ", segments);
    }

    private static string StandardizeOrdinals(string value)
    {
        return OrdinalRegex.Replace(value, match =>
        {
            var numberGroup = match.Groups[1].Value;
            if (!int.TryParse(numberGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return match.Value;
            }

            var lastTwoDigits = number % 100;
            var suffix = lastTwoDigits is >= 11 and <= 13
                ? "th"
                : (number % 10) switch
                {
                    1 => "st",
                    2 => "nd",
                    3 => "rd",
                    _ => "th"
                };

            return numberGroup + suffix;
        });
    }

    private static string ToHubSpotTitleCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var characters = value.ToLowerInvariant().ToCharArray();
        var startOfWord = true;

        for (var i = 0; i < characters.Length; i++)
        {
            var current = characters[i];
            if (char.IsLetterOrDigit(current))
            {
                if (startOfWord && char.IsLetter(current))
                {
                    characters[i] = char.ToUpperInvariant(current);
                }

                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }

        return new string(characters);
    }

    private static string CleanPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        return NonDigitRegex.Replace(phone, string.Empty);
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

    private async Task<string> WriteContactsExcelAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var contacts = GetContactsForExport(isSecondary).ToList();
        if (contacts.Count == 0)
        {
            return string.Empty;
        }

        return await _excelExporter.WriteContactsAsync(outputPath, fileName, contacts, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> WritePhonesExcelAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var phones = GetPhonesForExport(isSecondary).ToList();
        if (phones.Count == 0)
        {
            return string.Empty;
        }

        var activePhoneFields = GetActivePhoneFieldOrder(phones);

        return await _excelExporter.WritePhonesAsync(outputPath, fileName, phones, activePhoneFields, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> WritePropertiesExcelAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var properties = GetPropertiesForExport(isSecondary).ToList();
        if (properties.Count == 0)
        {
            return string.Empty;
        }

        var includePropertyType = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyType));
        var includePropertyValue = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyValue));
        var activePropertyFields = GetActivePropertyFieldOrder(properties);

        return await _excelExporter.WritePropertiesAsync(outputPath, fileName, properties, activePropertyFields, includePropertyType, includePropertyValue, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> WriteContactsJsonAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var contacts = GetContactsForExport(isSecondary).ToList();
        if (contacts.Count == 0)
        {
            return string.Empty;
        }

        var includeLinkedContact = _createSecondaryContacts && contacts.Any(c => !string.IsNullOrWhiteSpace(c.LinkedContactId));

        var filePath = Path.Combine(outputPath, fileName);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartArray();
            foreach (var contact in contacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("ImportId", contact.ImportId);
                writer.WriteString("FirstName", contact.FirstName);
                writer.WriteString("LastName", contact.LastName);
                writer.WriteString("Email", contact.Email);
                writer.WriteString("Company", contact.Company);
                if (includeLinkedContact && !string.IsNullOrWhiteSpace(contact.LinkedContactId))
                {
                    writer.WriteString("LinkedContactId", contact.LinkedContactId);
                }
                writer.WriteString("AssociationLabel", contact.AssociationLabel);
                writer.WriteString("DataSource", contact.DataSource);
                writer.WriteString("DataType", contact.DataType);
                writer.WriteString("Tags", contact.Tags);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing contacts JSON '{filePath}': {ex.Message}", ex);
        }
    }

    private async Task<string> WritePhonesJsonAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var phones = GetPhonesForExport(isSecondary).ToList();
        if (phones.Count == 0)
        {
            return string.Empty;
        }

        var activePhoneFields = GetActivePhoneFieldOrder(phones);
        var includeDataSource = phones.Any(phone => !string.IsNullOrWhiteSpace(phone.DataSource));
        var filePath = Path.Combine(outputPath, fileName);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartArray();
            foreach (var phone in phones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("ImportId", phone.ImportId);
                writer.WriteString("PhoneNumber", phone.PhoneNumber);
                if (includeDataSource)
                {
                    writer.WriteString("DataSource", phone.DataSource);
                }
                if (activePhoneFields.Count > 0)
                {
                    writer.WritePropertyName("AdditionalFields");
                    writer.WriteStartObject();
                    foreach (var field in activePhoneFields)
                    {
                        phone.AdditionalFields.TryGetValue(field, out var value);
                        writer.WriteString(field, value ?? string.Empty);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing phone JSON '{filePath}': {ex.Message}", ex);
        }
    }

    private async Task<string> WritePropertiesJsonAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var properties = GetPropertiesForExport(isSecondary).ToList();
        if (properties.Count == 0)
        {
            return string.Empty;
        }

        var includePropertyType = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyType));
        var includePropertyValue = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyValue));
        var activePropertyFields = GetActivePropertyFieldOrder(properties);

        var filePath = Path.Combine(outputPath, fileName);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartArray();
            foreach (var property in properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("ImportId", property.ImportId);
                writer.WriteString("Address", property.Address);
                writer.WriteString("City", property.City);
                writer.WriteString("State", property.State);
                writer.WriteString("Zip", property.Zip);
                writer.WriteString("County", property.County);
                writer.WriteString("dedupe_key_address_city_state", property.DedupeKeyAddressCityState);
                writer.WriteString("dedupe_key_address_zip", property.DedupeKeyAddressZip);
                if (includePropertyType)
                {
                    writer.WriteString("PropertyType", property.PropertyType ?? string.Empty);
                }
                if (includePropertyValue)
                {
                    writer.WriteString("PropertyValue", property.PropertyValue ?? string.Empty);
                }
                writer.WriteString("PropertyGroup", property.PropertyGroup);
                writer.WriteString("AssociationLabel", property.AssociationLabel);
                writer.WriteString("DataSource", property.DataSource);
                writer.WriteString("DataType", property.DataType);
                writer.WriteString("Tags", property.Tags);
                if (activePropertyFields.Count > 0)
                {
                    writer.WritePropertyName("AdditionalFields");
                    writer.WriteStartObject();
                    foreach (var field in activePropertyFields)
                    {
                        property.AdditionalFields.TryGetValue(field, out var value);
                        writer.WriteString(field, value ?? string.Empty);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing property JSON '{filePath}': {ex.Message}", ex);
        }
    }

    private static async Task WriteFailureLogAsync(string outputPath, Exception exception, string stage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                return;

            Directory.CreateDirectory(outputPath);
            var logPath = Path.Combine(outputPath, "processing-error.log");
            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:O}] Stage: {stage}");
            builder.AppendLine($"Exception: {exception.GetType().FullName}");
            builder.AppendLine($"Message: {exception.Message}");
            if (exception.InnerException != null)
            {
                builder.AppendLine($"Inner: {exception.InnerException.GetType().FullName} - {exception.InnerException.Message}");
            }
            builder.AppendLine("Stack Trace:");
            builder.AppendLine(exception.StackTrace ?? "<none>");

            await File.WriteAllTextAsync(logPath, builder.ToString()).ConfigureAwait(false);
        }
        catch
        {
            // Swallow logging failures; primary exception path already reported.
        }
    }

    private async Task<string> WriteSummaryReportAsync(string outputPath, int processedRows, int primaryContacts, int secondaryContacts, int primaryPhones, int secondaryPhones, int primaryProperties, int secondaryProperties)
    {
        var summary = new
        {
            ProcessedRows = processedRows,
            Contacts = new { Primary = primaryContacts, Secondary = secondaryContacts },
            Phones = new { Primary = primaryPhones, Secondary = secondaryPhones },
            Properties = new { Primary = primaryProperties, Secondary = secondaryProperties },
            GeneratedAt = DateTime.UtcNow
        };

        var filePath = Path.Combine(outputPath, "processing-summary.json");
        try
        {
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing summary report '{filePath}': {ex.Message}", ex);
        }
    }

    private IEnumerable<ContactRecord> GetContactsForExport(bool isSecondary)
    {
        return _contacts.Values
            .Where(c => c.IsSecondary == isSecondary)
            .OrderBy(c => c.LastName)
            .ThenBy(c => c.FirstName);
    }

    private IEnumerable<PhoneRecord> GetPhonesForExport(bool isSecondary)
    {
        return _phones.SelectMany(kvp => kvp.Value)
            .Where(p => p.IsSecondary == isSecondary)
            .OrderBy(p => p.ImportId);
    }

    private List<string> GetActivePhoneFieldOrder(IEnumerable<PhoneRecord> phones)
    {
        return _additionalPhoneFieldOrder
            .Where(field => phones.Any(phone => phone.AdditionalFields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value)))
            .ToList();
    }

    private IEnumerable<PropertyRecord> GetPropertiesForExport(bool isSecondary)
    {
        return _properties.Values
            .Where(p => p.IsSecondary == isSecondary)
            .OrderBy(p => p.Address)
            .ThenBy(p => p.ImportId);
    }

    private List<string> GetActivePropertyFieldOrder(IEnumerable<PropertyRecord> properties)
    {
        return _additionalPropertyFieldOrder
            .Where(field => properties.Any(property => property.AdditionalFields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value)))
            .ToList();
    }
    private async Task<string> WriteContactsFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var contacts = GetContactsForExport(isSecondary).ToList();
        if (contacts.Count == 0)
        {
            return string.Empty;
        }

        var includeLinkedContact = _createSecondaryContacts && contacts.Any(c => !string.IsNullOrWhiteSpace(c.LinkedContactId));
        var includeAssociationLabel = contacts.Any(c => !string.IsNullOrWhiteSpace(c.AssociationLabel));

        var filePath = Path.Combine(outputPath, fileName);

        try
        {
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
            if (includeLinkedContact)
            {
                csv.WriteField("Linked Contact ID");
            }
            if (includeAssociationLabel)
            {
                csv.WriteField("Association Label");
            }
            csv.WriteField("Data Source");
            csv.WriteField("Data Type");
            csv.WriteField("Tags");
            await csv.NextRecordAsync();

            foreach (var contact in contacts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                csv.WriteField(contact.ImportId);
                csv.WriteField(contact.FirstName);
                csv.WriteField(contact.LastName);
                csv.WriteField(contact.Email);
                csv.WriteField(contact.Company);
                if (includeLinkedContact)
                {
                    csv.WriteField(string.IsNullOrWhiteSpace(contact.LinkedContactId) ? string.Empty : contact.LinkedContactId);
                }
                if (includeAssociationLabel)
                {
                    csv.WriteField(contact.AssociationLabel);
                }
                csv.WriteField(contact.DataSource);
                csv.WriteField(contact.DataType);
                csv.WriteField(contact.Tags);
                await csv.NextRecordAsync();
            }

            await writer.FlushAsync();
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing contacts CSV '{filePath}': {ex.Message}", ex);
        }
    }

    private async Task<string> WritePhonesFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var phones = GetPhonesForExport(isSecondary).ToList();
        if (phones.Count == 0)
        {
            return string.Empty;
        }

        var activePhoneFields = GetActivePhoneFieldOrder(phones);
        var includeDataSource = phones.Any(phone => !string.IsNullOrWhiteSpace(phone.DataSource));
        var filePath = Path.Combine(outputPath, fileName);

        try
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true
            });

            csv.WriteField("Import ID");
            csv.WriteField("Phone Number");
            foreach (var field in activePhoneFields)
            {
                csv.WriteField(field);
            }
            if (includeDataSource)
            {
                csv.WriteField("Data Source");
            }
            await csv.NextRecordAsync();

            foreach (var phone in phones)
            {
                cancellationToken.ThrowIfCancellationRequested();

                csv.WriteField(phone.ImportId);
                csv.WriteField(phone.PhoneNumber);
                foreach (var field in activePhoneFields)
                {
                    phone.AdditionalFields.TryGetValue(field, out var value);
                    csv.WriteField(value ?? string.Empty);
                }
                if (includeDataSource)
                {
                    csv.WriteField(phone.DataSource);
                }
                await csv.NextRecordAsync();
            }

            await writer.FlushAsync();
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing phone CSV '{filePath}': {ex.Message}", ex);
        }
    }

    private async Task<string> WritePropertiesFileAsync(string outputPath, string fileName, bool isSecondary, CancellationToken cancellationToken)
    {
        var properties = GetPropertiesForExport(isSecondary).ToList();
        if (properties.Count == 0)
        {
            return string.Empty;
        }

        var includePropertyType = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyType));
        var includePropertyValue = properties.Any(p => !string.IsNullOrWhiteSpace(p.PropertyValue));
        var activePropertyFields = GetActivePropertyFieldOrder(properties);

        var filePath = Path.Combine(outputPath, fileName);

        try
        {
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
            csv.WriteField("dedupe_key_address_city_state");
            csv.WriteField("dedupe_key_address_zip");
            if (includePropertyType)
            {
                csv.WriteField("Property Type");
            }
            if (includePropertyValue)
            {
                csv.WriteField("Property Value");
            }

            foreach (var field in activePropertyFields)
            {
                csv.WriteField(field);
            }

            csv.WriteField("Property Group");
            csv.WriteField("Association Label");
            csv.WriteField("Data Source");
            csv.WriteField("Data Type");
            csv.WriteField("Tags");
            await csv.NextRecordAsync();

            foreach (var property in properties)
            {
                cancellationToken.ThrowIfCancellationRequested();

                csv.WriteField(property.ImportId);
                csv.WriteField(property.Address);
                csv.WriteField(property.City);
                csv.WriteField(property.State);
                csv.WriteField(property.Zip);
                csv.WriteField(property.County);
                csv.WriteField(property.DedupeKeyAddressCityState);
                csv.WriteField(property.DedupeKeyAddressZip);
                if (includePropertyType)
                {
                    csv.WriteField(property.PropertyType);
                }
                if (includePropertyValue)
                {
                    csv.WriteField(property.PropertyValue);
                }

                foreach (var field in activePropertyFields)
                {
                    property.AdditionalFields.TryGetValue(field, out var value);
                    csv.WriteField(value ?? string.Empty);
                }

                csv.WriteField(property.PropertyGroup);
                csv.WriteField(property.AssociationLabel);
                csv.WriteField(property.DataSource);
                csv.WriteField(property.DataType);
                csv.WriteField(property.Tags);
                await csv.NextRecordAsync();
            }

            await writer.FlushAsync();
            return filePath;
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed writing property CSV '{filePath}': {ex.Message}", ex);
        }
    }


    private void ReportProgress(string message, int percentComplete, ProcessingProgressSeverity severity = ProcessingProgressSeverity.Info)
    {
        _progress?.Report(new ProcessingProgress
        {
            Message = message,
            PercentComplete = percentComplete,
            Timestamp = DateTime.Now,
            Severity = severity
        });
    }

    private sealed class PhoneSlotBuilder
    {
        public PhoneSlotBuilder(ContactContext owner)
        {
            Owner = owner;
        }

        public ContactContext Owner { get; }
        public string PhoneNumber { get; set; } = string.Empty;
        public Dictionary<string, string> AdditionalFields { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SetAdditionalField(string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(value))
                return;

            AdditionalFields[fieldName.Trim()] = value.Trim();
        }
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
        public bool SharesMailingWithPrimary { get; set; }
        public string? LinkedContactId { get; set; }
        public string ImportId { get; set; } = string.Empty;
        public PropertySnapshot Property { get; set; } = PropertySnapshot.Empty;
        public PropertySnapshot Mailing { get; set; } = PropertySnapshot.Empty;
        public Dictionary<string, PropertySnapshot> PropertyGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AdditionalContactFields { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasContactData => !string.IsNullOrWhiteSpace(FirstName) || !string.IsNullOrWhiteSpace(LastName) || !string.IsNullOrWhiteSpace(Email) || !string.IsNullOrWhiteSpace(Company);
    }

    private sealed record PropertySnapshot
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyAdditionalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PropertySnapshot(string address, string city, string state, string zip, string county, string propertyType, string propertyValue, IReadOnlyDictionary<string, string>? additionalFields = null, string propertyGroup = "")
        {
            Address = address;
            City = city;
            State = state;
            Zip = zip;
            County = county;
            PropertyType = propertyType;
            PropertyValue = propertyValue;
            AdditionalFields = additionalFields ?? EmptyAdditionalFields;
            PropertyGroup = propertyGroup ?? string.Empty;
        }

        public static PropertySnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, EmptyAdditionalFields, string.Empty);
        public PropertySnapshot ForMailingOnly()
        {
            return new PropertySnapshot(Address, City, State, Zip, County, string.Empty, string.Empty, EmptyAdditionalFields, PropertyGroup);
        }


        public string Address { get; }
        public string City { get; }
        public string State { get; }
        public string Zip { get; }
        public string County { get; }
        public string PropertyType { get; }
        public string PropertyValue { get; }
        public IReadOnlyDictionary<string, string> AdditionalFields { get; }
        public string PropertyGroup { get; }

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
            var record = new PropertyRecord
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
                PropertyGroup = PropertyGroup,
                AdditionalFields = new Dictionary<string, string>(AdditionalFields, StringComparer.OrdinalIgnoreCase)
            };

            UpdatePropertyDedupeKeys(record);
            return record;
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
    public string DataSource { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
}


public class PhoneRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsSecondary { get; set; }
    public string DataSource { get; set; } = string.Empty;
    public Dictionary<string, string> AdditionalFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class PropertyRecord
{
    public string ImportId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty;
    public string DedupeKeyAddressCityState { get; set; } = string.Empty;
    public string DedupeKeyAddressZip { get; set; } = string.Empty;
    public string AssociationLabel { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string PropertyValue { get; set; } = string.Empty;
    public bool IsSecondary { get; set; }
    public string DataSource { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string PropertyGroup { get; set; } = string.Empty;
    public Dictionary<string, string> AdditionalFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}


public class ProcessingResult
{
    public bool Success { get; set; }
    public List<string> CsvFiles { get; set; } = new();
    public List<string> ExcelFiles { get; set; } = new();
    public List<string> JsonFiles { get; set; } = new();
    public string ContactsFile { get; set; } = string.Empty;
    public string PhonesFile { get; set; } = string.Empty;
    public string SecondaryContactsFile { get; set; } = string.Empty;
    public string SecondaryPhonesFile { get; set; } = string.Empty;
    public string SecondaryPropertiesFile { get; set; } = string.Empty;
    public string PropertiesFile { get; set; } = string.Empty;
    public string? SummaryReportPath { get; set; }
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
    public ProcessingProgressSeverity Severity { get; set; } = ProcessingProgressSeverity.Info;
}

public enum ProcessingProgressSeverity
{
    Info,
    Warning,
    Error
}








