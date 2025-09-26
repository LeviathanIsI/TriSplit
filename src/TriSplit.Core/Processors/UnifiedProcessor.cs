using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
            cancellationToken.ThrowIfCancellationRequested();
            var row = inputData.Rows[rowIndex];
            var contactId = Guid.NewGuid().ToString(); // Generate unique contact ID for this row

            var rowData = ProcessRowData(row, contactId, propertyGroups, contactGroups, phoneGroups, headerLookup, groupCounters);
            if (rowData != null)
            {
                processedData.Add(rowData);
            }

            var percent = 15 + (int)Math.Round(70.0 * (rowIndex + 1) / Math.Max(1, inputData.Rows.Count));
            ReportProgress($"Processed {rowIndex + 1:N0} of {inputData.Rows.Count:N0} rows", percent);
        }

        // Build final output with proper deduplication and linking
        var acceptedTag = !string.IsNullOrWhiteSpace(options.Tag) ? options.Tag : null;
        var propertyRows = BuildPropertyRows(processedData, acceptedTag);
        var contactRows = BuildContactRows(processedData, _profile.CreateSecondaryContactsFile, acceptedTag);
        var phoneRows = BuildPhoneRows(processedData, acceptedTag);

        var propertyDictionaries = propertyRows.Select(row => ToPropertyDictionary(row)).ToList();
        var contactDictionaries = contactRows.Select(row => ToContactDictionary(row, _profile.CreateSecondaryContactsFile)).ToList();
        var phoneDictionaries = phoneRows.Select(row => ToPhoneDictionary(row)).ToList();

        var result = new ProcessingResult
        {
            Success = true,
            TotalRecordsProcessed = inputData.Rows.Count,
            PropertiesCreated = propertyDictionaries.Count,
            ContactsCreated = contactDictionaries.Count,
            PhonesCreated = phoneDictionaries.Count
        };

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (options.OutputCsv)
        {
            ReportProgress("Writing CSV files...", 90);
            result.PropertiesFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"properties-{timestamp}.csv"), propertyDictionaries, ProfileObjectType.Property, cancellationToken).ConfigureAwait(false);
            result.ContactsFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"contacts-{timestamp}.csv"), contactDictionaries, ProfileObjectType.Contact, cancellationToken).ConfigureAwait(false);
            result.PhonesFile = await WriteCsvAsync(Path.Combine(outputDirectory, $"phones-{timestamp}.csv"), phoneDictionaries, ProfileObjectType.Phone, cancellationToken).ConfigureAwait(false);

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
        }

        if (options.OutputExcel)
        {
            ReportProgress("Writing Excel workbooks...", 92);
            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"properties-{timestamp}.xlsx"), "Properties", propertyDictionaries, ProfileObjectType.Property, cancellationToken).ConfigureAwait(false));
            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"contacts-{timestamp}.xlsx"), "Contacts", contactDictionaries, ProfileObjectType.Contact, cancellationToken).ConfigureAwait(false));
            result.ExcelFiles.Add(await WriteExcelAsync(Path.Combine(outputDirectory, $"phones-{timestamp}.xlsx"), "Phones", phoneDictionaries, ProfileObjectType.Phone, cancellationToken).ConfigureAwait(false));
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

        // Only create a row if we have actual data
        if (properties.Any(p => p.HasData) || contacts.Any(c => c.HasData) || phones.Any(p => p.HasData))
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
                group.Metadata.AssociationLabel,
                group.Metadata.DataSource,
                group.Metadata.DataType,
                group.Metadata.Tags,
                values,
                hasData));
        }
    }

    private IReadOnlyDictionary<string, int> BuildHeaderLookup(IEnumerable<string> headers)
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var header in headers ?? Array.Empty<string>())
        {
            if (!lookup.ContainsKey(header))
            {
                lookup[header] = index++;
            }
        }
        return lookup;
    }

    private string ResolveFieldValue(Dictionary<string, object> row, IReadOnlyDictionary<string, int> headerLookup, ProfileMapping mapping)
    {
        if (!headerLookup.ContainsKey(mapping.SourceField))
        {
            return string.Empty;
        }

        row.TryGetValue(mapping.SourceField, out var rawValue);
        var text = Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
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
        if (row.TryGetValue(argument, out var value) && value is not null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return argument ?? string.Empty;
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
        var metadata = ResolveGroupMetadata(defaults, mappings, warnings);

        ValidateDuplicateTargets(objectType, groupIndex, mappings);

        return new MappingGroup(objectType, groupIndex, mappings, metadata);
    }

    private static GroupMetadata ResolveGroupMetadata(GroupDefaults defaults, List<ProfileMapping> mappings, List<string> warnings)
    {
        var associationLabel = defaults.AssociationLabel ?? string.Empty;
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
            warnings.Add($"Multiple association overrides defined for group {mappings.First().GroupIndex}; using '{associationOverrides[0]}'.");
        }
        if (associationOverrides.Count >= 1)
        {
            associationLabel = associationOverrides[0];
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
            .Where(tags => tags is { Count: > 0 })
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

        return new GroupMetadata(associationLabel, dataSource, dataType, tags);
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

    private async Task<string> WriteCsvAsync(string path, IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = BuildColumnOrder(rows, objectType);

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

    private async Task<string> WriteExcelAsync(string path, string sheetName, IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = BuildColumnOrder(rows, objectType);

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

    private static IReadOnlyList<string> BuildColumnOrder(IReadOnlyList<Dictionary<string, string>> rows, ProfileObjectType objectType)
    {
        var columns = new List<string>();
        
        // Define main columns based on object type and check if they have data
        switch (objectType)
        {
            case ProfileObjectType.Contact:
                // MAIN COLUMNS: Import ID, First Name, Last Name, Data Source, Data Type, Tags
                TryAddColumnWithData(columns, rows, "Import ID");
                TryAddColumnWithData(columns, rows, "First Name");
                TryAddColumnWithData(columns, rows, "Last Name");
                TryAddColumnWithData(columns, rows, "Data Source");
                TryAddColumnWithData(columns, rows, "Data Type");
                TryAddColumnWithData(columns, rows, "Tags");
                TryAddColumnWithData(columns, rows, "Association Label"); // Only if create secondary files is enabled
                break;
                
            case ProfileObjectType.Property:
                // MAIN COLUMNS: Import ID, Address, City, State, Postal Code, Association Label, Data Source, Data Type, Tags
                TryAddColumnWithData(columns, rows, "Import ID");
                TryAddColumnWithData(columns, rows, "Address");
                TryAddColumnWithData(columns, rows, "City");
                TryAddColumnWithData(columns, rows, "State");
                TryAddColumnWithData(columns, rows, "Postal Code");
                TryAddColumnWithData(columns, rows, "Association Label");
                TryAddColumnWithData(columns, rows, "Data Source");
                TryAddColumnWithData(columns, rows, "Data Type");
                TryAddColumnWithData(columns, rows, "Tags");
                break;
                
            case ProfileObjectType.Phone:
                // MAIN COLUMNS: Import ID, Phone Number, Phone Type, Data Source
                TryAddColumnWithData(columns, rows, "Import ID");
                TryAddColumnWithData(columns, rows, "Phone Number");
                TryAddColumnWithData(columns, rows, "Phone Type");
                TryAddColumnWithData(columns, rows, "Data Source");
                break;
        }

        // Add any remaining columns that have data, alphabetically
        var allColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                allColumns.Add(key);
            }
        }

        foreach (var column in allColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase) && ColumnHasData(rows, column))
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    private static void TryAddColumnWithData(List<string> columns, IReadOnlyList<Dictionary<string, string>> rows, string columnName)
    {
        if (ColumnHasData(rows, columnName))
        {
            columns.Add(columnName);
        }
    }

    private static bool ColumnHasData(IReadOnlyList<Dictionary<string, string>> rows, string columnName)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(columnName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }
        return false;
    }

    private Dictionary<string, string> ToContactDictionary(RowOutput row, bool includeAssociationLabel)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Data Source"] = row.DataSource ?? string.Empty,
            ["Data Type"] = row.DataType ?? string.Empty,
            ["Tags"] = row.Tags.Count > 0 ? string.Join(", ", row.Tags) : string.Empty
        };

        // Only include Association Label if create secondary files is enabled
        if (includeAssociationLabel)
        {
            dictionary["Association Label"] = row.AssociationLabel ?? string.Empty;
        }

        foreach (var kvp in row.Values)
        {
            dictionary[kvp.Key] = kvp.Value ?? string.Empty;
        }

        return dictionary;
    }

    private Dictionary<string, string> ToPropertyDictionary(RowOutput row)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Association Label"] = row.AssociationLabel ?? string.Empty,
            ["Data Source"] = row.DataSource ?? string.Empty,
            ["Data Type"] = row.DataType ?? string.Empty,
            ["Tags"] = row.Tags.Count > 0 ? string.Join(", ", row.Tags) : string.Empty
        };

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
            ["Data Source"] = row.DataSource ?? string.Empty
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
            ["Association Label"] = row.AssociationLabel ?? string.Empty,
            ["Data Source"] = row.DataSource ?? string.Empty,
            ["Tags"] = row.Tags.Count > 0 ? string.Join(", ", row.Tags) : string.Empty
        };

        foreach (var kvp in row.Values)
        {
            dictionary[kvp.Key] = kvp.Value ?? string.Empty;
        }

        return dictionary;
    }

    private List<RowOutput> BuildPropertyRows(List<ProcessedRowData> processedData, string? acceptedTag)
    {
        // Step 1: Build all property rows without deduplication
        var propertyRows = new List<RowOutput>();

        foreach (var data in processedData)
        {
            foreach (var property in data.Properties.Where(p => p.HasData))
            {
                var tags = acceptedTag != null ? new List<string> { acceptedTag } : property.Tags;
                var row = new RowOutput(property.ObjectType, property.GroupIndex, property.AssociationLabel, property.DataSource, property.DataType, tags);
                row.Values["Import ID"] = data.ContactId; // Link to contact
                
                foreach (var kvp in property.Values)
                {
                    row.Values[kvp.Key] = kvp.Value;
                }
                
                propertyRows.Add(row);
            }
        }

        // Step 2: Perform deduplication as separate operation
        return DeduplicatePropertyRows(propertyRows);
    }

    private List<RowOutput> DeduplicatePropertyRows(List<RowOutput> propertyRows)
    {
        var deduplicatedRows = new List<RowOutput>();
        var addressLookup = new Dictionary<string, RowOutput>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in propertyRows)
        {
            var addressKey = BuildAddressKey(row.Values);
            
            if (!string.IsNullOrEmpty(addressKey) && addressLookup.TryGetValue(addressKey, out var existingRow))
            {
                // Combine association labels
                var existingLabels = existingRow.AssociationLabel?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
                var newLabels = row.AssociationLabel.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                
                var combinedLabels = existingLabels.Concat(newLabels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                existingRow.AssociationLabel = string.Join(";", combinedLabels);
                
                // Merge any additional data that doesn't exist in the existing row
                foreach (var kvp in row.Values)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value) && (!existingRow.Values.ContainsKey(kvp.Key) || string.IsNullOrWhiteSpace(existingRow.Values[kvp.Key])))
                    {
                        existingRow.Values[kvp.Key] = kvp.Value;
                    }
                }
            }
            else
            {
                deduplicatedRows.Add(row);
                
                if (!string.IsNullOrEmpty(addressKey))
                {
                    addressLookup[addressKey] = row;
                }
            }
        }

        return deduplicatedRows;
    }

    private List<RowOutput> BuildContactRows(List<ProcessedRowData> processedData, bool includeAssociationLabels, string? acceptedTag)
    {
        var contactRows = new List<RowOutput>();

        foreach (var data in processedData)
        {
            foreach (var contact in data.Contacts.Where(c => c.HasData))
            {
                var tags = acceptedTag != null ? new List<string> { acceptedTag } : contact.Tags;
                var row = new RowOutput(contact.ObjectType, contact.GroupIndex, 
                    includeAssociationLabels ? contact.AssociationLabel : string.Empty, 
                    contact.DataSource, contact.DataType, tags);
                
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

    private List<RowOutput> BuildPhoneRows(List<ProcessedRowData> processedData, string? acceptedTag)
    {
        var phoneRows = new List<RowOutput>();

        foreach (var data in processedData)
        {
            foreach (var phone in data.Phones.Where(p => p.HasData))
            {
                var tags = acceptedTag != null ? new List<string> { acceptedTag } : phone.Tags;
                // Phones never get association labels
                var row = new RowOutput(phone.ObjectType, phone.GroupIndex, string.Empty, phone.DataSource, phone.DataType, tags);
                row.Values["Import ID"] = data.ContactId; // Link to contact
                
                foreach (var kvp in phone.Values)
                {
                    row.Values[kvp.Key] = kvp.Value;
                }
                
                phoneRows.Add(row);
            }
        }

        return phoneRows;
    }

    private string BuildAddressKey(Dictionary<string, string> values)
    {
        // Use address fields to create a key for deduplication
        var addressFields = new[] { "address", "street", "property address", "mailing address" };
        var cityFields = new[] { "city", "property city", "mailing city" };
        var stateFields = new[] { "state", "property state", "mailing state" };
        var zipFields = new[] { "zip", "postal code", "property zip", "mailing zip" };

        var address = FindFieldValue(values, addressFields);
        var city = FindFieldValue(values, cityFields);
        var state = FindFieldValue(values, stateFields);
        var zip = FindFieldValue(values, zipFields);

        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        return $"{address?.Trim()?.ToLowerInvariant()}|{city?.Trim()?.ToLowerInvariant()}|{state?.Trim()?.ToLowerInvariant()}|{zip?.Trim()?.ToLowerInvariant()}";
    }

    private string? FindFieldValue(Dictionary<string, string> values, string[] fieldNames)
    {
        foreach (var fieldName in fieldNames)
        {
            if (values.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
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
        }

        public string ContactId { get; }
        public List<ProcessedGroup> Properties { get; }
        public List<ProcessedGroup> Contacts { get; }
        public List<ProcessedGroup> Phones { get; }
    }

    private sealed class ProcessedGroup
    {
        public ProcessedGroup(ProfileObjectType objectType, int groupIndex, string associationLabel, string dataSource, string dataType, List<string> tags, Dictionary<string, string> values, bool hasData)
        {
            ObjectType = objectType;
            GroupIndex = groupIndex;
            AssociationLabel = associationLabel;
            DataSource = dataSource;
            DataType = dataType;
            Tags = tags;
            Values = values;
            HasData = hasData;
        }

        public ProfileObjectType ObjectType { get; }
        public int GroupIndex { get; }
        public string AssociationLabel { get; }
        public string DataSource { get; }
        public string DataType { get; }
        public List<string> Tags { get; }
        public Dictionary<string, string> Values { get; }
        public bool HasData { get; }
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
        public GroupMetadata(string associationLabel, string dataSource, string dataType, IReadOnlyList<string> tags)
        {
            AssociationLabel = associationLabel ?? string.Empty;
            DataSource = dataSource ?? string.Empty;
            DataType = dataType ?? string.Empty;
            Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        }

        public string AssociationLabel { get; }
        public string DataSource { get; }
        public string DataType { get; }
        public List<string> Tags { get; }
    }

    private sealed class RowOutput
    {
        public RowOutput(ProfileObjectType objectType, int groupIndex, string associationLabel, string dataSource, string dataType, IReadOnlyList<string> tags)
        {
            ObjectType = objectType;
            GroupIndex = groupIndex;
            AssociationLabel = associationLabel;
            DataSource = dataSource;
            DataType = dataType;
            Tags = tags?.ToList() ?? new List<string>();
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public ProfileObjectType ObjectType { get; }
        public int GroupIndex { get; }
        public string AssociationLabel { get; set; }
        public string DataSource { get; set; }
        public string DataType { get; set; }
        public List<string> Tags { get; }
        public Dictionary<string, string> Values { get; }
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
