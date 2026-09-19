using OrderFlow.Domain.Common;
using System;

namespace OrderFlow.Domain.Entities;

public class Product : BaseEntity
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;

    public Category Category { get; set; } = null!;

}
