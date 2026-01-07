using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.User;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly IUserService _userService;

        public UserController(IUserService userService)
        {
            _userService = userService;
        }

        [HttpPost("/login")]
        public async Task<UserLoginResponseDto?> Login([FromBody] UserLoginRequestDto request)
        {
            return await _userService.Login(request);
        }

        [HttpPost("/register")]
        public async Task<UserRegisterResponseDto> Register([FromBody] UserRegisterRequestDto request, string otp)
        {
            return await _userService.Register(request, otp);
        }
    }
}
