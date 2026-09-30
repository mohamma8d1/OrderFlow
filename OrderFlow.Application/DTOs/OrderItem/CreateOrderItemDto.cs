using System;

namespace OrderFlow.Application.DTOs.OrderItem;

public record CreateOrderItemDto(Guid ProductId, int Quantity);
