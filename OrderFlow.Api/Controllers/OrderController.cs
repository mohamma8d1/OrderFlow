using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OrderFlow.Application.DTOs.Order;
using OrderFlow.Application.DTOs.OrderItem;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class OrderController : ControllerBase
{
    [HttpPost]
    public Task<IActionResult<List<CreateOrderDto>>> Create(CreateOrderItemDto orderItem)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);


        return Ok();
    }
}
