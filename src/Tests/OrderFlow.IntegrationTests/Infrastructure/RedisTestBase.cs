using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFlow.DataAccess.Core;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;
using Xunit;

namespace OrderFlow.IntegrationTests;

public abstract class RedisTestBase : IAsyncLifetime
{
    private readonly string _connectionString =
        $"Data Source=RedisTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _provider = null!;

    protected RedisTestBase(RedisContainerFixture fixture)
    {
        Fixture = fixture;
    }

    protected RedisContainerFixture Fixture { get; }

    protected IDatabase Redis => Fixture.Multiplexer.GetDatabase();

    protected IServiceScopeFactory ScopeFactory { get; private set; } = null!;

    private static readonly ILoggerFactory LoggerFactory =
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Warning).AddConsole());

    protected ILogger<T> Logger<T>() => LoggerFactory.CreateLogger<T>();

    public async Task InitializeAsync()
    {
        await Redis.ExecuteAsync("FLUSHDB");

        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connectionString));
        services.AddLogging();

        _provider = services.BuildServiceProvider();
        ScopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _provider?.Dispose();

        if (_keepAlive is not null)
            await _keepAlive.DisposeAsync();

        await Redis.ExecuteAsync("FLUSHDB");
    }

    protected async Task WithDbAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
    }

    protected async Task<T> WithDbAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await action(db);
    }
}
