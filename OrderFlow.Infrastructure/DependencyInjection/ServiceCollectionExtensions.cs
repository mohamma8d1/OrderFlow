using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderFlow.Application.Interfaces;
using OrderFlow.Infrastructure.Services;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IJwtService, JwtService>();

        var redisConnectionString = config["Redis:ConnectionString"] ?? throw new InvalidOperationException("Redis connection string is missing.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);
            options.ClientName = "OrderFlow";

            return ConnectionMultiplexer.Connect(options);
        });

        return services;
    }
}
