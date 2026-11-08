using OrderFlow.Common.DTOs.OrderItem;
using System;

namespace OrderFlow.Common.DTOs.Order;

public record CreateOrderDto(List<CreateOrderItemDto> Items);
