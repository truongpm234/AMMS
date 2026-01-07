using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.User;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly JWTService _jwt;

        public UserController(IUserService userService, JWTService jwt)
        {
            _userService = userService;
            _jwt = jwt;
        }

        [HttpPost("/login")]
        public async Task<UserLoginResponseDto?> Login([FromBody] UserLoginRequestDto request)
        {
            var user = await _userService.Login(request);
            if (user != null)
            {
                var token = _jwt.GenerateToken(user.user_id, user.role_id);
                user.jwt = token;
                return user;
            }
            return null;
        }

        [HttpPost("/register")]
        public async Task<UserRegisterResponseDto> Register([FromBody] UserRegisterRequestDto request, string otp)
        {
            return await _userService.Register(request, otp);
        }
    }
}
