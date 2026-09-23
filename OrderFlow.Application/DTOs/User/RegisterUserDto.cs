using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.User;

public record RegisterUserDto(
    string Email,
    string PhoneNumber,
    string? FullName,
    string Password
    );

