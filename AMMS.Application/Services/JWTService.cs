using AMMS.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace AMMS.Application.Services
{
    public class JWTService
    {
        private readonly IConfiguration _config;
        private readonly IUserService _userService;

        public JWTService(IConfiguration config, IUserService userService)
        {
            _config = config;
            _userService = userService;
        }

        public string GenerateToken(int userId, int? roleId)
        {
            var claims = new[]
            {
            new Claim("user_id", userId.ToString()),
            new Claim("roleid", roleId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_config["Jwt:Key"])
            );

            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(
                    int.Parse(_config["Jwt:ExpireMinutes"])
                ),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task<string> GenerateTokenForGoogle(string email, string name, string googleId)
        {
            var user = await _userService.GetUserForGoogleAuth(email, name);
            var claims = new[]
            {
                 new Claim(ClaimTypes.Email, email),
                 new Claim(ClaimTypes.Name, name),
                 new Claim("roleid", user.role_id.ToString()),
                 new Claim("GoogleId", googleId)
            };


            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_config["Jwt:Key"])
            );

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(2),
                signingCredentials: new SigningCredentials(
                    key, SecurityAlgorithms.HmacSha256
                )
            );
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
