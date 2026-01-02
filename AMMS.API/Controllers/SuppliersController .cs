using AMMS.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SuppliersController : ControllerBase
    {
        private readonly ISupplierService _service;

        public SuppliersController(ISupplierService service)
        {
            _service = service;
        }

        /// <summary>
        /// Lấy danh sách supplier (phân trang) kèm theo materials
        /// có main_material_type trùng với supplier.main_material_type
        /// </summary>
        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged(
            [FromQuery] int page,
            [FromQuery] int pageSize,
            CancellationToken ct)
        {
            var result = await _service.GetPagedAsync(page, pageSize, ct);
            return Ok(result);
        }

        /// <summary>
        /// Lấy chi tiết 1 supplier + lịch sử mua materials (phân trang).
        /// </summary>
        [HttpGet("detail/{id:int}")]
        public async Task<IActionResult> GetSupplierDetail(
            int id,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetSupplierDetailWithMaterialsAsync(id, page, pageSize, ct);
            if (result == null)
                return NotFound(new { message = "Supplier not found", supplierId = id });

            return Ok(result);
        }

        [HttpGet("suppliers-by-material-id")]
        public async Task<IActionResult> GetSupplierByMaterialId(int id)
        {
            var res = await _service.ListSupplierByMaterialId(id);
            return Ok(res);
        }
    }
}
