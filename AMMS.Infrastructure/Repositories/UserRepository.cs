using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.User;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _db;

        public UserRepository(AppDbContext db) => _db = db;

        public async Task<UserLoginResponseDto?> GetUserByUsernamePassword(UserLoginRequestDto req)
        {
            var user = await _db.users
                .SingleOrDefaultAsync(u => u.username == req.user_name || u.email == req.email);

            if (user == null)
                return null;

            bool isValidPassword = BCrypt.Net.BCrypt.Verify(
                req.password,
                user.password_hash
            );

            if (!isValidPassword)
                return null;

            return new UserLoginResponseDto
            {
                full_name = user.full_name,
                user_id = user.user_id,
                role_id = user.role_id
            };
        }

        public async Task<UserRegisterResponseDto> CreateNewUser(UserRegisterRequestDto req)
        {
            var pass_hash = BCrypt.Net.BCrypt.HashPassword(req.password);
            var newUser = new user();
            newUser.username = req.user_name;
            newUser.password_hash = pass_hash;
            newUser.full_name = req.full_name;
            newUser.created_at = DateTime.Now;
            newUser.is_active = true;
            newUser.role_id = 6;
            newUser.phone_number = req.phone_number;
            newUser.email = req.email;

            _db.users.Add(newUser);
            await _db.SaveChangesAsync();

            return new UserRegisterResponseDto
            {
                status = "201",
                user_id = newUser.user_id,
                full_name = newUser.full_name,
                role_id = newUser.role_id,
            };
        }

        public async Task<user?> GetUserForGoogleAuth(string email, string name)
        {
            var exitUser = _db.users.SingleOrDefault(u => u.email == email);
            var newUser = new user();
            if (exitUser == null)
            {
                newUser.email = email;
                newUser.username = email;
                newUser.password_hash = "null123";
                newUser.full_name = name;
                newUser.created_at = DateTime.Now;
                newUser.is_active = true;
                newUser.role_id = 6;
                _db.users.Add(newUser);
                await _db.SaveChangesAsync();
                return newUser;
            }
            return exitUser;
        }

    }
}
