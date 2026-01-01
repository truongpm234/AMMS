using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProductTypesController : ControllerBase
    {
        private readonly IProductTypeService _productTypeService;
        public ProductTypesController(IProductTypeService productTypeService)
        {
            _productTypeService = productTypeService;
        }

        [HttpGet("Get-All-Product-Types")]
        public async Task<IActionResult> GetAllProductTypes()
        {
            var productTypes = await _productTypeService.GetAllAsync();
            return Ok(productTypes);
        }

        [HttpGet("get-all-type-general")]
        public async Task<ActionResult<List<string>>> GetAllTypeGeneralAsync()
        {
            var data = await _productTypeService.GetAllTypeGeneralAsync();
            return Ok(data);
        }
        [HttpGet("get-all-form-type-of-hop-mau")]
        public async Task<ActionResult<List<string>>> GetAllTypeOfHop_MauAsync()
        {
            var data = await _productTypeService.GetAllTypeHop_MauAsync();
            return Ok(data);
        }

        [HttpGet("get-all-type-of-gach")]
        public async Task<ActionResult<List<string>>> GetAllTypeOfGachAsync()
        {
            var data = await _productTypeService.GetAllTypeFormGachAsync();
            return Ok(data);
        }

        // GET api/producttypes/{id}/detail
        [HttpGet("{id:int}/detail")]
        public async Task<IActionResult> GetDetail(int id, CancellationToken ct)
        {
            if (id <= 0) return BadRequest("productTypeId invalid");

            var data = await _productTypeService.GetDetailAsync(id, ct);
            if (data == null) return NotFound(new { message = "Product type not found" });

            return Ok(data);
        }
    }
}
