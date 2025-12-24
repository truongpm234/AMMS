using AMMS.Application.Interfaces;
using AMMS.Application.Services;
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

        [HttpPut("adjust-cost/{estimateId}")]
        public async Task<IActionResult> AdjustCost(int estimateId, [FromBody] AdjustCostRequest req)
        {
            await _service.UpdateFinalCostAsync(estimateId, req.final_cost);
            return NoContent();
        }

        [HttpPost("process-cost-breakdown")]
        public async Task<IActionResult> ProcessCostBreakdown([FromBody] CostEstimateRequest req)
        {
            var res = await _service.CalculateProcessCostBreakdownAsync(req);
            return Ok(res);
        }

    }
}
