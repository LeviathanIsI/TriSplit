using Microsoft.Extensions.DependencyInjection;
using TriSplit.Core.Extensions;
using TriSplit.Core.Interfaces;

namespace TriSplit.CLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        switch (args[0].ToLower())
        {
            case "load":
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: Please provide a file path");
                    Console.WriteLine("Usage: TriSplit.CLI load <file>");
                    return 1;
                }
                await LoadFileAsync(args[1]);
                break;

            case "profile":
                if (args.Length > 1 && args[1] == "--list")
                {
                    await ListProfilesAsync();
                }
                else
                {
                    Console.WriteLine("Usage: TriSplit.CLI profile --list");
                }
                break;

            case "--help":
            case "-h":
                ShowHelp();
                break;

            default:
                Console.WriteLine($"Unknown command: {args[0]}");
                ShowHelp();
                return 1;
        }

        return 0;
    }

    static void ShowHelp()
    {
        Console.WriteLine("TriSplit CLI - Data processing and HubSpot integration");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  TriSplit.CLI <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  load <file>      Load and process a data file");
        Console.WriteLine("  profile --list   List all profiles");
        Console.WriteLine("  --help, -h       Show this help message");
    }

    static async Task LoadFileAsync(string filePath)
    {
        try
        {
            Console.WriteLine($"Loading file: {filePath}");

            var services = new ServiceCollection();
            services.AddTriSplitCore();

            using var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();

            var sampleLoader = scope.ServiceProvider.GetRequiredService<ISampleLoader>();

            var data = await sampleLoader.LoadSampleWithLimitAsync(filePath, 10);

            Console.WriteLine($"Successfully loaded {data.TotalRows} rows");
            Console.WriteLine($"Headers: {string.Join(", ", data.Headers)}");
            Console.WriteLine($"\nFirst few rows:");

            foreach (var row in data.Rows.Take(3))
            {
                foreach (var kvp in row)
                {
                    Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
                }
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading file: {ex.Message}");
        }
    }

    static async Task ListProfilesAsync()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddTriSplitCore();

            using var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();

            var profileStore = scope.ServiceProvider.GetRequiredService<IProfileStore>();
            var profiles = await profileStore.GetAllProfilesAsync();

            if (!profiles.Any())
            {
                Console.WriteLine("No profiles found.");
                return;
            }

            Console.WriteLine("Available Profiles:");
            Console.WriteLine("==================");

            foreach (var profile in profiles)
            {
                Console.WriteLine($"- {profile.Name} (ID: {profile.Id})");
                Console.WriteLine($"  Created: {profile.CreatedAt:yyyy-MM-dd HH:mm}");
                Console.WriteLine($"  Mappings: {profile.ContactMappings.Count} contacts, " +
                                $"{profile.PropertyMappings.Count} properties");
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error listing profiles: {ex.Message}");
        }
    }
}
