using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.DTOs.User;
using OrderFlow.Domain.Entities;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserController(ApplicationDbContext dbContext, IPasswordHasher<User> passwordHasher) : ControllerBase
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

        return Ok();
    } 

}
