using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Core.Processors;
using TriSplit.Core.Services;

static string ResolvePath(string relative)
{
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));
}

static IInputReader CreateReader(string inputPath)
{
    var extension = Path.GetExtension(inputPath);
    return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
        ? new ExcelInputReader()
        : new CsvInputReader();
}

static Profile CreateFallbackProfile()
{
    return new Profile
    {
        Id = Guid.NewGuid(),
        Name = "Harness Default",
        DefaultAssociationLabel = "Owner",
        ContactMappings = new List<FieldMapping>
        {
            new()
            {
                SourceColumn = "owner1_first_name",
                HubSpotProperty = "First Name",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Contact
            },
            new()
            {
                SourceColumn = "owner1_last_name",
                HubSpotProperty = "Last Name",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Contact
            }
        },
        PropertyMappings = new List<FieldMapping>
        {
            new()
            {
                SourceColumn = "property_address",
                HubSpotProperty = "Address",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "city",
                HubSpotProperty = "City",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "state",
                HubSpotProperty = "State",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "zip",
                HubSpotProperty = "Zip",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "owner_mailing_address",
                HubSpotProperty = "Address",
                AssociationType = "Mailing Address",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "owner_mailing_city",
                HubSpotProperty = "City",
                AssociationType = "Mailing Address",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "owner_mailing_state",
                HubSpotProperty = "State",
                AssociationType = "Mailing Address",
                ObjectType = MappingObjectTypes.Property
            },
            new()
            {
                SourceColumn = "owner_mailing_zip",
                HubSpotProperty = "Zip",
                AssociationType = "Mailing Address",
                ObjectType = MappingObjectTypes.Property
            }
        },
        PhoneMappings = new List<FieldMapping>
        {
            new()
            {
                SourceColumn = "TloPhone1",
                HubSpotProperty = "Phone Number",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.PhoneNumber
            },
            new()
            {
                SourceColumn = "TloPhone1PhoneType",
                HubSpotProperty = "Phone Type",
                AssociationType = "Owner",
                ObjectType = MappingObjectTypes.PhoneNumber
            }
        }
    };
}

static async Task<Profile> LoadProfileAsync(string profilePath)
{
    if (!File.Exists(profilePath))
    {
        Console.WriteLine("Profile file not found; using fallback profile.");
        return CreateFallbackProfile();
    }

    try
    {
        var profileJson = await File.ReadAllTextAsync(profilePath);
        var profile = JsonConvert.DeserializeObject<Profile>(profileJson);
        if (profile is null)
        {
            Console.WriteLine("Failed to deserialize profile; using fallback profile.");
            return CreateFallbackProfile();
        }

        Console.WriteLine($"Loaded profile '{profile.Name}' ({profile.Id}).");
        return profile;
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"Profile parsing failed: {ex.Message}. Using fallback profile.");
        return CreateFallbackProfile();
    }
}

var profileArgument = args.Length > 0 ? args[0] : "profiles/test-phone-profile.json";
var inputArgument = args.Length > 1 ? args[1] : "tlo_sample.csv";
var outputArgument = args.Length > 2 ? args[2] : "harness-output";

var profilePath = Path.IsPathRooted(profileArgument) ? profileArgument : ResolvePath(profileArgument);
var inputPath = Path.IsPathRooted(inputArgument) ? inputArgument : ResolvePath(Path.Combine("scripts", "ProcessorHarness", inputArgument));
var outputDir = Path.IsPathRooted(outputArgument) ? outputArgument : ResolvePath(Path.Combine("scripts", "ProcessorHarness", outputArgument));

Console.WriteLine($"Using profile: {profilePath}");
Console.WriteLine($"Input file: {inputPath}");
Console.WriteLine($"Output directory: {outputDir}");

if (!File.Exists(inputPath))
{
    Console.WriteLine("Input file not found.");
    return;
}

Directory.CreateDirectory(outputDir);

var profile = await LoadProfileAsync(profilePath);

IInputReader inputReader = CreateReader(inputPath);
IExcelExporter excelExporter = new ExcelExporter();

var processor = new UnifiedProcessor(profile, inputReader, excelExporter, new Progress<ProcessingProgress>(p =>
{
    Console.WriteLine($"[{p.Timestamp:HH:mm:ss}] {p.PercentComplete}% {p.Message} ({p.Severity})");
}));

var options = new ProcessingOptions
{
    OutputCsv = true,
    OutputExcel = false,
    OutputJson = false
};

var result = await processor.ProcessAsync(Path.GetFullPath(inputPath), Path.GetFullPath(outputDir), options, CancellationToken.None);

Console.WriteLine($"Success: {result.Success}, Records: {result.TotalRecordsProcessed}");
Console.WriteLine($"CSV Files: {string.Join(", ", result.CsvFiles)}");
