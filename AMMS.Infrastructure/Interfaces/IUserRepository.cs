using AMMS.Shared.DTOs.User;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IUserRepository
    {
        Task<UserLoginResponseDto?> GetUserByUsernamePassword(UserLoginRequestDto req);

        Task<UserRegisterResponseDto> CreateNewUser(UserRegisterRequestDto req);
    }
}
