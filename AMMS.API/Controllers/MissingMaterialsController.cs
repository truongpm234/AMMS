using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Shared.DTOs.Orders;
using ExcelDataReader;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;
using System.Text;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MissingMaterialsController : ControllerBase
    {
        private readonly IMissingMaterialService _service;
        private readonly AppDbContext _context;

        public MissingMaterialsController(
            IMissingMaterialService service,
            AppDbContext context)
        {
            _service = service;
            _context = context;
        }
        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetPagedAsync(page, pageSize, ct);
            return Ok(result);
        }

        [HttpGet("export-excel")]
        public async Task<IActionResult> ExportExcel(
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 200,
    CancellationToken ct = default)
        {
            try
            {
                var fileBytes = await _service.ExportExcelAsync(page, pageSize, ct);

                var fileName = $"missing-materials-{DateTime.Now:yyyyMMddHHmmss}.xlsx";

                return File(
                    fileBytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    fileName
                );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message
                });
            }
        }

        [HttpPost("import-stock-from-excel")]
        public async Task<IActionResult> ImportStockFromExcel(IFormFile file, CancellationToken ct = default)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest("File không hợp lệ");

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                using var stream = file.OpenReadStream();

                IExcelDataReader reader;

                string extension = Path.GetExtension(file.FileName).ToLower();

                if (extension == ".csv")
                {
                    reader = ExcelReaderFactory.CreateCsvReader(stream);
                }
                else
                {
                    reader = ExcelReaderFactory.CreateReader(stream);
                }

                var importRows = new List<ImportStockRow>();
                var invalidRows = new List<object>();

                using (reader)
                {
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                    {
                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                        {
                            UseHeaderRow = true
                        }
                    });

                    if (result.Tables.Count == 0)
                        return BadRequest("File không có dữ liệu");

                    DataTable table = result.Tables[0];

                    int rowIndex = 1;

                    foreach (DataRow row in table.Rows)
                    {
                        rowIndex++;

                        string materialIdText = GetCellValue(
                            row,
                            "material_id",
                            "Mã NVL",
                            "id",
                            "ID"
                        );

                        string quantityText = GetCellValue(
                            row,
                            "quantity",
                            "Số lượng cần nhập",
                            "qty",
                            "Qty"
                        );

                        if (!int.TryParse(materialIdText, out int materialId) || materialId <= 0)
                        {
                            invalidRows.Add(new
                            {
                                row = rowIndex,
                                reason = "material_id không hợp lệ",
                                material_id = materialIdText,
                                quantity = quantityText
                            });

                            continue;
                        }

                        decimal? quantity = ParseNullableDecimal(quantityText);

                        if (quantity == null || quantity <= 0)
                        {
                            invalidRows.Add(new
                            {
                                row = rowIndex,
                                reason = "quantity không hợp lệ hoặc <= 0",
                                material_id = materialIdText,
                                quantity = quantityText
                            });

                            continue;
                        }

                        importRows.Add(new ImportStockRow
                        {
                            MaterialId = materialId,
                            Quantity = quantity.Value
                        });
                    }
                }

                if (importRows.Count == 0)
                {
                    return BadRequest(new
                    {
                        message = "Không có dòng hợp lệ để import",
                        invalidRows
                    });
                }

                var groupedRows = importRows
                    .GroupBy(x => x.MaterialId)
                    .Select(g => new ImportStockRow
                    {
                        MaterialId = g.Key,
                        Quantity = g.Sum(x => x.Quantity)
                    })
                    .ToList();

                var materialIds = groupedRows
                    .Select(x => x.MaterialId)
                    .Distinct()
                    .ToList();

                var materials = await _context.materials
                    .Where(x => materialIds.Contains(x.material_id))
                    .ToListAsync(ct);

                int updatedCount = 0;

                var updatedRows = new List<object>();
                var notFoundRows = new List<object>();

                var successfulMaterialIds = new List<int>();

                foreach (var item in groupedRows)
                {
                    var material = materials.FirstOrDefault(x => x.material_id == item.MaterialId);

                    if (material == null)
                    {
                        notFoundRows.Add(new
                        {
                            material_id = item.MaterialId,
                            quantity = item.Quantity,
                            reason = "Không tìm thấy material_id trong bảng materials"
                        });

                        continue;
                    }

                    decimal oldStock = material.stock_qty ?? 0m;
                    decimal newStock = oldStock + item.Quantity;

                    material.stock_qty = newStock;

                    updatedCount++;
                    successfulMaterialIds.Add(material.material_id);

                    updatedRows.Add(new
                    {
                        material_id = material.material_id,
                        material_name = material.name,
                        imported_quantity = item.Quantity,
                        old_stock = oldStock,
                        new_stock = newStock
                    });
                }
                int updatedMissingStatusCount = 0;
                var updatedMissingRows = new List<object>();

                if (successfulMaterialIds.Count > 0)
                {
                    var missingRows = await _context.missing_materials
                        .Where(x =>
                            successfulMaterialIds.Contains(x.material_id) &&
                            x.quantity > 0m)
                        .ToListAsync(ct);

                    foreach (var missing in missingRows)
                    {
                        // Đã nhập kho thành công thì đánh dấu đã mua
                        missing.is_buy = true;

                        // Không xóa, không trừ quantity
                        // Chỉ chuyển trạng thái active sang false
                        missing.is_active = false;

                        updatedMissingStatusCount++;

                        updatedMissingRows.Add(new
                        {
                            miss_id = missing.miss_id,
                            material_id = missing.material_id,
                            material_name = missing.material_name,
                            quantity = missing.quantity,
                            total_price = missing.total_price,
                            is_buy = missing.is_buy,
                            is_active = missing.is_active
                        });
                    }
                }

                await _context.SaveChangesAsync(ct);

                return Ok(new
                {
                    message = "Import thành công",
                    updated = updatedCount,
                    updatedMissingStatus = updatedMissingStatusCount,
                    invalid = invalidRows.Count,
                    notFound = notFoundRows.Count,
                    invalidRows,
                    notFoundRows,
                    updatedRows,
                    updatedMissingRows
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message
                });
            }
        }

        private static string GetCellValue(DataRow row, params string[] columnNames)
        {
            foreach (var columnName in columnNames)
            {
                var column = row.Table.Columns
                    .Cast<DataColumn>()
                    .FirstOrDefault(c =>
                        string.Equals(
                            c.ColumnName.Trim(),
                            columnName.Trim(),
                            StringComparison.OrdinalIgnoreCase));

                if (column != null)
                    return row[column]?.ToString()?.Trim() ?? "";
            }

            return "";
        }

        private static decimal? ParseNullableDecimal(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                return result;

            if (decimal.TryParse(value, NumberStyles.Any, new CultureInfo("vi-VN"), out result))
                return result;

            value = value.Replace(",", ".");

            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return result;

            return null;
        }

        [HttpPost("generate-purchase-pdf")]
        public async Task<IActionResult> GeneratePurchasePdf(
    [FromBody] GenerateMissingMaterialPurchasePdfRequest request,
    CancellationToken ct = default)
        {
            try
            {
                if (request == null || request.miss_ids == null || request.miss_ids.Count == 0)
                {
                    return BadRequest(new
                    {
                        message = "Vui lòng chọn ít nhất một miss_id."
                    });
                }

                var result = await _service.GeneratePurchasePdfAsync(
                    request.miss_ids,
                    ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message
                });
            }
        }

        [HttpPost("recalculate")]
        public async Task<IActionResult> Recalculate(CancellationToken ct = default)
        {
            try
            {
                var result = await _service.RecalculateAsync(ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message
                });
            }
        }

        private sealed class ImportStockRow
        {
            public int MaterialId { get; set; }
            public decimal Quantity { get; set; }
        }
    }
}
