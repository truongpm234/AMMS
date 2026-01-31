using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.User;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;

namespace AMMS.Application.Services
{
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly IEmailService _emailService;
        private readonly IMemoryCache _cache;

        public UserService(IUserRepository userRepository, IEmailService emailService, IMemoryCache memoryCache)
        {
            _userRepository = userRepository;
            _emailService = emailService;
            _cache = memoryCache;
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

        public async Task<List<user>> GetAllUser()
        {
            return await _userRepository.GetAllUser();
        }

        public async Task ResetPasswordAsync(ResetPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.email) ||
                string.IsNullOrWhiteSpace(request.token) ||
                string.IsNullOrWhiteSpace(request.new_password))
            {
                throw new Exception("Dữ liệu không hợp lệ");
            }

            var email = request.email.Trim().ToLower();
            var cacheKey = $"RESET_PASSWORD::{email}";

            // ===== 1. Lấy data từ cache (object ẩn danh) =====
            if (!_cache.TryGetValue(cacheKey, out object cacheObj))
                throw new Exception("Link đặt lại mật khẩu đã hết hạn hoặc không hợp lệ");

            // ===== 2. Trích dữ liệu bằng reflection =====
            var tokenHashProp = cacheObj.GetType().GetProperty("TokenHash");
            if (tokenHashProp == null)
                throw new Exception("Token không hợp lệ");

            var cachedTokenHash = tokenHashProp.GetValue(cacheObj)?.ToString();
            if (string.IsNullOrEmpty(cachedTokenHash))
                throw new Exception("Token không hợp lệ");

            // ===== 3. Hash token FE gửi lên =====
            var incomingHash = Sha256($"{email}:{request.token}");

            if (!string.Equals(incomingHash, cachedTokenHash, StringComparison.Ordinal))
                throw new Exception("Token không hợp lệ");

            // ===== 4. Validate password =====
            //if (!IsValidPassword(request.new_password))
            //    throw new Exception("Mật khẩu không đủ mạnh");

            // ===== 5. Lấy user =====
            var user = await _userRepository.GetUserByEmail(email);

            if (user == null)
                throw new Exception("Tài khoản không tồn tại");

            // ===== 6. Update password =====
            user.password_hash = BCrypt.Net.BCrypt.HashPassword(request.new_password);
            _cache.Remove(cacheKey); // one-time token

            var res = await _userRepository.ResetPassword(request.new_password, email);
            if (!res)
            {
                throw new Exception("Đặt mật khẩu không thành công");
            }
        }

        // ===== Helpers =====

        private static string Sha256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private static bool IsValidPassword(string password)
        {
            if (password.Length < 8) return false;
            if (!password.Any(char.IsUpper)) return false;
            if (!password.Any(char.IsLower)) return false;
            if (!password.Any(char.IsDigit)) return false;
            if (!password.Any(c => !char.IsLetterOrDigit(c))) return false;
            return true;
        }
    }
}
