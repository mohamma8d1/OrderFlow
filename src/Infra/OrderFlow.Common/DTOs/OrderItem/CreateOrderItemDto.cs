using System;

namespace OrderFlow.Common.DTOs.OrderItem;

public record CreateOrderItemDto(Guid ProductId, int Quantity);
