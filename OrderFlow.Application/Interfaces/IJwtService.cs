using OrderFlow.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Application.Interfaces;

public interface IJwtService
{
    public string GenerateJwtToken(User user);
}
