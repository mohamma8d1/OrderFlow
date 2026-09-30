using OrderFlow.Application.DTOs.OrderItem;
using System;

namespace OrderFlow.Application.DTOs.Order;

public record CreateOrderDto(List<CreateOrderItemDto> Items);
