using Microsoft.Extensions.DependencyInjection;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Services;

namespace TriSplit.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTriSplitCore(this IServiceCollection services)
    {
        // Register core services
        services.AddSingleton<IProfileStore, ProfileStore>();
        services.AddSingleton<IInputReaderFactory, InputReaderFactory>();
        services.AddScoped<ISampleLoader, SampleLoader>();

        // Register readers
        services.AddTransient<IInputReader, CsvInputReader>();
        services.AddTransient<IInputReader, ExcelInputReader>();

        return services;
    }
}
