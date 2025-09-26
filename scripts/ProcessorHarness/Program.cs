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
    var profile = new Profile
    {
        Id = Guid.NewGuid(),
        Name = "Harness Default",
        OwnerMailing = new OwnerMailingConfiguration
        {
            Owner1GetsMailing = true,
            Owner2GetsMailing = false
        }
    };

    profile.Groups.PropertyGroups[1] = new GroupDefaults
    {
        AssociationLabel = "Owner",
        DataSource = "Harness"
    };
    profile.Groups.PropertyGroups[2] = new GroupDefaults
    {
        AssociationLabel = "Mailing Address",
        DataSource = "Harness"
    };
    profile.Groups.ContactGroups[1] = new GroupDefaults
    {
        AssociationLabel = "Owner",
        DataSource = "Harness"
    };
    profile.Groups.PhoneGroups[1] = new GroupDefaults
    {
        AssociationLabel = "Owner",
        DataSource = "Harness"
    };

    profile.Mappings = new List<ProfileMapping>
    {
        new()
        {
            SourceField = "owner1_first_name",
            HubSpotHeader = "First Name",
            ObjectType = ProfileObjectType.Contact,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "owner1_last_name",
            HubSpotHeader = "Last Name",
            ObjectType = ProfileObjectType.Contact,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "property_address",
            HubSpotHeader = "Address",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "city",
            HubSpotHeader = "City",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "state",
            HubSpotHeader = "State",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "zip",
            HubSpotHeader = "Postal Code",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "owner_mailing_address",
            HubSpotHeader = "Address",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 2
        },
        new()
        {
            SourceField = "owner_mailing_city",
            HubSpotHeader = "City",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 2
        },
        new()
        {
            SourceField = "owner_mailing_state",
            HubSpotHeader = "State",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 2
        },
        new()
        {
            SourceField = "owner_mailing_zip",
            HubSpotHeader = "Postal Code",
            ObjectType = ProfileObjectType.Property,
            GroupIndex = 2
        },
        new()
        {
            SourceField = "TloPhone1",
            HubSpotHeader = "Phone Number",
            ObjectType = ProfileObjectType.Phone,
            GroupIndex = 1
        },
        new()
        {
            SourceField = "TloPhone1PhoneType",
            HubSpotHeader = "Phone Type",
            ObjectType = ProfileObjectType.Phone,
            GroupIndex = 1
        }
    };

    return profile;
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

        if (profile.Mappings == null || profile.Mappings.Count == 0)
        {
            Console.WriteLine("Profile contains no mappings; using fallback profile.");
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

var processor = new UnifiedProcessor(profile, inputReader, new Progress<ProcessingProgress>(p =>
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
