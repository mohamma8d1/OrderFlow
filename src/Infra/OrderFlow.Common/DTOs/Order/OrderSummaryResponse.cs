using OrderFlow.Common.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.DTOs.Order;

public record OrderSummaryResponse(
    Guid Id,
    DateTime CreatedDate,
    decimal TotalAmount,
    OrderStatus Status);
