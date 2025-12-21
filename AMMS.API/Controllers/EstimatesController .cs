using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EstimatesController : ControllerBase
    {
        private readonly IEstimateService _service;

        public EstimatesController(IEstimateService service)
        {
            _service = service;
        }

        [HttpPost("paper")]
        [ProducesResponseType(typeof(PaperEstimateResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> EstimatePaper([FromBody] PaperEstimateRequest req)
        {
            var result = await _service.EstimatePaperAsync(req);
            return Ok(result);
        }

        [HttpPost("cost")]
        public async Task<IActionResult> CalculateCost([FromBody] CostEstimateRequest req)
        {
            var result = await _service.CalculateCostEstimateAsync(req);
            return Ok(result);
        }

        [HttpPut("adjust-cost-by-discount/{id}")]
        public async Task<IActionResult> AdjustCost(int id, [FromBody] AdjustCostRequest req)
        {
            await _service.AdjustCostBaseOnDiscountAsync(id, (decimal)req.discount_percent, req.cost_note);
            return NoContent();
        }
    }
}
