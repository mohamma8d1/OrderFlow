using OrderFlow.Domain.Common;
using OrderFlow.Domain.Enums;
using System;

namespace OrderFlow.Domain.Entities;

public class Order : BaseEntity
{
    public Guid UserId { get; set; }
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; }
    public User User { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
