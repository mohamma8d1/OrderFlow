using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderFlow.Application.DTOs.User;
using OrderFlow.Application.Interfaces;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserController(ApplicationDbContext dbContext, IPasswordHasher<User> passwordHasher, IJwtService jwtService) : ControllerBase
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

        var accessToken = jwtService.GenerateJwtToken(user);

        return Ok(new { accessToken });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginUserDto dto)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);

        if (user is null)
            return Unauthorized("Invalid email or password.");


        var passwordVerificationResult = passwordHasher.VerifyHashedPassword(user,user.PasswordHash, dto.Password);

        if(passwordVerificationResult == PasswordVerificationResult.Failed)
            return Unauthorized("Invalid email or password.");

        var accessToken = jwtService.GenerateJwtToken(user);

        return Ok(new { accessToken });

    }

}
