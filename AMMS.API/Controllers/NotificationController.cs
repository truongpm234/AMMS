using AMMS.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private readonly NotificationService _notiService;

        public NotificationController(NotificationService service)
        {
            _notiService = service;
        }

        [HttpGet("get-noti-by-role-id")]
        public async Task<IActionResult> GetNotiByRoleId(int id, int? user_id)
        {
            if (user_id != null)
            {
                return Ok(await _notiService.GetConsultantNotiByUserId(id, user_id));
            }
            return Ok(await _notiService.GetNotiByRoleId(id));
        }

        [HttpGet("staff-get-noti")]
        public async Task<IActionResult> StaffGetNoti(string role)
        {
            return Ok(await _notiService.StaffGetNoti(role));
        }

        [HttpPut("mark-as-read/{id}")]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            try
            {
                var result = await _notiService.MarkAsRead(id);
                if (result)
                {
                    return Ok(new { message = "Đã đánh dấu là đã đọc" });
                }
                return BadRequest(new { message = "Không tìm thấy thông báo hoặc cập nhật thất bại" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("mark-all-read")]
        public async Task<IActionResult> MarkAllRead(int role_id, int? user_id)
        {
            try
            {
                var result = await _notiService.MarkAllAsRead(role_id, user_id);
                if (result)
                {
                    return Ok(new { message = "Đã đánh dấu tất cả là đã đọc" });
                }
                return BadRequest(new { message = "Không có thông báo nào để cập nhật hoặc cập nhật thất bại" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}
