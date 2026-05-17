using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Storage.CoW;
using NohddX.Storage.Images;
using NohddX.Storage.Raid;

namespace NohddX.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNohddxStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NohddxOptions>(configuration.GetSection(NohddxOptions.SectionName));
        services.AddSingleton<VhdImageManager>();
        services.AddSingleton<ImageCatalog>();
        services.AddSingleton<ICowStorageEngine, CowOverlayDisk>();
        services.AddSingleton<RaidMonitor>();
        return services;
    }
}
