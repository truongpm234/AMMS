using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/productions")]
    public class ProductionsController : ControllerBase
    {
        private readonly IProductionService _service;

        public ProductionsController(IProductionService service)
        {
            _service = service;
        }
        /// <summary>
        /// Ngày giao gần nhất (để tính xưởng rảnh)
        /// </summary>
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
        public async Task<IActionResult> GetProducingOrders(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetProducingOrdersAsync(page, pageSize, ct);
            return Ok(result);
        }
    }
}
