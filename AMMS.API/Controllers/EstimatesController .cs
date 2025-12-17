using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EstimatesController : ControllerBase
    {
        private readonly IPaperEstimateService _service;
        public EstimatesController(IPaperEstimateService service) => _service = service;

        [HttpPost("paper")]
        public async Task<IActionResult> EstimatePaper([FromBody] PaperEstimateRequest req)
        {
            var result = await _service.EstimatePaperAsync(req);
            return Ok(result);
        }
    }
}
