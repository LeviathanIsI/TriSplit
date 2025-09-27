using System;

using System.Collections.Generic;

using System.Globalization;

using System.IO;

using System.Linq;

using System.Text;
using System.Text.RegularExpressions;

using System.Text.Json;

using System.Threading;

using System.Threading.Tasks;

using ClosedXML.Excel;

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

    private readonly List<string> _validationWarnings = new();

    private static readonly Regex OrdinalRegex = new("(?i)\\b(\\d+)(ST|ND|RD|TH)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DirectionRegex = new("(?i)\\b(North|South|East|West)\\s+(?!St\\.?|Street|Ave\\.?|Avenue|Dr\\.?|Drive|Blvd\\.?|Boulevard|Ln\\.?|Lane|Rd\\.?|Road|Ct\\.?|Court|Pl\\.?|Place)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StreetAbbreviationDotRegex = new("(?i)\\b(St|Ave|Dr|Blvd|Ln|Rd|Ct|Pl)\\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TitleCaseRegex = new("(?i)\\b\\w", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiSpaceRegex = new("\\s+", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DirectionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["North"] = "N",
        ["South"] = "S",
        ["East"] = "E",
        ["West"] = "W"
    };

    private static readonly Dictionary<string, string> StreetTypeMap = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly Dictionary<string, string> StateMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alabama"] = "AL", ["alaska"] = "AK", ["arizona"] = "AZ", ["arkansas"] = "AR", ["california"] = "CA",
        ["colorado"] = "CO", ["connecticut"] = "CT", ["delaware"] = "DE", ["florida"] = "FL", ["georgia"] = "GA",
        ["hawaii"] = "HI", ["idaho"] = "ID", ["illinois"] = "IL", ["indiana"] = "IN", ["iowa"] = "IA",
        ["kansas"] = "KS", ["kentucky"] = "KY", ["louisiana"] = "LA", ["maine"] = "ME", ["maryland"] = "MD",
        ["massachusetts"] = "MA", ["michigan"] = "MI", ["minnesota"] = "MN", ["mississippi"] = "MS", ["missouri"] = "MO",
        ["montana"] = "MT", ["nebraska"] = "NE", ["nevada"] = "NV", ["new hampshire"] = "NH", ["new jersey"] = "NJ",
        ["new mexico"] = "NM", ["new york"] = "NY", ["north carolina"] = "NC", ["north dakota"] = "ND", ["ohio"] = "OH",
        ["oklahoma"] = "OK", ["oregon"] = "OR", ["pennsylvania"] = "PA", ["rhode island"] = "RI", ["south carolina"] = "SC",
        ["south dakota"] = "SD", ["tennessee"] = "TN", ["texas"] = "TX", ["utah"] = "UT", ["vermont"] = "VT",
        ["virginia"] = "VA", ["washington"] = "WA", ["west virginia"] = "WV", ["wisconsin"] = "WI", ["wyoming"] = "WY",
        ["district of columbia"] = "DC", ["puerto rico"] = "PR"
    };




    public UnifiedProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)

    {

        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        _inputReader = inputReader ?? throw new ArgumentNullException(nameof(inputReader));

        _progress = progress;



        if (_profile.Mappings == null)

        {

            _profile.Mappings = new List<ProfileMapping>();

        }

    }



    public async Task<ProcessingResult> ProcessAsync(string inputFilePath, string outputDirectory, ProcessingOptions options, CancellationToken cancellationToken = default)

    {

        if (string.IsNullOrWhiteSpace(inputFilePath))

        {

            throw new ArgumentException("Input file path is required", nameof(inputFilePath));

        }



        if (string.IsNullOrWhiteSpace(outputDirectory))

        {

            throw new ArgumentException("Output directory is required", nameof(outputDirectory));

        }



        options ??= new ProcessingOptions();

        if (!options.OutputCsv && !options.OutputExcel && !options.OutputJson)

        {

            throw new InvalidOperationException("At least one output format must be selected.");

        }



        Directory.CreateDirectory(outputDirectory);



        ReportProgress("Loading input file...", 5);

        var inputData = await _inputReader.ReadAsync(inputFilePath).ConfigureAwait(false);



        var headerLookup = BuildHeaderLookup(inputData.Headers);

        var (propertyGroups, contactGroups, phoneGroups, validationIssues) = BuildMappingGroups(_profile);



        _validationWarnings.AddRange(validationIssues);



        var missingHeaders = ValidateSourceHeaders(headerLookup, propertyGroups, contactGroups, phoneGroups);

        if (missingHeaders.Count > 0 && _profile.MissingHeaderBehavior == MissingHeaderBehavior.Error)

        {

            throw new InvalidOperationException($"Missing required source columns: {string.Join(", ", missingHeaders)}");

        }



        ReportProgress("Processing rows...", 15);



        var processedData = new List<ProcessedRowData>();

        var groupCounters = InitializeGroupCounters(propertyGroups, contactGroups, phoneGroups);



        for (var rowIndex = 0; rowIndex < inputData.Rows.Count; rowIndex++)

        {

            try

            {

                cancellationToken.ThrowIfCancellationRequested();

                var row = inputData.Rows[rowIndex];

                var contactId = Guid.NewGuid().ToString();

                var rowData = ProcessRowData(row, contactId, propertyGroups, contactGroups, phoneGroups, headerLookup, groupCounters);

                if (rowData != null) processedData.Add(rowData);

            }

            catch (Exception ex)

            {

                if (_profile.MissingHeaderBehavior == MissingHeaderBehavior.Error)

                    throw new InvalidOperationException($"Row {rowIndex + 2}: {ex.Message}", ex); // +2 for header + 1-based row

                _validationWarnings.Add($"Row {rowIndex + 2}: {ex.Message}");

            }



            var percent = 15 + (int)Math.Round(70.0 * (rowIndex + 1) / Math.Max(1, inputData.Rows.Count));

            ReportProgress($"Processed {rowIndex + 1:N0} of {inputData.Rows.Count:N0} rows", percent);

        }



        // Build final output (no deduplication; HubSpot will dedupe)

        var hasContactMappings = contactGroups.Count > 0;
        var propertyRows = BuildPropertyRows(processedData, hasContactMappings);

        var contactRows = BuildContactRows(processedData, _profile.CreateSecondaryContactsFile);

        var phoneRows = BuildPhoneRows(processedData, hasContactMappings);

        var associationRows = BuildAssociationRows(processedData);



        var propertyDictionaries = propertyRows.Select(row => ToPropertyDictionary(row)).ToList();

        var contactDictionaries = contactRows.Select(row => ToContactDictionary(row, _profile.CreateSecondaryContactsFile)).ToList();

        var phoneDictionaries = phoneRows.Select(row => ToPhoneDictionary(row)).ToList();



        var result = new ProcessingResult

        {

            Success = true,

            TotalRecordsProcessed = inputData.Rows.Count,

            PropertiesCreated = propertyDictionaries.Count,

            ContactsCreated = contactDictionaries.Count,

            PhonesCreated = phoneDictionaries.Count,

            AssociationsCreated = associationRows.Count

        };



        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);



        if (options.OutputCsv)

        {

            ReportProgress("Writing CSV files...", 90);

            result.PropertiesFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"properties-{timestamp}.csv"), propertyDictionaries, ProfileObjectType.Property, propertyGroups, cancellationToken).ConfigureAwait(false);

            result.ContactsFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"contacts-{timestamp}.csv"), contactDictionaries, ProfileObjectType.Contact, contactGroups, cancellationToken).ConfigureAwait(false);

            result.PhonesFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"phones-{timestamp}.csv"), phoneDictionaries, ProfileObjectType.Phone, phoneGroups, cancellationToken).ConfigureAwait(false);

            result.AssociationsFile = await WriteAssociationCsvAsync(Path.Combine(outputDirectory, $"associations-{timestamp}.csv"), associationRows, cancellationToken).ConfigureAwait(false);



            if (!string.IsNullOrEmpty(result.PropertiesFile))

            {

                result.CsvFiles.Add(result.PropertiesFile);

            }

            if (!string.IsNullOrEmpty(result.ContactsFile))

            {

                result.CsvFiles.Add(result.ContactsFile);

            }

            if (!string.IsNullOrEmpty(result.PhonesFile))

            {

                result.CsvFiles.Add(result.PhonesFile);

            }

            if (!string.IsNullOrEmpty(result.AssociationsFile))

            {

                result.CsvFiles.Add(result.AssociationsFile);

            }

        }



        if (options.OutputExcel)

        {

            ReportProgress("Writing Excel workbooks...", 92);

            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"properties-{timestamp}.xlsx"), "Properties", propertyDictionaries, ProfileObjectType.Property, propertyGroups, cancellationToken).ConfigureAwait(false));

            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"contacts-{timestamp}.xlsx"), "Contacts", contactDictionaries, ProfileObjectType.Contact, contactGroups, cancellationToken).ConfigureAwait(false));

            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"phones-{timestamp}.xlsx"), "Phones", phoneDictionaries, ProfileObjectType.Phone, phoneGroups, cancellationToken).ConfigureAwait(false));

            var associationsExcel = await WriteAssociationExcelAsync(Path.Combine(outputDirectory, $"associations-{timestamp}.xlsx"), associationRows, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(associationsExcel))

            {

                result.AssociationsExcelFile = associationsExcel;

                result.ExcelFiles.Add(associationsExcel);

            }

            result.ExcelFiles = result.ExcelFiles.Where(path => !string.IsNullOrEmpty(path)).ToList();

        }



        if (options.OutputJson)

        {

            ReportProgress("Writing processing report...", 94);

            result.SummaryReportPath = await WriteReportAsync(Path.Combine(outputDirectory, $"processing_report-{timestamp}.json"), groupCounters, missingHeaders).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.SummaryReportPath))

            {

                result.JsonFiles.Add(result.SummaryReportPath);

            }

        }



        ReportProgress("Processing complete", 100);

        return result;

    }



    private ProcessedRowData? ProcessRowData(

        Dictionary<string, object> row,

        string contactId,

        IReadOnlyList<MappingGroup> propertyGroups,

        IReadOnlyList<MappingGroup> contactGroups,

        IReadOnlyList<MappingGroup> phoneGroups,

        IReadOnlyDictionary<string, int> headerLookup,

        Dictionary<ProfileObjectType, Dictionary<int, int>> counters)

    {

        var properties = new List<ProcessedGroup>();

        var contacts = new List<ProcessedGroup>();

        var phones = new List<ProcessedGroup>();



        // Process each group type

        ProcessGroups(row, propertyGroups, headerLookup, properties, counters);

        ProcessGroups(row, contactGroups, headerLookup, contacts, counters);

        ProcessGroups(row, phoneGroups, headerLookup, phones, counters);



        // Row–contact/property/phone linkage for this sheet

        var anyContact = contacts.Any(c => c.HasData);

        var anyPropOrPhone = properties.Any(p => p.HasData) || phones.Any(p => p.HasData);

        var contactMappingsConfigured = contactGroups.Count > 0;

        if (contactMappingsConfigured && anyPropOrPhone && !anyContact)

            throw new InvalidOperationException("Row has property/phone data but no contact data per profile mapping.");



        // Only create a row if we have actual data

        if (anyPropOrPhone || anyContact)

        {

            return new ProcessedRowData(contactId, properties, contacts, phones);

        }



        return null;

    }



    private void ProcessGroups(

        Dictionary<string, object> row,

        IReadOnlyList<MappingGroup> groups,

        IReadOnlyDictionary<string, int> headerLookup,

        List<ProcessedGroup> output,

        Dictionary<ProfileObjectType, Dictionary<int, int>> counters)

    {

        foreach (var group in groups)

        {

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var hasData = false;



            foreach (var mapping in group.Mappings)

            {

                var value = ResolveFieldValue(row, headerLookup, mapping);

                values[mapping.HubSpotHeader] = value;

                

                if (!string.IsNullOrWhiteSpace(value))

                {

                    hasData = true;

                }

            }



            if (hasData)

            {

                counters[group.ObjectType][group.GroupIndex]++;

            }



            output.Add(new ProcessedGroup(

                group.ObjectType,

                group.GroupIndex,

                group.Metadata.Associations,

                group.Metadata.DataSource,

                group.Metadata.DataType,

                group.Metadata.Tags,

                values,

                hasData));

        }

    }



    private IReadOnlyDictionary<string, int> BuildHeaderLookup(IEnumerable<string> headers)

    {

        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);

        var i = 0;

        foreach (var h in headers ?? Array.Empty<string>())

            if (!lookup.ContainsKey(h)) lookup[h] = i++;

        return lookup;

    }



    private string ResolveFieldValue(

        Dictionary<string, object> row,

        IReadOnlyDictionary<string, int> headerLookup,

        ProfileMapping mapping)

    {

        if (!headerLookup.ContainsKey(mapping.SourceField))

            return string.Empty; // preflight handles error policy



        if (!row.TryGetValue(mapping.SourceField, out var raw))

            throw new InvalidOperationException(

                $"Row is missing exact key '{mapping.SourceField}' despite header presence. " +

                "Ensure IInputReader preserves original header strings as row keys.");



        var text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;

        return ApplyTransform(text, mapping.Transform, row);

    }



    private static string ApplyTransform(string value, TransformDefinition? transform, Dictionary<string, object> row)

    {

        if (transform == null)

        {

            return value;

        }



        switch (transform.Verb)

        {

            case TransformVerb.Trim:

                return value?.Trim() ?? string.Empty;

            case TransformVerb.Upper:

                return (value ?? string.Empty).ToUpperInvariant();

            case TransformVerb.Lower:

                return (value ?? string.Empty).ToLowerInvariant();

            case TransformVerb.Zip5:

                return TakeDigits(value, 5);

            case TransformVerb.Phone10:

                var digits = TakeDigits(value, int.MaxValue);

                return digits.Length <= 10 ? digits : digits[^10..];

            case TransformVerb.Left:

                return TakeSlice(value, transform.Arguments.FirstOrDefault(), fromLeft: true);

            case TransformVerb.Right:

                return TakeSlice(value, transform.Arguments.FirstOrDefault(), fromLeft: false);

            case TransformVerb.Replace:

                if (transform.Arguments.Count >= 2)

                {

                    var search = ResolveArgument(transform.Arguments[0], row);

                    var replacement = ResolveArgument(transform.Arguments[1], row);

                    return (value ?? string.Empty).Replace(search, replacement, StringComparison.OrdinalIgnoreCase);

                }

                return value ?? string.Empty;

            case TransformVerb.Concat:

                var builder = new StringBuilder(value ?? string.Empty);

                foreach (var arg in transform.Arguments)

                {

                    builder.Append(ResolveArgument(arg, row));

                }

                return builder.ToString();

            default:

                return value ?? string.Empty;

        }

    }



    private static string ResolveArgument(string argument, Dictionary<string, object> row)

    {

        if (argument == null) return string.Empty;

        if (row.TryGetValue(argument, out var v) && v is not null)

            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;

        return argument; // literal, not a column

    }



    private static string TakeSlice(string value, string? lengthArgument, bool fromLeft)

    {

        if (!int.TryParse(lengthArgument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)

        {

            return value ?? string.Empty;

        }



        var text = value ?? string.Empty;

        if (text.Length <= length)

        {

            return text;

        }



        return fromLeft ? text.Substring(0, length) : text.Substring(text.Length - length);

    }



    private static string TakeDigits(string? value, int maxLength)

    {

        if (string.IsNullOrEmpty(value))

        {

            return string.Empty;

        }



        var digits = new StringBuilder();

        foreach (var ch in value.Where(char.IsDigit))

        {

            digits.Append(ch);

            if (digits.Length == maxLength)

            {

                break;

            }

        }



        return digits.ToString();

    }



    private static (IReadOnlyList<MappingGroup> Properties, IReadOnlyList<MappingGroup> Contacts, IReadOnlyList<MappingGroup> Phones, List<string> Warnings) BuildMappingGroups(Profile profile)

    {

        var warnings = new List<string>();



        var propertyGroups = BuildGroup(profile, ProfileObjectType.Property, warnings);

        var contactGroups = BuildGroup(profile, ProfileObjectType.Contact, warnings);

        var phoneGroups = BuildGroup(profile, ProfileObjectType.Phone, warnings);



        return (propertyGroups, contactGroups, phoneGroups, warnings);

    }



    private static IReadOnlyList<MappingGroup> BuildGroup(Profile profile, ProfileObjectType objectType, List<string> warnings)

    {

        var comparer = StringComparer.OrdinalIgnoreCase;

        var groups = profile.Mappings

            .Where(m => m.ObjectType == objectType)

            .GroupBy(m => m.GroupIndex)

            .OrderBy(g => g.Key)

            .Select(group => CreateMappingGroup(profile, objectType, group.Key, group.ToList(), warnings))

            .Where(g => g.Mappings.Count > 0)

            .ToList();



        return groups;

    }



    private static MappingGroup CreateMappingGroup(Profile profile, ProfileObjectType objectType, int groupIndex, List<ProfileMapping> mappings, List<string> warnings)

    {

        if (groupIndex <= 0)

        {

            throw new InvalidOperationException($"Group index must be greater than zero for {objectType} mappings.");

        }



        var defaults = profile.Groups.GetOrAdd(objectType, groupIndex);

        var metadata = ResolveGroupMetadata(profile, objectType, groupIndex, defaults, mappings, warnings);



        ValidateDuplicateTargets(objectType, groupIndex, mappings);



        return new MappingGroup(objectType, groupIndex, mappings, metadata);

    }



    private static GroupMetadata ResolveGroupMetadata(Profile profile, ProfileObjectType objectType, int groupIndex, GroupDefaults defaults, List<ProfileMapping> mappings, List<string> warnings)



    {



        var dataSource = defaults.DataSource ?? string.Empty;



        var dataType = defaults.DataType ?? string.Empty;



        var tags = new List<string>(defaults.Tags ?? new List<string>());







        var associationOverrides = mappings



            .Select(m => m.AssociationLabelOverride)



            .Where(v => !string.IsNullOrWhiteSpace(v))



            .Select(v => v!.Trim())



            .Distinct(StringComparer.OrdinalIgnoreCase)



            .ToList();







        if (associationOverrides.Count > 1)



        {



            warnings.Add($"Multiple association overrides defined for group {mappings.First().GroupIndex}; merging all values.");



        }







        var dataSourceOverrides = mappings



            .Select(m => m.DataSourceOverride)



            .Where(v => !string.IsNullOrWhiteSpace(v))



            .Select(v => v!.Trim())



            .Distinct(StringComparer.OrdinalIgnoreCase)



            .ToList();







        if (dataSourceOverrides.Count > 1)



        {



            warnings.Add($"Multiple data source overrides defined for group {mappings.First().GroupIndex}; using '{dataSourceOverrides[0]}'.");



        }



        if (dataSourceOverrides.Count >= 1)



        {



            dataSource = dataSourceOverrides[0];



        }







        var tagOverrides = mappings



            .Select(m => m.TagsOverride)



            .Where(t => t is { Count: > 0 })



            .ToList();







        if (tagOverrides.Count > 0)



        {



            var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);



            foreach (var overrideSet in tagOverrides)



            {



                foreach (var tag in overrideSet)



                {



                    if (!string.IsNullOrWhiteSpace(tag))



                    {



                        tagSet.Add(tag.Trim());



                    }



                }



            }



            tags = tagSet.ToList();



        }







        var associations = BuildAssociations(profile, objectType, groupIndex, defaults, associationOverrides);







        return new GroupMetadata(associations, dataSource, dataType, tags);



    }







    private static List<GroupAssociation> BuildAssociations(Profile profile, ProfileObjectType sourceType, int groupIndex, GroupDefaults defaults, IReadOnlyList<string> overrideLabels)



    {



        var sanitized = new List<GroupAssociation>();



        var overrideSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);







        if (overrideLabels != null)



        {



            foreach (var label in overrideLabels)



            {



                if (string.IsNullOrWhiteSpace(label))



                {



                    continue;



                }







                overrideSet.Add(label.Trim());



            }



        }







        if (defaults.Associations is { Count: > 0 })



        {



            foreach (var association in defaults.Associations)



            {



                if (association == null || association.TargetIndex <= 0)



                {



                    continue;



                }







                var labels = new List<string>();



                var labelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);







                if (association.Labels is { Count: > 0 })



                {



                    foreach (var label in association.Labels)



                    {



                        if (string.IsNullOrWhiteSpace(label))



                        {



                            continue;



                        }







                        var trimmed = label.Trim();



                        if (labelSet.Add(trimmed))



                        {



                            labels.Add(trimmed);



                        }



                    }



                }







                foreach (var label in overrideSet)



                {



                    if (labelSet.Add(label))



                    {



                        labels.Add(label);



                    }



                }







                if (labels.Count == 0)



                {



                    continue;



                }







                sanitized.Add(new GroupAssociation



                {



                    TargetType = association.TargetType,



                    TargetIndex = association.TargetIndex,



                    Labels = labels



                });



            }



        }







        return sanitized;



    }







    private static void ValidateDuplicateTargets(ProfileObjectType objectType, int groupIndex, List<ProfileMapping> mappings)

    {

        var duplicates = mappings

            .GroupBy(m => m.HubSpotHeader, StringComparer.OrdinalIgnoreCase)

            .Where(g => g.Count() > 1)

            .Select(g => g.Key)

            .ToList();



        if (duplicates.Count > 0)

        {

            throw new InvalidOperationException($"Duplicate HubSpot headers detected in {objectType} group {groupIndex}: {string.Join(", ", duplicates)}");

        }

    }



    private List<string> ValidateSourceHeaders(IReadOnlyDictionary<string, int> headerLookup, params IReadOnlyList<MappingGroup>[] groups)

    {

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);



        foreach (var groupSet in groups)

        {

            foreach (var group in groupSet)

            {

                foreach (var mapping in group.Mappings)

                {

                    if (string.IsNullOrWhiteSpace(mapping.SourceField))

                    {

                        throw new InvalidOperationException("Each mapping requires a source field.");

                    }



                    if (!headerLookup.ContainsKey(mapping.SourceField))

                    {

                        missing.Add(mapping.SourceField);

                    }

                }

            }

        }



        return missing.ToList();

    }



    private Dictionary<ProfileObjectType, Dictionary<int, int>> InitializeGroupCounters(params IReadOnlyList<MappingGroup>[] groups)

    {

        var counters = new Dictionary<ProfileObjectType, Dictionary<int, int>>();

        foreach (var groupSet in groups.SelectMany(g => g))

        {

            if (!counters.TryGetValue(groupSet.ObjectType, out var inner))

            {

                inner = new Dictionary<int, int>();

                counters[groupSet.ObjectType] = inner;

            }



            if (!inner.ContainsKey(groupSet.GroupIndex))

            {

                inner[groupSet.GroupIndex] = 0;

            }

        }



        return counters;

    }



    private async Task<string> WriteCsvAsync(string path, IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType, IReadOnlyList<MappingGroup> groups, CancellationToken cancellationToken)

    {

        if (rows.Count == 0)

        {

            return string.Empty;

        }



        var columns = BuildColumnOrder(rows, objectType, groups);



        try

        {

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)

            {

                HasHeaderRecord = true

            });



            foreach (var column in columns)

            {

                csv.WriteField(column);

            }

            await csv.NextRecordAsync().ConfigureAwait(false);



            foreach (var row in rows)

            {

                cancellationToken.ThrowIfCancellationRequested();

                foreach (var column in columns)

                {

                    row.TryGetValue(column, out var value);

                    csv.WriteField(value ?? string.Empty);

                }

                await csv.NextRecordAsync().ConfigureAwait(false);

            }



            await writer.FlushAsync().ConfigureAwait(false);

            return path;

        }

        catch (Exception ex)

        {

            throw new IOException($"Failed to write CSV '{path}': {ex.Message}", ex);

        }

    }



    private async Task<string> WriteExcelAsync(string path, string sheetName, IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType, IReadOnlyList<MappingGroup> groups, CancellationToken cancellationToken)

    {

        if (rows.Count == 0)

        {

            return string.Empty;

        }



        var columns = BuildColumnOrder(rows, objectType, groups);



        try

        {

            using var workbook = new XLWorkbook();

            var worksheet = workbook.Worksheets.Add(sheetName);



            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)

            {

                worksheet.Cell(1, columnIndex + 1).Value = columns[columnIndex];

                worksheet.Cell(1, columnIndex + 1).Style.Font.Bold = true;

            }



            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)

            {

                cancellationToken.ThrowIfCancellationRequested();

                var rowValues = rows[rowIndex];

                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)

                {

                    rowValues.TryGetValue(columns[columnIndex], out var value);

                    worksheet.Cell(rowIndex + 2, columnIndex + 1).Value = value ?? string.Empty;

                }

            }



            worksheet.Columns().AdjustToContents(1, Math.Min(columns.Count, 50));

            workbook.SaveAs(path);

            await Task.CompletedTask.ConfigureAwait(false);

            return path;

        }

        catch (Exception ex)

        {

            throw new IOException($"Failed to write Excel workbook '{path}': {ex.Message}", ex);

        }

    }





    private async Task<string> WriteAssociationCsvAsync(string path, IReadOnlyList<AssociationRow> rows, CancellationToken cancellationToken)

    {

        if (rows.Count == 0)

        {

            return string.Empty;

        }



        var columns = new[]

        {

            "Source Type",

            "Source Group",

            "Source Import ID",

            "Target Type",

            "Target Group",

            "Target Import ID",

            "Association Labels"

        };



        try

        {

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)

            {

                HasHeaderRecord = true

            });



            foreach (var column in columns)

            {

                csv.WriteField(column);

            }

            await csv.NextRecordAsync().ConfigureAwait(false);



            foreach (var row in rows)

            {

                cancellationToken.ThrowIfCancellationRequested();

                csv.WriteField(row.SourceType.ToString());

                csv.WriteField(row.SourceIndex);

                csv.WriteField(row.SourceImportId ?? string.Empty);

                csv.WriteField(row.TargetType.ToString());

                csv.WriteField(row.TargetIndex);

                csv.WriteField(row.TargetImportId ?? string.Empty);

                csv.WriteField(row.Labels ?? string.Empty);

                await csv.NextRecordAsync().ConfigureAwait(false);

            }



            await writer.FlushAsync().ConfigureAwait(false);

            return path;

        }

        catch (Exception ex)

        {

            throw new IOException($"Failed to write associations CSV '{path}': {ex.Message}", ex);

        }

    }



    private async Task<string> WriteAssociationExcelAsync(string path, IReadOnlyList<AssociationRow> rows, CancellationToken cancellationToken)

    {

        if (rows.Count == 0)

        {

            return string.Empty;

        }



        try

        {

            using var workbook = new XLWorkbook();

            var worksheet = workbook.Worksheets.Add("Associations");

            var columns = new[]

            {

                "Source Type",

                "Source Group",

                "Source Import ID",

                "Target Type",

                "Target Group",

                "Target Import ID",

                "Association Labels"

            };



            for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)

            {

                worksheet.Cell(1, columnIndex + 1).Value = columns[columnIndex];

                worksheet.Cell(1, columnIndex + 1).Style.Font.Bold = true;

            }



            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)

            {

                cancellationToken.ThrowIfCancellationRequested();

                var row = rows[rowIndex];

                worksheet.Cell(rowIndex + 2, 1).Value = row.SourceType.ToString();

                worksheet.Cell(rowIndex + 2, 2).Value = row.SourceIndex;

                worksheet.Cell(rowIndex + 2, 3).Value = row.SourceImportId ?? string.Empty;

                worksheet.Cell(rowIndex + 2, 4).Value = row.TargetType.ToString();

                worksheet.Cell(rowIndex + 2, 5).Value = row.TargetIndex;

                worksheet.Cell(rowIndex + 2, 6).Value = row.TargetImportId ?? string.Empty;

                worksheet.Cell(rowIndex + 2, 7).Value = row.Labels ?? string.Empty;

            }



            worksheet.Columns().AdjustToContents(1, columns.Length);

            workbook.SaveAs(path);

            await Task.CompletedTask.ConfigureAwait(false);

            return path;

        }

        catch (Exception ex)

        {

            throw new IOException($"Failed to write associations workbook '{path}': {ex.Message}", ex);

        }

    }



    private async Task<string> WriteReportAsync(string path, Dictionary<ProfileObjectType, Dictionary<int, int>> counters, List<string> missingHeaders)

    {

        var report = new ProcessingReport

        {

            GeneratedAtUtc = DateTime.UtcNow,

            GroupCounts = counters,

            MissingHeaders = missingHeaders,

            Warnings = _validationWarnings

        };



        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions

        {

            WriteIndented = true

        });



        await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);

        return path;

    }



    private static IReadOnlyList<string> BuildColumnOrder(IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType, IReadOnlyList<MappingGroup> groups)

    {

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    columns.Add(key);
                }
            }
        }

        foreach (var group in groups)
        {
            foreach (var mapping in group.Mappings)
            {
                columns.Add(mapping.HubSpotHeader);
            }
        }

        var includeImportId = rows.Any(row => row.TryGetValue("Import ID", out var value) && !string.IsNullOrWhiteSpace(value));
        if (includeImportId)
        {
            columns.Add("Import ID");
        }
        else
        {
            columns.Remove("Import ID");
        }

        var includeAssociationLabel = rows.Any(row => row.TryGetValue("Association Label", out var value) && !string.IsNullOrWhiteSpace(value));
        if (includeAssociationLabel)
        {
            columns.Add("Association Label");
        }
        else
        {
            columns.Remove("Association Label");
        }

        if (rows.Any(row => row.ContainsKey("Data Source")))
        {
            columns.Add("Data Source");
        }

        if (rows.Any(row => row.ContainsKey("Data Type")))
        {
            columns.Add("Data Type");
        }

        if (rows.Any(row => row.ContainsKey("Tags")))
        {
            columns.Add("Tags");
        }

        var orderedColumns = new List<string>();

        if (columns.Remove("Import ID"))
        {
            orderedColumns.Add("Import ID");
        }

        if (columns.Remove("Association Label"))
        {
            orderedColumns.Add("Association Label");
        }

        if (columns.Remove("Data Source"))
        {
            orderedColumns.Add("Data Source");
        }

        if (columns.Remove("Data Type"))
        {
            orderedColumns.Add("Data Type");
        }

        if (columns.Remove("Tags"))
        {
            orderedColumns.Add("Tags");
        }

        orderedColumns.AddRange(columns.OrderBy(c => c, StringComparer.Ordinal));

        return orderedColumns;

    }



    private static string FormatMultiSelectSingle(string? value)

    {

        var text = value?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))

        {

            return string.Empty;

        }

        text = text.Trim(';');

        return ";" + text;

    }



    private static string FormatMultiSelectList(IReadOnlyList<string> values)

    {

        var cleaned = values?.Where(v => !string.IsNullOrWhiteSpace(v))

                             .Select(v => v.Trim().Trim(';'))

                             .ToList() ?? new List<string>();

        if (cleaned.Count == 0)

        {

            return string.Empty;

        }

        return ";" + string.Join(";", cleaned);

    }



    private Dictionary<string, string> ToContactDictionary(RowOutput row, bool includeAssociationLabel)

    {

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        {

            ["Data Source"] = FormatMultiSelectSingle(row.DataSource),

            ["Data Type"] = FormatMultiSelectSingle(row.DataType),

            ["Tags"] = FormatMultiSelectList(row.Tags)

        };



        // Only include Association Label if create secondary files is enabled

        if (includeAssociationLabel)

        {

            dictionary["Association Label"] = FormatMultiSelectList(row.AssociationLabels);

        }



        foreach (var kvp in row.Values)

        {

            dictionary[kvp.Key] = kvp.Value ?? string.Empty;

        }



        return dictionary;

    }



    private Dictionary<string, string> ToPropertyDictionary(RowOutput row)



    {



        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);



        var associationLabel = FormatMultiSelectList(row.AssociationLabels);

        if (!string.IsNullOrEmpty(associationLabel))

        {

            dictionary["Association Label"] = associationLabel;

        }



        dictionary["Data Source"] = FormatMultiSelectSingle(row.DataSource);

        dictionary["Data Type"] = FormatMultiSelectSingle(row.DataType);

        dictionary["Tags"] = FormatMultiSelectList(row.Tags);



        foreach (var kvp in row.Values)



        {



            dictionary[kvp.Key] = kvp.Value ?? string.Empty;



        }



        return dictionary;



    }







    private Dictionary<string, string> ToPhoneDictionary(RowOutput row)

    {

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        {

            ["Data Source"] = FormatMultiSelectSingle(row.DataSource)

            // Phones never get Data Type or Tags

        };



        foreach (var kvp in row.Values)

        {

            dictionary[kvp.Key] = kvp.Value ?? string.Empty;

        }



        return dictionary;

    }



    // Keep the old method for compatibility during transition

    private Dictionary<string, string> ToDictionary(RowOutput row)

    {

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

        {

            ["Group Index"] = row.GroupIndex.ToString(CultureInfo.InvariantCulture),

            ["Association Label"] = FormatMultiSelectList(row.AssociationLabels),

            ["Data Source"] = row.DataSource ?? string.Empty,

            ["Tags"] = row.Tags.Count > 0 ? string.Join(", ", row.Tags) : string.Empty

        };



        foreach (var kvp in row.Values)

        {

            dictionary[kvp.Key] = kvp.Value ?? string.Empty;

        }



        return dictionary;

    }



    private List<RowOutput> BuildPropertyRows(List<ProcessedRowData> processedData, bool linkToContacts)

    {

        var propertyRows = new List<RowOutput>();



        foreach (var data in processedData)

        {

            foreach (var property in data.Properties.Where(p => p.HasData))

            {

                var row = new RowOutput(property.ObjectType, property.GroupIndex, property.AssociationLabels, property.DataSource, property.DataType, property.Tags);

                if (linkToContacts)
                {
                    row.Values["Import ID"] = data.ContactId; // Link to contact
                }



                foreach (var kvp in property.Values)

                {

                    row.Values[kvp.Key] = kvp.Value;

                }



                var formattedAddress = FormatAddress(GetValueOrEmpty(property.Values, "Address"));

                if (!string.IsNullOrEmpty(formattedAddress))

                {

                    row.Values["Address"] = formattedAddress;

                }



                var formattedCity = FormatCity(GetValueOrEmpty(property.Values, "City"));

                if (!string.IsNullOrEmpty(formattedCity))

                {

                    row.Values["City"] = formattedCity;

                }



                var formattedState = FormatState(GetValueOrEmpty(property.Values, "State"));

                if (!string.IsNullOrEmpty(formattedState))

                {

                    row.Values["State"] = formattedState;

                }



                var formattedCounty = FormatCounty(GetValueOrEmpty(property.Values, "County"));

                if (!string.IsNullOrEmpty(formattedCounty))

                {

                    row.Values["County"] = formattedCounty;

                }



                var formattedPostalCode = FormatPostalCode(GetValueOrEmpty(property.Values, "Postal Code"));

                if (!string.IsNullOrEmpty(formattedPostalCode))

                {

                    row.Values["Postal Code"] = formattedPostalCode;

                }



                var addressCityStateKey = BuildDedupeKey(formattedAddress, formattedCity, formattedState);

                if (!string.IsNullOrEmpty(addressCityStateKey))

                {

                    row.Values["dedupe_key_address_city_state"] = addressCityStateKey;

                }



                var addressZipKey = BuildDedupeKey(formattedAddress, formattedPostalCode);

                if (!string.IsNullOrEmpty(addressZipKey))

                {

                    row.Values["dedupe_key_address_zip"] = addressZipKey;

                }



                propertyRows.Add(row);

            }

        }



        // No deduplication; return as-is so HubSpot can dedupe

        return propertyRows;

    }













    private List<RowOutput> BuildContactRows(List<ProcessedRowData> processedData, bool includeAssociationLabels)

    {

        var contactRows = new List<RowOutput>();



        foreach (var data in processedData)

        {

            foreach (var contact in data.Contacts.Where(c => c.HasData))

            {

                var row = new RowOutput(contact.ObjectType, contact.GroupIndex, 

                    includeAssociationLabels ? contact.AssociationLabels : Array.Empty<string>(), 

                    contact.DataSource, contact.DataType, contact.Tags);

                

                row.Values["Import ID"] = data.ContactId; // Unique contact ID

                

                foreach (var kvp in contact.Values)

                {

                    row.Values[kvp.Key] = kvp.Value;

                }

                

                contactRows.Add(row);

            }

        }



        return contactRows;

    }



    private List<RowOutput> BuildPhoneRows(List<ProcessedRowData> processedData, bool linkToContacts)

    {

        var phoneRows = new List<RowOutput>();



        foreach (var data in processedData)

        {

            foreach (var phone in data.Phones.Where(p => p.HasData))

            {

                // Phones never get association labels

                var row = new RowOutput(phone.ObjectType, phone.GroupIndex, Array.Empty<string>(), phone.DataSource, phone.DataType, phone.Tags);

                if (linkToContacts)
                {
                    row.Values["Import ID"] = data.ContactId; // Link to contact
                }

                

                foreach (var kvp in phone.Values)

                {

                    row.Values[kvp.Key] = kvp.Value;

                }

                

                phoneRows.Add(row);

            }

        }



        return phoneRows;

    }



    private List<AssociationRow> BuildAssociationRows(List<ProcessedRowData> processedData)

    {

        var rows = new List<AssociationRow>();



        foreach (var data in processedData)

        {

            foreach (var source in data.AllGroups)

            {

                if (!source.HasData || string.IsNullOrWhiteSpace(source.ImportId) || source.Associations.Count == 0)

                {

                    continue;

                }



                foreach (var association in source.Associations)

                {

                    if (association.Labels is not { Count: > 0 })

                    {

                        continue;

                    }



                    if (!data.GroupLookup.TryGetValue((association.TargetType, association.TargetIndex), out var target))

                    {

                        continue;

                    }



                    if (!target.HasData || string.IsNullOrWhiteSpace(target.ImportId))

                    {

                        continue;

                    }



                    var labels = FormatMultiSelectList(association.Labels);

                    if (string.IsNullOrEmpty(labels))

                    {

                        continue;

                    }



                    rows.Add(new AssociationRow(

                        source.ObjectType,

                        source.GroupIndex,

                        source.ImportId,

                        association.TargetType,

                        association.TargetIndex,

                        target.ImportId,

                        labels));

                }

            }

        }



        return rows;

    }





    private void ReportProgress(string message, int percent)

    {

        _progress?.Report(new ProcessingProgress

        {

            Message = message,

            PercentComplete = Math.Clamp(percent, 0, 100),

            Timestamp = DateTime.UtcNow

        });

    }



    private sealed class ProcessedRowData



    {



        public ProcessedRowData(string contactId, List<ProcessedGroup> properties, List<ProcessedGroup> contacts, List<ProcessedGroup> phones)



        {



            ContactId = contactId;



            Properties = properties;



            Contacts = contacts;



            Phones = phones;



            GroupLookup = new Dictionary<(ProfileObjectType Type, int Index), ProcessedGroup>();







            foreach (var group in properties)



            {



                GroupLookup[(group.ObjectType, group.GroupIndex)] = group;



            }







            foreach (var group in contacts)



            {



                GroupLookup[(group.ObjectType, group.GroupIndex)] = group;



            }







            foreach (var group in phones)



            {



                GroupLookup[(group.ObjectType, group.GroupIndex)] = group;



            }



        }







        public string ContactId { get; }



        public List<ProcessedGroup> Properties { get; }



        public List<ProcessedGroup> Contacts { get; }



        public List<ProcessedGroup> Phones { get; }



        public Dictionary<(ProfileObjectType Type, int Index), ProcessedGroup> GroupLookup { get; }







        public IEnumerable<ProcessedGroup> AllGroups



        {



            get



            {



                foreach (var group in Properties)



                {



                    yield return group;



                }







                foreach (var group in Contacts)



                {



                    yield return group;



                }







                foreach (var group in Phones)



                {



                    yield return group;



                }



            }



        }



    }







    private static string GetValueOrEmpty(IReadOnlyDictionary<string, string> values, string key)

    {

        if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))

        {

            return value.Trim();

        }



        return string.Empty;

    }



    private static string BuildDedupeKey(params string[] parts)
    {
        return parts.Any(part => string.IsNullOrEmpty(part))
            ? string.Empty
            : string.Join(" | ", parts);
    }

    private static string FormatAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var result = StandardizeOrdinals(address);
        result = result.Replace("#", string.Empty);
        result = DirectionRegex.Replace(result, match => DirectionMap[match.Groups[1].Value] + " ");
        result = StreetAbbreviationDotRegex.Replace(result, m => m.Groups[1].Value);

        foreach (var kvp in StreetTypeMap)
        {
            result = Regex.Replace(result, $"\\b{Regex.Escape(kvp.Key)}\\b", kvp.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        result = result.ToLowerInvariant();
        result = TitleCaseRegex.Replace(result, m => m.Value.ToUpperInvariant());
        result = MultiSpaceRegex.Replace(result, " ").Trim();
        return result;
    }

    private static string FormatCity(string city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return string.Empty;
        }

        var result = city.Replace("(", string.Empty).Replace(")", string.Empty);
        result = result.Trim().ToLowerInvariant();
        result = TitleCaseRegex.Replace(result, m => m.Value.ToUpperInvariant());
        return MultiSpaceRegex.Replace(result, " ").Trim();
    }

    private static string FormatState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return string.Empty;
        }

        var clean = state.Replace("(", string.Empty).Replace(")", string.Empty).Trim();
        if (StateMap.TryGetValue(clean.ToLowerInvariant(), out var abbreviation))
        {
            return abbreviation;
        }

        return clean.ToUpperInvariant();
    }

    private static string FormatCounty(string county)
    {
        if (string.IsNullOrWhiteSpace(county))
        {
            return string.Empty;
        }

        var result = county.Replace("(", string.Empty).Replace(")", string.Empty);
        result = result.Trim().ToLowerInvariant();
        result = TitleCaseRegex.Replace(result, m => m.Value.ToUpperInvariant());
        return MultiSpaceRegex.Replace(result, " ").Trim();
    }

    private static string FormatPostalCode(string postalCode)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
        {
            return string.Empty;
        }

        var digits = Regex.Replace(postalCode, "\\D", string.Empty);
        if (digits.Length == 0)
        {
            return string.Empty;
        }

        if (digits.Length > 5)
        {
            digits = digits.Substring(0, 5);
        }

        return digits.PadLeft(5, '0');
    }

    private static string StandardizeOrdinals(string input)
    {
        return OrdinalRegex.Replace(input, match =>
        {
            var numberText = match.Groups[1].Value;
            if (!int.TryParse(numberText, out var number))
            {
                return match.Value;
            }

            var lastDigit = number % 10;
            var lastTwoDigits = number % 100;
            var suffix = lastTwoDigits >= 11 && lastTwoDigits <= 13
                ? "th"
                : lastDigit switch
                {
                    1 => "st",
                    2 => "nd",
                    3 => "rd",
                    _ => "th"
                };

            return numberText + suffix;
        });
    }
    private sealed class ProcessedGroup

    {

        public ProcessedGroup(ProfileObjectType objectType, int groupIndex, IReadOnlyList<GroupAssociation> associations, string dataSource, string dataType, List<string> tags, Dictionary<string, string> values, bool hasData)

        {

            ObjectType = objectType;

            GroupIndex = groupIndex;

            Associations = associations?.Select(CloneAssociation).ToList() ?? new List<GroupAssociation>();

            AssociationLabels = Associations

                .SelectMany(a => a.Labels ?? new List<string>())

                .Where(label => !string.IsNullOrWhiteSpace(label))

                .Select(label => label.Trim())

                .Distinct(StringComparer.OrdinalIgnoreCase)

                .ToList();

            DataSource = dataSource ?? string.Empty;

            DataType = dataType ?? string.Empty;

            Tags = tags ?? new List<string>();

            Values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            HasData = hasData;

        }



        public ProfileObjectType ObjectType { get; }

        public int GroupIndex { get; }

        public List<GroupAssociation> Associations { get; }

        public List<string> AssociationLabels { get; }

        public string DataSource { get; }

        public string DataType { get; }

        public List<string> Tags { get; }

        public Dictionary<string, string> Values { get; }

        public bool HasData { get; }

        public string ImportId { get; set; } = string.Empty;



        private static GroupAssociation CloneAssociation(GroupAssociation association)

        {

            return new GroupAssociation

            {

                TargetType = association.TargetType,

                TargetIndex = association.TargetIndex,

                Labels = association.Labels == null

                    ? new List<string>()

                    : association.Labels.Where(label => !string.IsNullOrWhiteSpace(label))

                        .Select(label => label.Trim())

                        .Distinct(StringComparer.OrdinalIgnoreCase)

                        .ToList()

            };

        }

    }



    private sealed class MappingGroup

    {

        public MappingGroup(ProfileObjectType objectType, int groupIndex, List<ProfileMapping> mappings, GroupMetadata metadata)

        {

            ObjectType = objectType;

            GroupIndex = groupIndex;

            Mappings = mappings;

            Metadata = metadata;

        }



        public ProfileObjectType ObjectType { get; }

        public int GroupIndex { get; }

        public List<ProfileMapping> Mappings { get; }

        public GroupMetadata Metadata { get; }

    }



    private sealed class GroupMetadata



    {



        public GroupMetadata(IReadOnlyList<GroupAssociation> associations, string dataSource, string dataType, IReadOnlyList<string> tags)



        {



            Associations = associations?.Select(CloneAssociation).ToList() ?? new List<GroupAssociation>();



            DataSource = dataSource ?? string.Empty;



            DataType = dataType ?? string.Empty;



            Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t))



                       .Select(t => t.Trim())



                       .Distinct(StringComparer.OrdinalIgnoreCase)



                       .ToList() ?? new List<string>();



        }







        public List<GroupAssociation> Associations { get; }



        public string DataSource { get; }



        public string DataType { get; }



        public List<string> Tags { get; }







        private static GroupAssociation CloneAssociation(GroupAssociation association)



        {



            return new GroupAssociation



            {



                TargetType = association.TargetType,



                TargetIndex = association.TargetIndex,



                Labels = association.Labels == null



                    ? new List<string>()



                    : association.Labels.Where(label => !string.IsNullOrWhiteSpace(label))



                        .Select(label => label.Trim())



                        .Distinct(StringComparer.OrdinalIgnoreCase)



                        .ToList()



            };



        }



    }







    private sealed class RowOutput



    {



        public RowOutput(ProfileObjectType objectType, int groupIndex, IReadOnlyList<string> associationLabels, string dataSource, string dataType, IReadOnlyList<string> tags)



        {



            ObjectType = objectType;



            GroupIndex = groupIndex;



            AssociationLabels = associationLabels?.Where(label => !string.IsNullOrWhiteSpace(label))



                .Select(label => label.Trim())



                .Distinct(StringComparer.OrdinalIgnoreCase)



                .ToList() ?? new List<string>();



            DataSource = dataSource ?? string.Empty;



            DataType = dataType ?? string.Empty;



            Tags = tags?.ToList() ?? new List<string>();



            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);



        }







        public ProfileObjectType ObjectType { get; }



        public int GroupIndex { get; }



        public List<string> AssociationLabels { get; }



        public string DataSource { get; set; }



        public string DataType { get; set; }



        public List<string> Tags { get; }



        public Dictionary<string, string> Values { get; }



        public string ImportId { get; set; } = string.Empty;



    }







    private sealed class AssociationRow



    {



        public AssociationRow(ProfileObjectType sourceType, int sourceIndex, string sourceImportId, ProfileObjectType targetType, int targetIndex, string targetImportId, string labels)



        {



            SourceType = sourceType;



            SourceIndex = sourceIndex;



            SourceImportId = sourceImportId;



            TargetType = targetType;



            TargetIndex = targetIndex;



            TargetImportId = targetImportId;



            Labels = labels;



        }







        public ProfileObjectType SourceType { get; }



        public int SourceIndex { get; }



        public string SourceImportId { get; }



        public ProfileObjectType TargetType { get; }



        public int TargetIndex { get; }



        public string TargetImportId { get; }



        public string Labels { get; }



    }







    private sealed class ProcessingReport

    {

        public DateTime GeneratedAtUtc { get; set; }

        public Dictionary<ProfileObjectType, Dictionary<int, int>> GroupCounts { get; set; } = new();

        public List<string> MissingHeaders { get; set; } = new();

        public List<string> Warnings { get; set; } = new();

    }

}



public class ProcessingResult

{

    public bool Success { get; set; }

    public List<string> CsvFiles { get; set; } = new();

    public List<string> ExcelFiles { get; set; } = new();

    public List<string> JsonFiles { get; set; } = new();

    public string ContactsFile { get; set; } = string.Empty;

    public string PhonesFile { get; set; } = string.Empty;

    public string AssociationsFile { get; set; } = string.Empty;

    public string AssociationsExcelFile { get; set; } = string.Empty;

    public string SecondaryContactsFile { get; set; } = string.Empty;

    public string SecondaryPhonesFile { get; set; } = string.Empty;

    public string SecondaryPropertiesFile { get; set; } = string.Empty;

    public string PropertiesFile { get; set; } = string.Empty;

    public string? SummaryReportPath { get; set; }

    public int TotalRecordsProcessed { get; set; }

    public int ContactsCreated { get; set; }

    public int PropertiesCreated { get; set; }

    public int PhonesCreated { get; set; }

    public int AssociationsCreated { get; set; }

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






