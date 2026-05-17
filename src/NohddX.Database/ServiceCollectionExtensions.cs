using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NohddX.Core.Interfaces;
using NohddX.Database.Repositories;

namespace NohddX.Database;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNohddxDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("NohddxDb");
        var provider = configuration["Database:Provider"] ?? "sqlite";

        services.AddDbContext<NohddxDbContext>(options =>
        {
            if (provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
                options.UseNpgsql(connectionString);
            else
                options.UseSqlite(connectionString ?? "Data Source=nohddx.db");
        });

        services.AddScoped<IClientRepository, ClientRepository>();
        services.AddScoped<IImageRepository, ImageRepository>();
        services.AddScoped<IBootAssignmentRepository, BootAssignmentRepository>();
        services.AddScoped<IClusterNodeRepository, ClusterNodeRepository>();
        services.AddScoped<IBootEventRepository, BootEventRepository>();
        services.AddScoped<IStoragePoolRepository, StoragePoolRepository>();
        services.AddScoped<IClientGroupRepository, ClientGroupRepository>();
        services.AddScoped<IHardwareProfileRepository, HardwareProfileRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        return services;
    }
}
