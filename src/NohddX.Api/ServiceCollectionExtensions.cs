using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using NohddX.Api.Auth;
using NohddX.Api.Hubs;
using NohddX.Core.Configuration;

namespace NohddX.Api;

public static class ServiceCollectionExtensions
{
    public const string AgentRegisterRateLimitPolicy = "agent-register";
    public const string AgentRateLimitPolicy = "agent";
    public const string AdminRateLimitPolicy = "admin";

    public static IServiceCollection AddNohddxApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR();
        services.AddSingleton<DashboardNotifier>();
        services.AddHttpContextAccessor();

        // Configuration
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));

        // Auth services
        services.AddSingleton<AgentTokenService>();
        services.AddScoped<AuditLogger>();

        services
            .AddAuthentication(NohddxAuthSchemes.Scheme)
            .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(NohddxAuthSchemes.Scheme, _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(NohddxAuthSchemes.AdminPolicy, p =>
                p.RequireAuthenticatedUser().RequireRole(NohddxAuthSchemes.AdminRole));

            options.AddPolicy(NohddxAuthSchemes.AgentPolicy, p =>
                p.RequireAuthenticatedUser()
                 .RequireAssertion(c =>
                    c.User.IsInRole(NohddxAuthSchemes.AgentRole) ||
                    c.User.IsInRole(NohddxAuthSchemes.AdminRole)));
        });

        // Rate limiting
        services.AddRateLimiter(rl =>
        {
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.OnRejected = (ctx, _) =>
            {
                var logger = ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimit");
                logger.LogWarning(
                    "Rate-limit hit on {Path} from {Remote}",
                    ctx.HttpContext.Request.Path,
                    ctx.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            };

            rl.AddPolicy(AgentRegisterRateLimitPolicy, ctx =>
            {
                var opts = ctx.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
                var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = opts.AgentRegisterRatePerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });

            rl.AddPolicy(AgentRateLimitPolicy, ctx =>
            {
                var key = ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });

            rl.AddPolicy(AdminRateLimitPolicy, ctx =>
            {
                var opts = ctx.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
                var key = ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = opts.AdminRatePerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "NoHddX API",
                Version = "v1",
                Description = "NoHddX Diskless Boot System API"
            });

            c.AddSecurityDefinition("AdminApiKey", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Admin-Api-Key",
                Description = "Pre-shared admin API key (operator console)."
            });

            c.AddSecurityDefinition("AgentBearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "agent-token",
                Description = "HMAC-signed agent token issued at /api/agents/register."
            });
        });

        return services;
    }

    public static WebApplication MapNohddxEndpoints(this WebApplication app)
    {
        app.UseRouting();

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHub<DashboardHub>("/hubs/dashboard");

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // One-time security warning if admin auth is implicitly disabled
        var sec = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NohddX.Security");
        if (!sec.AuthEnabled)
        {
            logger.LogWarning("NohddX:Security:AuthEnabled = false. ALL endpoints are open. Do not run like this in production.");
        }
        else if (string.IsNullOrWhiteSpace(sec.AdminApiKey))
        {
            if (app.Environment.IsDevelopment() && sec.AllowAnonymousAdminInDev)
            {
                logger.LogWarning("No AdminApiKey set. Admin endpoints are anonymous (Development + AllowAnonymousAdminInDev). Set NohddX:Security:AdminApiKey before deploying.");
            }
            else
            {
                logger.LogError("No AdminApiKey set. Admin endpoints will reject all callers. Set NohddX:Security:AdminApiKey to enable the operator UI.");
            }
        }

        return app;
    }
}
