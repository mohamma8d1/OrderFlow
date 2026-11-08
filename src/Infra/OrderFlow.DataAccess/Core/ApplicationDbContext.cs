using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderFlow.Common.Entities;
using System;

namespace OrderFlow.DataAccess.Core;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        if (!Database.IsSqlServer())
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var createdAt = entityType.FindProperty(nameof(BaseEntity.CreatedAt));
                if (createdAt is not null)
                {
                    createdAt.SetDefaultValueSql(null);
                    createdAt.ValueGenerated = ValueGenerated.Never;
                }
            }

            var product = modelBuilder.Model.FindEntityType(typeof(Product));
            var rowVersion = product?.FindProperty(nameof(Product.RowVresion));
            if (rowVersion is not null)
            {
                rowVersion.IsConcurrencyToken = false;
                rowVersion.ValueGenerated = ValueGenerated.Never;
                rowVersion.IsNullable = true;
                rowVersion.RemoveAnnotation("Relational:IsRowVersion");
                rowVersion.RemoveAnnotation("SqlServer:IsRowVersion");
            }
        }
    }

    public override int SaveChanges()
    {
        UpdateBaseEntityProperties();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateBaseEntityProperties();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateBaseEntityProperties()
    {
        var now = DateTime.UtcNow;

        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(e => e.UpdatedAt).CurrentValue = now;

                entry.Property(e => e.UpdatedAt).IsModified = true;
            }
        }
    }
}
