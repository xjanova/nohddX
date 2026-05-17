using Microsoft.EntityFrameworkCore;
using NohddX.Api;
using NohddX.Boot;
using NohddX.Cluster;
using NohddX.ClientMgmt;
using NohddX.Database;
using NohddX.Iscsi;
using NohddX.Monitoring;
using NohddX.Server;
using NohddX.Storage;
using Prometheus;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/nohddx-.log", rollingInterval: RollingInterval.Day));

// Register all services
builder.Services.AddNohddxDatabase(builder.Configuration);
builder.Services.AddNohddxStorage(builder.Configuration);
builder.Services.AddNohddxIscsi();
builder.Services.AddNohddxBoot();
builder.Services.AddNohddxClientManagement();
builder.Services.AddNohddxCluster();
builder.Services.AddNohddxMonitoring();
builder.Services.AddNohddxApi(builder.Configuration);
builder.Services.AddControllers();

var app = builder.Build();

// Ensure database is created, then patch in any tables/indexes the model
// expects but the existing file lacks. EnsureCreatedAsync only initialises
// empty DBs — when we add a new table to the model (e.g. AuditLog), existing
// dev DBs would be missing it forever without this step. Real production
// deployments should use `dotnet ef` migrations, but for dev this avoids
// surprising "no such table" 500s.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NohddxDbContext>();
    await db.Database.EnsureCreatedAsync();

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbSync");
    await DbSync.SyncMissingTablesAsync(db, logger);
}

app.MapControllers();
app.MapNohddxEndpoints();
app.MapMetrics();

await app.RunAsync();
