using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.ProductTemplates;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductTemplatesController : ControllerBase
    {
        private readonly IProductTemplateService _service;

        public ProductTemplatesController(IProductTemplateService service)
        {
            _service = service;
        }

        [HttpGet("by-product-type/{productTypeId:int}")]
        [ProducesResponseType(typeof(List<ProductTemplateDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<ProductTemplateDto>>> GetByProductType(int productTypeId, CancellationToken ct)
        {
            var data = await _service.GetByProductTypeIdAsync(productTypeId, ct);
            return Ok(data);
        }

        [HttpGet("get-all-templates")]
        [ProducesResponseType(typeof(List<ProductTemplateDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<ProductTemplateDto>>> GetAllTemplates(CancellationToken ct)
        {
            var data = await _service.GetAllAsync(ct);
            return Ok(data);
        }

        [HttpGet("get-paper-stock")]
        [ProducesResponseType(typeof(ProductTemplatesWithPaperStockResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductTemplatesWithPaperStockResponse>> GetByProductTypeWithPaperStock(CancellationToken ct)
        {
            var data = await _service.GetByProductTypeIdWithPaperStockAsync(ct);
            return Ok(data);
        }
    }
}
