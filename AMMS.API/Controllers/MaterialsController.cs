using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MaterialsController : ControllerBase
    {
        private readonly IMaterialService _materialService;

        public MaterialsController(IMaterialService materialService)
        {
            _materialService = materialService;
        }

        [HttpGet("get-material-by-{id}")]
        public async Task<IActionResult> GetMaterialById(int id)
        {
            var material = await _materialService.GetByIdAsync(id);
            if (material == null)
            {
                return NotFound(new { message = "Material not found" });
            }
            return Ok(material);
        }

        [HttpGet("get-all-materials")]
        public async Task<IActionResult> GetAllMaterials()
        {
            var materials = await _materialService.GetAllAsync();
            return Ok(materials);
        }

        [HttpGet("get-all-paper-type")]
        public async Task<ActionResult<List<string>>> GetAllPaperTypeAsync()
        {
            var data = await _materialService.GetAllPaperTypeAsync();
            return Ok(data);
        }

        [HttpGet("shortage-for-orders")]
        public async Task<IActionResult> GetShortageForAllOrders(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _materialService.GetShortageForAllOrdersPagedAsync(page, pageSize, ct);
            return Ok(result);
        }
    }
}
