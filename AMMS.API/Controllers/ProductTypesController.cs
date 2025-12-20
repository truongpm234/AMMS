using AMMS.Application.Interfaces;
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
    }
}
