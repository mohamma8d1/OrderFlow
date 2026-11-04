using System;

namespace OrderFlow.Infrastructure.Persistence.Outbox;

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }

    public string EventType { get; set; } = null!;

    public string StreamEntryId { get; set; } = null!;

    public DateTime ProcessedAtUtc { get; set; }
}
