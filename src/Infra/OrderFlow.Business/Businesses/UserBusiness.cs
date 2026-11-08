using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Business.Contracts;
using OrderFlow.Business.Exceptions;
using OrderFlow.Common.DTOs.User;
using OrderFlow.Common.Entities;
using OrderFlow.DataAccess.Core;

namespace OrderFlow.Business.Businesses;

public class UserBusiness(
    ApplicationDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IJwtService jwtService) : IUserBusiness
{
    public async Task<AuthTokenResponse> RegisterAsync(RegisterUserDto dto, CancellationToken cancellationToken = default)
    {
        var emailExists = await dbContext.Users.AnyAsync(u => u.Email == dto.Email, cancellationToken);
        if (emailExists)
            throw BusinessException.Conflict("Email already exists!!");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = dto.Email,
            FullName = dto.FullName,
            PhoneNumber = dto.PhoneNumber,
            Role = "Customer"
        };

        user.PasswordHash = passwordHasher.HashPassword(user, dto.Password);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResponse(jwtService.GenerateJwtToken(user));
    }

    public async Task<AuthTokenResponse> LoginAsync(LoginUserDto dto, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == dto.Email, cancellationToken)
            ?? throw BusinessException.Unauthorized("Invalid email or password.");

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, dto.Password);
        if (result == PasswordVerificationResult.Failed)
            throw BusinessException.Unauthorized("Invalid email or password.");

        return new AuthTokenResponse(jwtService.GenerateJwtToken(user));
    }
}
