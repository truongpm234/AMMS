using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Boms;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class MaterialService : IMaterialService
    {
        private readonly IMaterialRepository _materialRepository;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IRequestRepository _reqRepo;
        private readonly AppDbContext _db;
        public MaterialService(IMaterialRepository materialRepository, ICostEstimateRepository costEstimateRepository, IRequestRepository requestRepository, AppDbContext db)
        {
            _materialRepository = materialRepository;
            _estimateRepo = costEstimateRepository;
            _reqRepo = requestRepository;
            _db = db;
        }

        public async Task<List<material>> GetAllAsync()
        {
            return await _materialRepository.GetAll();
        }

        public async Task<List<material>> GetMaterialByTypeMangAsync()
        {
            return await _materialRepository.GetMaterialByTypeMangAsync();
        }

        public async Task<material?> GetByIdAsync(int id)
        {
            return await _materialRepository.GetByIdAsync(id);
        }

        public async Task UpdateAsync(material material)
        {
            await _materialRepository.GetByIdAsync(material.material_id);
            await _materialRepository.UpdateAsync(material);
            await _materialRepository.SaveChangeAsync();
        }

        public Task<MaterialTypePaperDto> GetAllPaperTypeAsync()
        {
            var res = _materialRepository.GetAllPaperTypeAsync();
            return res;
        }

        public async Task<List<material>> GetMaterialByTypeSongAsync()
        {
            return await _materialRepository.GetMaterialByTypeSongAsync();
        }

        public async Task<MaterialTypeGlueDto> GetAllPhuGlueTypeAsync()
        {
            return await _materialRepository.GetAllPhuGlueTypeAsync();
        }

        public async Task<MaterialTypeGlueDto> GetAllBoiGlueTypeAsync()
        {
            return await _materialRepository.GetAllBoiGlueTypeAsync();
        }

        public async Task<MaterialTypeGlueDto> GetAllDanGlueTypeAsync()
        {
            return await _materialRepository.GetAllDanGlueTypeAsync();
        }
        public Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
            int page, int pageSize, CancellationToken ct = default) =>
            _materialRepository.GetShortageForAllOrdersPagedAsync(page, pageSize, ct);
        public async Task<bool> IncreaseStockAsync(int materialId, decimal quantity)
        {
            if (quantity <= 0)
                throw new ArgumentException("Quantity phải lớn hơn 0.");

            return await _materialRepository.IncreaseStockAsync(materialId, quantity);
        }

        public async Task<bool> DecreaseStockAsync(int materialId, decimal quantity)
        {
            if (quantity <= 0)
                throw new ArgumentException("Quantity phải lớn hơn 0.");

            return await _materialRepository.DecreaseStockAsync(materialId, quantity);
        }

        public Task<PagedResultLite<MaterialStockAlertDto>> GetMaterialStockAlertsPagedAsync(
    int page, int pageSize, decimal nearMinThresholdPercent = 0.2m, CancellationToken ct = default) =>
    _materialRepository.GetMaterialStockAlertsPagedAsync(page, pageSize, nearMinThresholdPercent, ct);
        public async Task<OrderMaterialsResponse?> GetMaterialsByOrderIdAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var header = await (
                from o in _db.orders.AsNoTracking()

                join q in _db.quotes.AsNoTracking()
                    on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking()
                    on q.order_request_id equals r.order_request_id into rj
                from r in rj.DefaultIfEmpty()

                where o.order_id == orderId
                select new
                {
                    o,
                    r
                }
            ).FirstOrDefaultAsync(ct);

            if (header == null || header.r == null)
                return null;

            var o1 = header.o;
            var r1 = header.r;

            /*
             * Lấy order_item đầu tiên để fallback kích thước nếu request thiếu.
             * Dùng EF.Property để tránh lỗi nếu entity order_item của bạn chưa khai báo property width_mm/length_mm.
             */
            var itemShape = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.item_id,
                    x.quantity,
                    x.product_type_id,

                    item_length_mm = EF.Property<int?>(x, "length_mm"),
                    item_width_mm = EF.Property<int?>(x, "width_mm")
                })
                .FirstOrDefaultAsync(ct);

            /*
             * Kích thước sản phẩm:
             * Ưu tiên order_request vì đây là thông tin khách nhập/consultant chốt.
             * Fallback sang order_item nếu request thiếu length/width.
             */
            var productLengthMm = r1.product_length_mm ?? itemShape?.item_length_mm;
            var productWidthMm = r1.product_width_mm ?? itemShape?.item_width_mm;
            var productHeightMm = r1.product_height_mm;

            /*
             * Kích thước in:
             * Lấy trực tiếp từ order_request.
             */
            var printLengthMm = r1.print_length_mm;
            var printWidthMm = r1.print_width_mm;

            cost_estimate? ce1 = null;

            if (r1.accepted_estimate_id.HasValue && r1.accepted_estimate_id.Value > 0)
            {
                ce1 = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == r1.accepted_estimate_id.Value &&
                        x.order_request_id == r1.order_request_id,
                        ct);
            }

            ce1 ??= await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == r1.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            if (ce1 == null)
                return null;

            var displayPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                ce1.paper_alternative,
                ce1.paper_code);

            var displayWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                ce1.wave_alternative,
                ce1.wave_type);

            string? paperName = ce1.paper_name;

            if (!string.IsNullOrWhiteSpace(displayPaperCode))
            {
                paperName = await _db.materials
                    .AsNoTracking()
                    .Where(m => m.code == displayPaperCode)
                    .Select(m => m.name)
                    .FirstOrDefaultAsync(ct)
                    ?? ce1.paper_name
                    ?? displayPaperCode;
            }

            string? displayLaminationCode = ce1.lamination_material_code;
            string? displayLaminationName = ce1.lamination_material_name;

            if (string.IsNullOrWhiteSpace(displayLaminationName) &&
                !string.IsNullOrWhiteSpace(displayLaminationCode))
            {
                displayLaminationName = await _db.materials
                    .AsNoTracking()
                    .Where(m => m.code == displayLaminationCode)
                    .Select(m => m.name)
                    .FirstOrDefaultAsync(ct);
            }

            var items = new List<OrderMaterialLineDto>();

            if (!string.IsNullOrWhiteSpace(displayPaperCode))
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Giấy",
                    material_code = displayPaperCode,
                    material_name = paperName ?? displayPaperCode,
                    unit = "tờ",
                    quantity = ce1.sheets_total > 0
                        ? ce1.sheets_total
                        : ce1.sheets_required
                });
            }

            if (ce1.ink_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Mực",
                    material_code = "INK",
                    material_name = "Mực in",
                    unit = "kg",
                    quantity = ce1.ink_weight_kg
                });
            }

            if (ce1.coating_glue_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Keo phủ",
                    material_code = ce1.coating_material_code ?? ce1.coating_type,
                    material_name = ce1.coating_type ?? "Keo phủ",
                    unit = "kg",
                    quantity = ce1.coating_glue_weight_kg
                });
            }

            if (ce1.mounting_glue_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Keo bồi",
                    material_code = "KEO_BOI",
                    material_name = "Keo bồi",
                    unit = "kg",
                    quantity = ce1.mounting_glue_weight_kg
                });
            }

            if (ce1.lamination_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Màng cán",
                    material_code = displayLaminationCode,
                    material_name = displayLaminationName ?? "Màng cán",
                    unit = "kg",
                    quantity = ce1.lamination_weight_kg
                });
            }

            if (!string.IsNullOrWhiteSpace(displayWaveType))
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Sóng carton",
                    material_code = displayWaveType,
                    material_name = $"Sóng {displayWaveType}",
                    unit = "tờ",
                    quantity = ce1.wave_sheets_used ?? 0
                });
            }

            return new OrderMaterialsResponse
            {
                order_id = orderId,
                order_code = o1.code,
                order_request_id = r1.order_request_id,

                product_length_mm = productLengthMm,
                product_width_mm = productWidthMm,
                product_height_mm = productHeightMm,

                print_length_mm = printLengthMm,
                print_width_mm = printWidthMm,

                product_size_text = BuildProductSizeText(
                    productLengthMm,
                    productWidthMm,
                    productHeightMm),

                print_size_text = BuildPrintSizeText(
                    printLengthMm,
                    printWidthMm),

                items = items
            };
        }

        private static string? BuildProductSizeText(
    int? lengthMm,
    int? widthMm,
    int? heightMm)
        {
            if (!lengthMm.HasValue &&
                !widthMm.HasValue &&
                !heightMm.HasValue)
            {
                return null;
            }

            var lengthText = lengthMm.HasValue ? lengthMm.Value.ToString() : "?";
            var widthText = widthMm.HasValue ? widthMm.Value.ToString() : "?";

            /*
             * Nếu có chiều cao thì hiển thị Dài x Rộng x Cao.
             * Nếu không có chiều cao thì hiển thị Dài x Rộng.
             */
            if (heightMm.HasValue && heightMm.Value > 0)
            {
                return $"{lengthText} x {widthText} x {heightMm.Value} mm";
            }

            return $"{lengthText} x {widthText} mm";
        }

        private static string? BuildPrintSizeText(
            int? lengthMm,
            int? widthMm)
        {
            if (!lengthMm.HasValue && !widthMm.HasValue)
                return null;

            var lengthText = lengthMm.HasValue ? lengthMm.Value.ToString() : "?";
            var widthText = widthMm.HasValue ? widthMm.Value.ToString() : "?";

            return $"{lengthText} x {widthText} mm";
        }
    }
}
