using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Google;
using AMMS.Shared.DTOs.User;
using ExcelDataReader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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
        private readonly IEmailService _emailService;
        private readonly AppDbContext _db;

        public UserController(IUserService userService, JWTService jwt, GoogleAuthService googleAuthService, IEmailService emailService, AppDbContext dbContext)
        {
            _userService = userService;
            _jwt = jwt;
            _googleAuthService = googleAuthService;
            _emailService = emailService;
            _db = dbContext;
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

        //cmt for add again
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

        [Authorize(Policy = "admin")]
        [HttpGet("/get-all-user")]
        public async Task<IActionResult> AdminGetAllUser()
        {
            return Ok(await _userService.GetAllUser());
        }

        [HttpPost("/reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
        {
            await _userService.ResetPasswordAsync(req);
            return Ok(new { message = "Đặt lại mật khẩu thành công" });
        }

        [HttpPost("/forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] string email)
        {
            await _emailService.SendMailResetPassword(email);
            return Ok();
        }

        [HttpGet("/get-user-by-id/{user_id:int}")]
        public async Task<IActionResult> GetUserById([FromRoute] int user_id)
        {
            var user = await _userService.GetUserById(user_id);
            if (user != null)
            {
                return Ok(user);
            }
            return NotFound();
        }

        [HttpPost("upload-users")]
        public async Task<IActionResult> UploadUsers(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File không hợp lệ");

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var users = new List<user>();
            var errors = new List<object>();

            var roleMap = new Dictionary<string, int>()
            {
                {"admin",1},
                {"consultant",2},
                {"manager",3},
                {"warehouse_manager",4},
                {"user",5},
                {"production_manager",6},
                {"staff_ralo",7},
                {"staff_cat",8},
                {"staff_in",9},
                {"staff_phu",10},
                {"staff_can",11},
                {"staff_boi",12},
                {"staff_be",13},
                {"staff_dut",14},
                {"staff_dan",15},
            };

            using var stream = file.OpenReadStream();

            ExcelDataReader.IExcelDataReader reader;

            try
            {
                if (file.FileName.EndsWith(".xls"))
                {
                    reader = ExcelDataReader.ExcelReaderFactory.CreateBinaryReader(stream);
                }
                else if (file.FileName.EndsWith(".xlsx"))
                {
                    reader = ExcelDataReader.ExcelReaderFactory.CreateOpenXmlReader(stream);
                }
                else if (file.FileName.EndsWith(".csv"))
                {
                    reader = ExcelDataReader.ExcelReaderFactory.CreateCsvReader(stream);
                }
                else
                {
                    return BadRequest("Chỉ hỗ trợ file .xls, .xlsx, .csv");
                }
            }
            catch
            {
                return BadRequest("File không đúng định dạng Excel hoặc bị lỗi");
            }

            var result = reader.AsDataSet(new ExcelDataReader.ExcelDataSetConfiguration()
            {
                ConfigureDataTable = (_) => new ExcelDataReader.ExcelDataTableConfiguration()
                {
                    UseHeaderRow = true
                }
            });

            var table = result.Tables[0];

            for (int i = 0; i < table.Rows.Count; i++)
            {
                var row = table.Rows[i];

                try
                {
                    var username = row["username"]?.ToString()?.Trim();
                    var password = row["password"]?.ToString()?.Trim();
                    var fullName = row["full_name"]?.ToString()?.Trim();
                    var roleRaw = row["role_id"]?.ToString()?.Trim()?.ToLower();
                    var email = row["email"]?.ToString()?.Trim();
                    var phone = row["phone_number"]?.ToString()?.Trim();
                    var isActiveRaw = row["is_active"]?.ToString()?.Trim()?.ToLower();

                    if (string.IsNullOrEmpty(username))
                        throw new Exception("Username không được trống");

                    if (string.IsNullOrEmpty(password))
                        throw new Exception("Password không được trống");

                    var isActive = isActiveRaw == "true" || isActiveRaw == "1";

                    int roleId;

                    // ✅ role dạng số
                    if (int.TryParse(roleRaw, out int parsedRoleId))
                    {
                        if (parsedRoleId < 1 || parsedRoleId > 15)
                            throw new Exception("Role ID không hợp lệ");

                        roleId = parsedRoleId;
                    }
                    // ✅ role dạng string
                    else
                    {
                        if (!roleMap.ContainsKey(roleRaw))
                            throw new Exception($"Role '{roleRaw}' không hợp lệ");

                        roleId = roleMap[roleRaw];
                    }

                    var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

                    var existing = await _db.users
                        .FirstOrDefaultAsync(x => x.username == username);

                    if (existing != null)
                    {
                        existing.full_name = fullName;
                        existing.role_id = roleId;
                        existing.email = email;
                        existing.phone_number = phone;
                        existing.is_active = isActive;

                        if (!string.IsNullOrEmpty(password))
                            existing.password_hash = passwordHash;
                    }
                    else
                    {
                        users.Add(new user
                        {
                            username = username,
                            password_hash = passwordHash,
                            full_name = fullName,
                            role_id = roleId,
                            email = email,
                            phone_number = phone,
                            is_active = isActive,
                            created_at = DateTime.Now
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new
                    {
                        row = i + 2,
                        error = ex.Message
                    });
                }
            }

            if (errors.Any())
            {
                return BadRequest(new
                {
                    message = "Import có lỗi",
                    errors
                });
            }

            if (users.Any())
                await _db.users.AddRangeAsync(users);

            await _db.SaveChangesAsync();

            return Ok("Upload thành công");
        }

        [Authorize]
        [HttpPut("/update-profile")]
        public async Task<IActionResult> UpdateProfile(
    [FromBody] UpdateProfileDto dto,
    CancellationToken ct)
        {
            var userIdClaim = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
                return Unauthorized();

            var userId = int.Parse(userIdClaim);

            var updatedUser = await _userService.UpdateProfileAsync(userId, dto, ct);

            if (updatedUser == null)
                return NotFound(new
                {
                    message = "User không tồn tại"
                });

            return Ok(new
            {
                message = "Cập nhật profile thành công",
                data = new
                {
                    updatedUser.user_id,
                    updatedUser.full_name,
                    updatedUser.phone_number,
                    updatedUser.address,
                    updatedUser.email,
                    updatedUser.username
                }
            });
        }

        [Authorize]
        [HttpPost("add-address")]
        public async Task<IActionResult> AddAddress([FromBody] AddAddressDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userIdClaim))
                return Unauthorized();

            int userId = int.Parse(userIdClaim);

            var result = await _userService.AddAddressAsync(userId, dto.address);

            if (!result)
                return NotFound(new { message = "User không tồn tại" });

            return Ok(new
            {
                message = "Thêm địa chỉ thành công"
            });
        }

        [Authorize]
        [HttpDelete("delete-address")]
        public async Task<IActionResult> DeleteAddress([FromBody] DeleteAddressDto dto)
        {
            var userIdClaim = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userIdClaim))
            {
                return Unauthorized(new
                {
                    message = "User not found"
                });
            }

            var success = await _userService.DeleteAddressAsync(
                int.Parse(userIdClaim),
                dto.index
            );

            if (!success)
            {
                return BadRequest(new
                {
                    message = "Invalid address index"
                });
            }

            return Ok(new
            {
                message = "Delete address successfully"
            });
        }
    }
}
