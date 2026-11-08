using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Interfaces;
using OrderFlow.Infrastructure.Caching;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Services;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IJwtService, JwtService>();

        // Default: no caching. AddRedisCaching() swaps in the Redis implementation.
        services.TryAddSingleton<IProductCache, NullProductCache>();

        return services;
    }

    /// <summary>
    /// Uses Redis for the product cache. Requires AddRedisConnection().
    /// </summary>
    public static IServiceCollection AddRedisCaching(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IProductCache, RedisProductCache>());

        return services;
    }

    public static IServiceCollection AddOutboxMessaging(this IServiceCollection services)
    {
        services.AddSingleton(sp => new OutboxPublisher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<OutboxPublisher>>()));

        services.AddSingleton(sp => new OrderStreamConsumer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<OrderStreamConsumer>>()));

        return services;
    }

    public static IServiceCollection AddRedisConnection(
        this IServiceCollection services,
        IConfiguration config)
    {
        var redisConnectionString = config["Redis:ConnectionString"] ?? throw new InvalidOperationException("Redis connection string is missing.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);

            options.ClientName = "OrderFlow";
            options.AbortOnConnectFail = false;

            return ConnectionMultiplexer.Connect(options);
        });

        return services;
    }
}