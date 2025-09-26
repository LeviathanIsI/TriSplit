using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TriSplit.Core.Models;

namespace DiagHarness;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var profilePath = args.Length > 0 ? args[0] : "profiles/test-phone-profile.json";
        var profile = await LoadProfileAsync(profilePath);

        Console.WriteLine($"Profile: {profile.Name}");
        Console.WriteLine($"Mappings: {profile.Mappings.Count}");
        foreach (var mapping in profile.Mappings)
        {
            Console.WriteLine($"  [{mapping.ObjectType}] Group {mapping.GroupIndex}: {mapping.SourceField} -> {mapping.HubSpotHeader}");
        }

        Console.WriteLine();
        Console.WriteLine("Group defaults:");
        DumpDefaults("Properties", profile.Groups.PropertyGroups);
        DumpDefaults("Contacts", profile.Groups.ContactGroups);
        DumpDefaults("Phones", profile.Groups.PhoneGroups);
    }

    private static void DumpDefaults(string label, IDictionary<int, GroupDefaults> groups)
    {
        Console.WriteLine($"  {label}:");
        foreach (var kvp in groups)
        {
            var defaults = kvp.Value;
            var tags = defaults.Tags.Count > 0 ? string.Join(", ", defaults.Tags) : "(none)";
            Console.WriteLine($"    Group {kvp.Key}: Association={defaults.AssociationLabel}, DataSource={defaults.DataSource}, Tags={tags}");
        }
    }

    private static async Task<Profile> LoadProfileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return CreateSampleProfile();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var profile = JsonConvert.DeserializeObject<Profile>(json);
            return profile ?? CreateSampleProfile();
        }
        catch
        {
            return CreateSampleProfile();
        }
    }

    private static Profile CreateSampleProfile()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Diagnostics Sample"
        };

        profile.Groups.PropertyGroups[1] = new GroupDefaults
        {
            AssociationLabel = "Owner",
            DataSource = "Diagnostics"
        };

        profile.Mappings = new List<ProfileMapping>
        {
            new()
            {
                SourceField = "Address",
                HubSpotHeader = "Address",
                ObjectType = ProfileObjectType.Property,
                GroupIndex = 1
            },
            new()
            {
                SourceField = "City",
                HubSpotHeader = "City",
                ObjectType = ProfileObjectType.Property,
                GroupIndex = 1
            }
        };

        return profile;
    }
}
