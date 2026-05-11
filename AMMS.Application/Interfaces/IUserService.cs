using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.User;

namespace AMMS.Application.Interfaces
{
    public interface IUserService
    {
        Task<UserLoginResponseDto?> Login(UserLoginRequestDto request);
        Task<UserRegisterResponseDto> Register(UserRegisterRequestDto request, string otp);
        Task<user?> GetUserForGoogleAuth(string email, string name);
        Task<user?> AdminCreateUpdateNewUser(UserUpdateCreateDto new_user, int? user_id);
        Task<List<user>> GetAllUser();
        Task ResetPasswordAsync(ResetPasswordRequest request);
        Task<user?> GetUserById(int user_id);
        Task<user?> UpdateProfileAsync(int userId,UpdateProfileDto dto, CancellationToken ct = default);
        Task<bool> AddAddressAsync(int userId, string address);
        Task<bool> DeleteAddressAsync(int userId, int index);
    }
}
