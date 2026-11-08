using Microsoft.AspNetCore.Mvc;
using OrderFlow.Business.Contracts;
using OrderFlow.Common.DTOs.User;

namespace OrderFlow.Api.Controllers;

public class UserController(IUserBusiness userBusiness) : OrderFlowControllerBase
{
    [HttpPost("[action]")]
    public async Task<ActionResult<AuthTokenResponse>> Register(RegisterUserDto dto, CancellationToken cancellationToken) =>
        Ok(await userBusiness.RegisterAsync(dto, cancellationToken));

    [HttpPost("[action]")]
    public async Task<ActionResult<AuthTokenResponse>> Login(LoginUserDto dto, CancellationToken cancellationToken) =>
        Ok(await userBusiness.LoginAsync(dto, cancellationToken));
}
