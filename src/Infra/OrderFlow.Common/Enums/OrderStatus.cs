using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.Enums;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Processing,
    Shipped,
    Delivered,
    Cancelled
}
