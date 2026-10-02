using OrderFlow.Domain.Common;
using System;
using System.Globalization;

namespace OrderFlow.Domain.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = null!;
    public string PhoneNumber { get; set; } = null!;
    public string? FullName { get; set; }

    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = "Customer";

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
