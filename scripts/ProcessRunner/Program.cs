using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;
using TriSplit.Core.Processors;
using TriSplit.Core.Services;

namespace ProcessRunner;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: ProcessRunner <profileName> <inputFile> [outputDir]");
            return;
        }

        var profileName = args[0];
        var inputFile = args[1];
        var outputDir = args.Length > 2 ? args[2] : string.Empty;

        var profileStore = new ProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TriSplit", "Profiles"));
        var profile = await profileStore.GetProfileByNameAsync(profileName);
        if (profile == null)
        {
            Console.WriteLine($"Profile '{profileName}' not found.");
            return;
        }

        IInputReader reader = new CsvInputReader();
        var excelExporter = new ExcelExporter();
        var processor = new UnifiedProcessor(profile, reader, excelExporter, new Progress<ProcessingProgress>(p =>
        {
            Console.WriteLine($"[{p.PercentComplete}%] {p.Severity}: {p.Message}");
        }));

        var options = new ProcessingOptions
        {
            OutputCsv = true,
            OutputExcel = false,
            OutputJson = false,
            Tag = "manual"
        };

        try
        {
            var result = await processor.ProcessAsync(inputFile, outputDir, options);
            Console.WriteLine($"Success: {result.Success}, Error: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unhandled exception: {ex}");
        }
    }
}
