using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.Events;

public sealed record OrderCreatedEvent(
    Guid EventId,
    Guid OrderId,
    Guid UserId,
    decimal TotalAmount,
    DateTime OccurredAtUtc);
