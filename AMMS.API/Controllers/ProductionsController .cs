using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Productions;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductionsController : ControllerBase
    {
        private readonly IProductionService _service;
        private readonly IProductionSchedulingService _svc;

        public ProductionsController(IProductionService service, IProductionSchedulingService svc)
        {
            _service = service;
            _svc = svc;
        }

        [HttpPost("schedule")]
        public async Task<IActionResult> Schedule([FromBody] ScheduleRequest req)
        {
            var prodId = await _svc.ScheduleOrderAsync(
                orderId: req.order_id,
                productTypeId: req.product_type_id,
                productionProcessCsv: req.production_processes,
                managerId: req.manager_id
            );

            return Ok(new { prod_id = prodId });
        }

        [HttpGet("nearest-delivery")]
        public async Task<IActionResult> GetNearestDelivery()
        {
            var result = await _service.GetNearestDeliveryAsync();
            return Ok(result);
        }

        [HttpGet("get-all-process-type")]
        public async Task<ActionResult<List<string>>> GetAllProcessTypeAsync()
        {
            var data = await _service.GetAllProcessTypeAsync();
            return Ok(data);
        }

        [HttpGet("get-all-production")]
        public async Task<IActionResult> GetProducingOrders([FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
        {
            var result = await _service.GetProducingOrdersAsync(page, pageSize, ct);
            return Ok(result);
        }

        [HttpGet("detail/{orderId:int}")]
        public async Task<IActionResult> GetProductionDetail(int orderId, CancellationToken ct)
        {
            var result = await _service.GetProductionDetailByOrderIdAsync(orderId, ct);
            if (result == null) return NotFound();
            return Ok(result);
        }

        [HttpGet("progress/{prodId:int}")]
        public async Task<ActionResult<ProductionProgressResponse>> Progress(int prodId)
        {
            return Ok(await _service.GetProgressAsync(prodId));
        }

        [HttpGet("waste/{prodId:int}")]
        public async Task<IActionResult> GetWaste(int prodId, CancellationToken ct)
        {
            var result = await _service.GetProductionWasteAsync(prodId, ct);
            if (result == null) return NotFound();
            return Ok(result);
        }

        [HttpPost("start/{orderId:int}")]
        public async Task<IActionResult> StartProduction(int orderId, CancellationToken ct)
        {
            var ok = await _service.StartProductionByOrderIdAsync(orderId, ct);

            if (!ok)
                return NotFound(new { message = "Production not found for this orderId" });

            return NoContent();
        }
    }
}
