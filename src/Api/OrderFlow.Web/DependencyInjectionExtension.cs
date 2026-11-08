using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OrderFlow.Api.Controllers;
using OrderFlow.Api.Filters;
using OrderFlow.Business.Businesses;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Services;
using OrderFlow.Common.Entities;
using OrderFlow.DataAccess.Caching;
using OrderFlow.DataAccess.Core;
using OrderFlow.DataAccess.Messaging;
using OrderFlow.Web.BackgroundServices;
using StackExchange.Redis;
using System.Reflection;
using System.Text;

namespace OrderFlow.Web;

internal static class DependencyInjectionExtension
{
    internal static IServiceCollection InjectControllersAndApi(this IServiceCollection services) =>
        services
            .AddControllers(options =>
            {
                // BusinessException -> 400/401/404/409
                options.Filters.Add<BusinessExceptionFilter>();
            })
            // Controllers live in OrderFlow.Api, not in this project
            .AddApplicationPart(typeof(ProductController).Assembly)
            .Services;

    internal static IServiceCollection InjectSwagger(this IServiceCollection services) =>
        services
            .AddEndpointsApiExplorer()
            .AddSwaggerGen(options =>
            {
                const string schemeId = "bearer";

                options.AddSecurityDefinition(schemeId, new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Enter your JWT token."
                });

                options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(schemeId, document)] = []
                });
            });

    internal static IServiceCollection InjectJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"],
                    ValidAudience = configuration["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.")))
                };
            });

        return services.AddAuthorization();
    }

    /// <summary>
    /// One ApplicationDbContext per request; Business classes use it directly.
    /// Also registers "no Redis" fallbacks; <see cref="InjectRedis"/> replaces them with the real ones.
    /// </summary>
    internal static IServiceCollection InjectDataAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));

        services.TryAddSingleton<IProductCache, NullProductCache>();
        services.TryAddSingleton<IRateLimiter, NullRateLimiter>();
        services.TryAddSingleton<IProductChangePublisher, NullProductChangePublisher>();

        return services;
    }

    /// <summary>Redis connection + the Redis-backed product cache and rate limiter.</summary>
    internal static IServiceCollection InjectRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Redis connection string is missing.");

        // One multiplexer for the whole app: it is thread-safe and meant to be shared.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.ClientName = "OrderFlow";
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.Replace(ServiceDescriptor.Singleton<IProductCache, RedisProductCache>());
        services.Replace(ServiceDescriptor.Singleton<IRateLimiter, RedisRateLimiter>());
        services.Replace(ServiceDescriptor.Singleton<IProductChangePublisher, RedisProductChangePublisher>());

        return services;
    }

    internal static IServiceCollection InjectBusinesses(this IServiceCollection services) =>
        services
            .AddScoped<IPasswordHasher<User>, PasswordHasher<User>>()
            .AddScoped<IJwtService, JwtService>()
            .AddScopedByNamespace(typeof(ProductBusiness).Assembly, ns => ns.EndsWith("Business.Businesses", StringComparison.Ordinal));

    /// <summary>Outbox publisher + Redis Streams consumer, and the hosted services that run them.</summary>
    internal static IServiceCollection InjectMessaging(this IServiceCollection services) =>
        services
            .AddSingleton(sp => new OutboxPublisher(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<OutboxPublisher>>()))
            .AddSingleton(sp => new OrderStreamConsumer(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<OrderStreamConsumer>>()))
            .AddHostedService<ProductChangeSubscriber>()
            .AddHostedService<OutboxPublisherBackgroundService>()
            .AddHostedService<OrderStreamConsumerBackgroundService>();

    private static IServiceCollection AddScopedByNamespace(this IServiceCollection services, Assembly assembly, Func<string, bool> namespaceFilter)
    {
        var types = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false, IsGenericTypeDefinition: false }
                && type.Namespace is not null && namespaceFilter(type.Namespace));

        foreach (var type in types)
        {
            services.AddScoped(type);

            foreach (var contract in type.GetInterfaces().Where(i => i != typeof(IDisposable)))
                services.AddScoped(contract, sp => sp.GetRequiredService(type));
        }

        return services;
    }
}
