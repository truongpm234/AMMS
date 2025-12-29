using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    using AMMS.Application.Interfaces;
    using AMMS.Infrastructure.Interfaces;
    using AMMS.Shared.DTOs.Productions;
    using Microsoft.AspNetCore.Mvc;

    [ApiController]
    [Route("api/[controller]")]
    public class TasksController : ControllerBase
    {
        private readonly ITaskRepository _taskRepo;
        private readonly ITaskQrTokenService _tokenSvc;
        private readonly ITaskScanService _svc;

        public TasksController(ITaskRepository taskRepo, ITaskQrTokenService tokenSvc, ITaskScanService svc)
        {
            _taskRepo = taskRepo;
            _tokenSvc = tokenSvc;
            _svc = svc;
        }

        [HttpPost("qr/{taskId:int}")]
        public async Task<ActionResult<TaskQrResponse>> CreateQr(int taskId, [FromQuery] int ttlMinutes = 60)
        {
            var t = await _taskRepo.GetByIdAsync(taskId);
            if (t == null) return NotFound();

            var ttl = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
            var token = _tokenSvc.CreateToken(taskId, ttl);

            var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();

            return new TaskQrResponse
            {
                task_id = taskId,
                token = token,
                expires_at_unix = expiresAt
            };
        }

        [HttpPost("finish")]
        public async Task<ActionResult<ScanTaskResult>> Finish([FromBody] ScanTaskRequest req)
        {
            var res = await _svc.ScanFinishAsync(req);
            return Ok(res);
        }
    }
}
