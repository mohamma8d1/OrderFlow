using OrderFlow.Application.DTOs.OrderItem;
using OrderFlow.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.Order;

public record OrderDetailsResponse(
    Guid Id,
    DateTime CreatedAt,
    decimal TotalAmount,
    OrderStatus Status,
    IReadOnlyList<OrderItemDetailsResponse> Items);
