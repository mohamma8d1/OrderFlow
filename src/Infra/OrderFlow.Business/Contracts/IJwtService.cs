using OrderFlow.Common.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace OrderFlow.Business.Contracts;

public interface IJwtService
{
    public string GenerateJwtToken(User user);
}
