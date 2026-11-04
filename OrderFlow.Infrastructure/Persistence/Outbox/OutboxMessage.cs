using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Infrastructure.Persistence.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = null!;

    public string Payload { get; set; } = null!;

    public DateTime OccurredAtUtc { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }
}