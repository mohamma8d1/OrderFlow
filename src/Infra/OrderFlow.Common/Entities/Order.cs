using OrderFlow.Common.Entities;
using OrderFlow.Common.Enums;
using System;

namespace OrderFlow.Common.Entities;

public class Order : BaseEntity
{
    public Guid UserId { get; set; }
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; }
    public User User { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
