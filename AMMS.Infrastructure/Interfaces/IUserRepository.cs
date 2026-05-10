using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.User;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IUserRepository
    {
        Task<UserLoginResponseDto?> GetUserByUsernamePassword(UserLoginRequestDto req);
        Task<UserRegisterResponseDto> CreateNewUser(UserRegisterRequestDto req);
        Task<user?> GetUserForGoogleAuth(string email, string name);
        Task<user?> UpdateCreateUser(UserUpdateCreateDto new_user, int? user_id);
        Task<List<user>> GetAllUser();
        Task<user?> GetUserByEmail(string email);
        Task<bool> ResetPassword(string newPassword, string email);
        Task<user?> GetByIdAsync(int userId, CancellationToken ct = default);
        Task<AssignedConsultantSummaryDto?> GetAssignedConsultantSummaryAsync(int userId, CancellationToken ct = default);
        Task<string?> GetPhoneNumberByUserIdAsync(int userId, CancellationToken ct = default);
        Task<user?> UpdateProfileAsync(int userId, UpdateProfileDto dto, CancellationToken ct = default);
    }
}
