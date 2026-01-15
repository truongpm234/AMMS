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

        [HttpPost("qr")]
        public async Task<ActionResult<TaskQrResponse>> CreateQr([FromBody] CreateTaskQrRequest req)
        {
            var t = await _taskRepo.GetByIdAsync(req.task_id);
            if (t == null) return NotFound();

            var ttl = TimeSpan.FromMinutes(Math.Max(1, req.ttl_minutes));

            var token = _tokenSvc.CreateToken(req.task_id, ttl);
            var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();

            return new TaskQrResponse
            {
                task_id = req.task_id,
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
