using OrderFlow.Common.Enums;

namespace OrderFlow.Common.DTOs.Order;

public sealed record OrderCreatedResponse(Guid Id, decimal TotalAmount, OrderStatus Status);
