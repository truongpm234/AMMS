using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Google;
using AMMS.Shared.DTOs.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Tags("Auth")]
    public class UserController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly JWTService _jwt;
        private readonly GoogleAuthService _googleAuthService;

        public UserController(IUserService userService, JWTService jwt, GoogleAuthService googleAuthService)
        {
            _userService = userService;
            _jwt = jwt;
            _googleAuthService = googleAuthService;
        }

        [HttpPost("/login")]
        public async Task<IActionResult> Login([FromBody] UserLoginRequestDto request)
        {
            var user = await _userService.Login(request);
            if (user != null)
            {
                var token = _jwt.GenerateToken(user.user_id, user.role_id);
                user.jwt = token;
                return Ok(user);
            }
            return Unauthorized(new { message = "Email/UserName hoặc mật khẩu không đúng" });
        }


        [HttpPost("/login-with-google")]
        public async Task<IActionResult> GoogleLogin(GoogleLoginRequestDto req)
        {
            var payload = await _googleAuthService.VerifyToken(req.id_token);

            // TODO: check DB user theo payload.Email
            // TODO: nếu chưa có → tạo user
            if (payload.EmailVerified)
            {
                var token = _jwt.GenerateTokenForGoogle(
                payload.Email,
                payload.Name,
                payload.Subject
            );

                return Ok(new
                {
                    access_token = token,
                    email = payload.Email,
                    name = payload.Name,
                    avatar = payload.Picture
                });
            }
            return BadRequest();
        }

        [HttpPost("/register")]
        public async Task<UserRegisterResponseDto> Register([FromBody] UserRegisterRequestDto request, string otp)
        {
            return await _userService.Register(request, otp);
        }

        [Authorize(Policy = "admin")]
        [HttpPost("/admin-create-new-user")]
        public async Task<IActionResult> AdminCreateNewUser([FromBody] UserUpdateCreateDto new_user)
        {
            return Ok(await _userService.AdminCreateUpdateNewUser(new_user, null));
        }

        [Authorize(Policy = "admin")]
        [HttpPost("/admin-update-user/{user_id:int}")]
        public async Task<IActionResult> AdminUpdateUser([FromBody] UserUpdateCreateDto update_user, [FromRoute] int user_id)
        {
            return Ok(await _userService.AdminCreateUpdateNewUser(update_user, user_id));
        }
    }
}
