using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderFlow.DataAccess.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OrderFlow.Tests;

public class OrderFlowApiFactory : WebApplicationFactory<Program>
{
    // Unique named in-memory database per fixture, so parallel test classes
    // get isolated databases while all scopes/requests in this fixture share one.
    private readonly string _connectionString =
        $"Data Source=OrderFlowTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // Held open for the fixture lifetime to keep the shared in-memory database alive.
    private readonly SqliteConnection _keepAliveConnection;

    public OrderFlowApiFactory()
    {
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            var testSettings = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "OrderFlow-TEST-ONLY-Signing-Key-Do-Not-Use-In-Production-2026!",
                ["Jwt:Issuer"] = "OrderFlow.Tests",
                ["Jwt:Audience"] = "OrderFlow.Tests.Client"
            };

            configuration.AddInMemoryCollection(testSettings);
        });

        builder.ConfigureTestServices(services =>
        {
            // Drop the app's SQL Server EF registration so it doesn't clash with SQLite.
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>)
                            || d.ServiceType == typeof(ApplicationDbContext)
                            || (d.ServiceType.FullName?.Contains("EntityFrameworkCore") ?? false))
                .ToList();

            foreach (var descriptor in toRemove)
                services.Remove(descriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(_connectionString));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _keepAliveConnection.Dispose();

        base.Dispose(disposing);
    }
}
