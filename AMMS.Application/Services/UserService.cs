using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.User;

namespace AMMS.Application.Services
{
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly IEmailService _emailService;

        public UserService(IUserRepository userRepository, IEmailService emailService)
        {
            _userRepository = userRepository;
            _emailService = emailService;
        }

        public async Task<UserLoginResponseDto?> Login(UserLoginRequestDto request)
        {
            return await _userRepository.GetUserByUsernamePassword(request);
        }

        public async Task<UserRegisterResponseDto> Register(UserRegisterRequestDto request, string otp)
        {
            //await _emailService.SendOtpAsync(request.email);

            if (await _emailService.VerifyOtpAsync(request.email, otp))
            {
                return await _userRepository.CreateNewUser(request);
            }
            return new UserRegisterResponseDto
            {
                status = "Register Failed",
            };
        }

        public async Task<user?> GetUserForGoogleAuth(string email, string name)
        {
            return await _userRepository.GetUserForGoogleAuth(email, name);
        }

        public async Task<user?> AdminCreateUpdateNewUser(UserUpdateCreateDto new_user, int? user_id)
        {
            return await _userRepository.UpdateCreateUser(new_user, user_id);
        }
    }
}
