using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.User;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
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

        public async Task<user?> GetUserById(int user_id)
        {
            return await _userRepository.GetByIdAsync(user_id);
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

        public async Task<user?> UpdateProfileAsync(int userId, UpdateProfileDto dto, CancellationToken ct = default)
        {
            return await _userRepository.UpdateProfileAsync(userId, dto, ct);
        }

        public async Task<bool> AddAddressAsync(int userId, string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new Exception("Address không được để trống");

            return await _userRepository.AddAddressAsync(userId, address);
        }

        public async Task<bool> DeleteAddressAsync(int userId, int index)
        {
            return await _userRepository.DeleteAddressAsync(userId, index);
        }
    }
}
