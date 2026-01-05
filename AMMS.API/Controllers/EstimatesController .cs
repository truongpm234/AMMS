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
        [ProducesResponseType(typeof(CostEstimateWithProcessResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> CalculateCost([FromBody] CostEstimateRequest req)
        {
            // 1) Tính tổng chi phí (giấy + vật liệu + rush + discount...)
            var cost = await _service.CalculateCostEstimateAsync(req);

            // 2) Tính chi phí công đoạn (IN, PHU, CAN_MANG, BE, BOI, DAN...)
            var processCost = await _service.CalculateProcessCostBreakdownAsync(req);

            var response = new CostEstimateWithProcessResponse
            {
                cost = cost,
                process_cost = processCost
            };

            return Ok(response);
        }

        [HttpPut("adjust-cost/{orderRequestId:int}")]
        public async Task<IActionResult> AdjustCost(int orderRequestId, [FromBody] AdjustCostRequest req)
        {
            await _service.UpdateFinalCostAsync(orderRequestId, req.final_cost);
            return NoContent();
        }

        [HttpGet("deposit/by-request/{requestId:int}")]
        public async Task<IActionResult> GetDepositByRequestId(int requestId, CancellationToken ct)
        {
            var result = await _service.GetDepositByRequestIdAsync(requestId, ct);
            if (result == null)
                return NotFound(new { message = "Cost estimate not found for this requestId" });

            return Ok(result);
        }
    }
}
