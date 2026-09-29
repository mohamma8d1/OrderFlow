using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.DTOs.User;

public record LoginUserDto(string Email, string Password);
