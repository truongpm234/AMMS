using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Planning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EstimatesController : ControllerBase
    {
        private readonly IEstimateService _service;
        private readonly IEstimateBaseConfigService _baseConfig;
        private readonly IOrderPlanningService _planning;

        public EstimatesController(IEstimateService service, IEstimateBaseConfigService baseConfig, IOrderPlanningService planning)
        {
            _service = service;
            _baseConfig = baseConfig;
            _planning = planning;
        }

        [HttpGet("estimate-finish/{orderRequestId:int}")]
        public async Task<ActionResult<EstimateFinishDateResponse>> EstimateFinish(int orderRequestId, CancellationToken ct)
        {
            var res = await _planning.EstimateFinishByOrderRequestAsync(orderRequestId, ct);
            if (res == null) return NotFound();
            return Ok(res);
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

        [HttpGet("base-config")]
        [ProducesResponseType(typeof(EstimateBaseConfigDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetBaseConfig(CancellationToken ct)
        {
            var cfg = await _baseConfig.GetAsync(ct);
            return Ok(cfg);
        }

        [HttpPost("cost-save")]
        public async Task<IActionResult> SaveFeCost([FromBody] CostEstimateInsertRequest req, CancellationToken ct)
        {
            if (req.order_request_id <= 0)
                return BadRequest(new { message = "order_request_id is required and must be greater than 0" });

            var exists = await _service.OrderRequestExistsAsync(req.order_request_id);
            if (!exists)
                return NotFound(new { message = $"Order request with id {req.order_request_id} not found" });

            await _service.SaveFeCostEstimateAsync(req, ct);

            return NoContent();
        }
    }
}
