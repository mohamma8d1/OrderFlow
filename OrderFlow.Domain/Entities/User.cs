using OrderFlow.Domain.Common;
using System;

namespace OrderFlow.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = null!;
    public string PhoneNumber { get; set; } = null!;
    public string? FullName { get; set; }
}
