using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFlow.Infrastructure.Persistence.Outbox;
using System;

namespace OrderFlow.Infrastructure.Persistence.Configuration;

public sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("ProcessedEvents");

        builder.HasKey(x => x.EventId);

        builder.Property(x => x.EventType)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.StreamEntryId)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ProcessedAtUtc)
            .IsRequired();
    }
}