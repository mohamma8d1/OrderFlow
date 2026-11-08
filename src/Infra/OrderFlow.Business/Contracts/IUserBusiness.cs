using OrderFlow.Common.DTOs.User;

namespace OrderFlow.Business.Contracts;

public interface IUserBusiness
{
    Task<AuthTokenResponse> RegisterAsync(RegisterUserDto dto, CancellationToken cancellationToken = default);
    Task<AuthTokenResponse> LoginAsync(LoginUserDto dto, CancellationToken cancellationToken = default);
}
