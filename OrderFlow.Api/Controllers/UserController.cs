using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Application.DTOs.User;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserController(ApplicationDbContext dbContext, IPasswordHasher<User> passwordHasher, IConfiguration config) : ControllerBase
{

    [HttpPost("register")]
    public async Task<ActionResult> Register(RegisterUserDto dto)
    {
        var existEmail = await dbContext.Users.AnyAsync(u => u.Email == dto.Email);

        if(existEmail)
            return Conflict("Email already exists!!");


        var user = new User()
        {
            Id = Guid.NewGuid(),
            Email = dto.Email,
            FullName = dto.FullName,
            PhoneNumber = dto.PhoneNumber,  
            Role = "Customer",
            CreatedAt = DateTime.UtcNow,
        };

        user.PasswordHash = passwordHasher.HashPassword(user, dto.Password);

        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
  
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub , user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email)
        };

        claims.AddRange(new Claim(ClaimTypes.Role, user.Role));

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
            );

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new { accessToken });
    } 

}
