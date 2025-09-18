using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using TriSplit.Core.Models;
using TriSplit.Core.Interfaces;

namespace TriSplit.Core.Processors;

public abstract class BaseProcessor
{
    protected readonly Profile _profile;
    protected readonly IInputReader _inputReader;
    protected readonly IProgress<ProcessingProgress>? _progress;

    // Output data structures with Import ID as key
    protected readonly Dictionary<string, ContactRecord> _contacts = new();
    protected readonly Dictionary<string, List<PhoneRecord>> _phones = new();
    protected readonly Dictionary<string, PropertyRecord> _properties = new();

    protected BaseProcessor(Profile profile, IInputReader inputReader, IProgress<ProcessingProgress>? progress = null)
    {
        _profile = profile;
        _inputReader = inputReader;
        _progress = progress;
    }

    /// <summary>
    /// Generate a unique Import ID (UUID) for linking records across files
    /// </summary>
    protected string GenerateImportId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Process the input file and generate three output files with Import ID linking
    /// </summary>
    public virtual async Task<ProcessingResult> ProcessAsync(string inputFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ReportProgress("Starting processing...", 0);

        // Create output directory structure
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var outputPath = Path.Combine(outputDirectory, timestamp);
        Directory.CreateDirectory(outputPath);

        try
        {
            // Read input data
            ReportProgress("Reading input file...", 10);
            var inputData = await _inputReader.ReadAsync(inputFilePath);

            if (inputData.Rows.Count == 0)
            {
                throw new InvalidOperationException("No data found in input file");
            }

            // Process each row
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

            // Write output files with Import ID columns
            ReportProgress("Writing output files...", 75);
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
            throw;
        }
    }

    /// <summary>
    /// Process a single data row - must be implemented by derived classes
    /// </summary>
    protected abstract Task ProcessRowAsync(Dictionary<string, object> row);

    /// <summary>
    /// Apply field mappings from profile to extract value
    /// </summary>
    protected string GetMappedValue(Dictionary<string, object> row, string associationType, string hubSpotProperty)
    {
        // Find mapping in profile
        var mapping = _profile.ContactMappings
            .Concat(_profile.PropertyMappings)
            .Concat(_profile.PhoneMappings)
            .FirstOrDefault(m =>
                m.AssociationType == associationType &&
                m.HubSpotProperty == hubSpotProperty);

        if (mapping == null || string.IsNullOrEmpty(mapping.SourceColumn))
            return string.Empty;

        // Get value from row
        if (!row.ContainsKey(mapping.SourceColumn))
            return string.Empty;

        var value = row.GetValueOrDefault(mapping.SourceColumn)?.ToString() ?? string.Empty;

        // Apply transforms if any
        foreach (var transform in mapping.Transforms ?? new List<Transform>())
        {
            value = ApplyTransform(value, transform);
        }

        return value;
    }

    /// <summary>
    /// Apply a transform to a value
    /// </summary>
    protected virtual string ApplyTransform(string value, Transform transform)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        switch (transform.Type.ToLower())
        {
            case "regex":
                if (!string.IsNullOrEmpty(transform.Pattern))
                {
                    var regex = new System.Text.RegularExpressions.Regex(transform.Pattern);
                    value = regex.Replace(value, transform.Replacement ?? string.Empty);
                }
                break;

            case "format":
                // Format based on pattern
                if (!string.IsNullOrEmpty(transform.Pattern))
                {
                    try
                    {
                        value = string.Format(transform.Pattern, value);
                    }
                    catch { }
                }
                break;

            case "normalize":
                value = value.Trim().ToUpper();
                break;
        }

        return value;
    }

    /// <summary>
    /// Write contacts file with Import ID column
    /// </summary>
    protected virtual async Task<string> WriteContactsFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "01_Contacts_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        // Write header with Import ID as first column
        csv.WriteField("Import ID");
        csv.WriteField("First Name");
        csv.WriteField("Last Name");
        csv.WriteField("Email");
        csv.WriteField("Company");
        csv.WriteField("Association Label");
        csv.WriteField("Notes");
        await csv.NextRecordAsync();

        // Write records
        foreach (var contact in _contacts.Values.OrderBy(c => c.LastName).ThenBy(c => c.FirstName))
        {
            csv.WriteField(contact.ImportId);
            csv.WriteField(contact.FirstName);
            csv.WriteField(contact.LastName);
            csv.WriteField(contact.Email);
            csv.WriteField(contact.Company);
            csv.WriteField(contact.AssociationLabel);
            csv.WriteField(contact.Notes);
            await csv.NextRecordAsync();
        }

        await writer.FlushAsync();
        return filePath;
    }

    /// <summary>
    /// Write phones file with Import ID column for linking
    /// </summary>
    protected virtual async Task<string> WritePhonesFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "02_Phone_Numbers_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        // Write header with Import ID for linking
        csv.WriteField("Import ID");
        csv.WriteField("Phone Number");
        csv.WriteField("Phone Type");
        await csv.NextRecordAsync();

        // Write records
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

    /// <summary>
    /// Write properties file with Import ID column for linking
    /// </summary>
    protected virtual async Task<string> WritePropertiesFileAsync(string outputPath, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(outputPath, "03_Properties_Import.csv");

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        // Write header with Import ID for linking
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

        // Write records
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

    protected void ReportProgress(string message, int percentComplete)
    {
        _progress?.Report(new ProcessingProgress
        {
            Message = message,
            PercentComplete = percentComplete,
            Timestamp = DateTime.Now
        });
    }
}

// Data models for output records
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