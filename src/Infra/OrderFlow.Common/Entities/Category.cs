using OrderFlow.Common.Entities;
using System;
using System.Collections.Generic;

namespace OrderFlow.Common.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = null!;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
