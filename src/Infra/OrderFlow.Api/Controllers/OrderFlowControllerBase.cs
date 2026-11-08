using Microsoft.AspNetCore.Mvc;
using OrderFlow.Business.Exceptions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace OrderFlow.Api.Controllers;

[ApiController]
[Route("[controller]")]
public abstract class OrderFlowControllerBase : ControllerBase
{
    /// <summary>The signed-in user's id (JWT "sub"). A token without a valid id gets 401.</summary>
    protected Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            ? userId
            : throw BusinessException.Unauthorized();
}
