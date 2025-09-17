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
        services.AddTransient<CsvInputReader>();
        services.AddTransient<ExcelInputReader>();

        return services;
    }
}