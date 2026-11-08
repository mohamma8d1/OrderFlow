using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Common.DTOs.Product;

public record UpdateProductDto(
    string Name,
    string? Description,
    decimal Price,
    int Quantity,
    Guid CategoryId);  

