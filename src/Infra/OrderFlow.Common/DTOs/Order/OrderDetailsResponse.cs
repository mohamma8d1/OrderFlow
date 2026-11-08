using OrderFlow.Common.DTOs.OrderItem;
using OrderFlow.Common.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.DTOs.Order;

public record OrderDetailsResponse(
    Guid Id,
    DateTime CreatedAt,
    decimal TotalAmount,
    OrderStatus Status,
    IReadOnlyList<OrderItemDetailsResponse> Items);
