using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.DTOs.OrderItem;

public record OrderItemDetailsResponse(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice,
    decimal LineToatal);// LineTotal = UnitPrice × Quantity
