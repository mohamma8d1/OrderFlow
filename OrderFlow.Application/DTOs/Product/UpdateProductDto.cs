using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.Product;

public record UpdateProductDto(
    string Name,
    string? Description,
    decimal Price,
    int Quantity,
    Guid CategoryId);  

