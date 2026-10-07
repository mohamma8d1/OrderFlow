using OrderFlow.Domain.Common;
using System;
using System.Diagnostics.Contracts;

namespace OrderFlow.Domain.Entities;

public class Product : BaseEntity
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public byte[] RowVresion { get; set; } = null!;

    public Category Category { get; set; } = null!;

}
