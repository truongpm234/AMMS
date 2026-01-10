using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.User;

namespace AMMS.Application.Interfaces
{
    public interface IUserService
    {
        Task<UserLoginResponseDto?> Login(UserLoginRequestDto request);

        Task<UserRegisterResponseDto> Register(UserRegisterRequestDto request, string otp);

        Task<user?> GetUserForGoogleAuth(string email, string name);
    }
}
