using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MissingMaterialsController : ControllerBase
    {
        private readonly IMissingMaterialService _service;

        public MissingMaterialsController(IMissingMaterialService service)
        {
            _service = service;
        }

        // POST api/missingmaterials/recalculate
        [HttpPost("recalculate")]
        public async Task<IActionResult> Recalculate(CancellationToken ct = default)
        {
            var result = await _service.RecalculateAndSaveAsync(ct);
            return Ok(result);
        }

        // GET api/missingmaterials/paged?page=1&pageSize=10
        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetPagedAsync(page, pageSize, ct);
            return Ok(result);
        }
    }
}
