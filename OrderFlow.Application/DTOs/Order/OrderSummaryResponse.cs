using OrderFlow.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.Order;

public record OrderSummaryResponse(
    Guid Id,
    DateTime CreatedDate,
    decimal TotalAmount,
    OrderStatus Status);
