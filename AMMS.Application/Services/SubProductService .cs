using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.SubProduct;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class SubProductService : ISubProductService
    {
        private readonly ISubProductRepository _repo;
        private readonly AppDbContext _db;
        private readonly ICloudinaryFileStorageService _cloudinaryStorage;

        public SubProductService(
            ISubProductRepository repo,
            AppDbContext db,
            ICloudinaryFileStorageService cloudinaryStorage)
        {
            _repo = repo;
            _db = db;
            _cloudinaryStorage = cloudinaryStorage;
        }

        public async Task<SubProductDto?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            var entity = await _repo.GetByIdAsync(id, ct);
            if (entity == null) return null;

            return new SubProductDto
            {
                id = entity.id,
                product_type_id = entity.product_type_id,
                product_type_name = entity.product_type?.name,

                width = entity.width,
                length = entity.length,
                product_process = entity.product_process,
                quantity = entity.quantity,

                is_active = entity.is_active,
                is_imported = entity.is_imported,
                import_file = entity.import_file,

                source_task_id = entity.source_task_id,
                source_task_log_id = entity.source_task_log_id,
                source_prod_id = entity.source_prod_id,
                source_order_id = entity.source_order_id,
                source_process_code = entity.source_process_code,

                paper_material_code = entity.paper_material_code,
                wave_material_code = entity.wave_material_code,
                coating_material_code = entity.coating_material_code,
                lamination_material_code = entity.lamination_material_code,
                material_signature = entity.material_signature,

                cost_estimate_id = entity.cost_estimate_id,
                unit_cost_to_stage = entity.unit_cost_to_stage,
                total_cost_to_stage = entity.total_cost_to_stage,

                imported_to_sub_product_id = entity.imported_to_sub_product_id,

                description = entity.description,
                updated_at = entity.updated_at
            };
        }

        public Task<PagedResultLite<SubProductDto>> GetPagedAsync(
            int page,
            int pageSize,
            bool? isActive = null,
            bool? isImported = null,
            CancellationToken ct = default)
            => _repo.GetPagedAsync(page, pageSize, isActive, isImported, ct);

        public async Task<CreateSubProductResponse> CreateAsync(
            CreateSubProductDto dto,
            CancellationToken ct = default)
        {
            if (dto.product_type_id <= 0)
                throw new ArgumentException("product_type_id must be > 0");

            var productTypeExists = await _repo.ProductTypeExistsAsync(dto.product_type_id, ct);
            if (!productTypeExists)
                throw new ArgumentException($"product_type_id {dto.product_type_id} does not exist");

            var processPath = NormalizeProcess(dto.product_process);

            var paperCode = NormalizeMaterialCode(dto.paper_material_code);
            var waveCode = NormalizeMaterialCode(dto.wave_material_code);
            var coatingCode = NormalizeMaterialCode(dto.coating_material_code);
            var laminationCode = NormalizeMaterialCode(dto.lamination_material_code);

            var unitCost = Math.Round(dto.unit_cost_to_stage ?? 0m, 4);
            var qty = dto.quantity ?? 0;

            var signature = !string.IsNullOrWhiteSpace(dto.material_signature)
                ? dto.material_signature.Trim()
                : SubProductCompatibilityHelper.BuildMaterialSignature(
                    paperCode,
                    waveCode,
                    coatingCode,
                    laminationCode);

            var entity = new sub_product
            {
                product_type_id = dto.product_type_id,
                width = dto.width,
                length = dto.length,
                product_process = string.IsNullOrWhiteSpace(processPath) ? null : processPath,
                quantity = qty,

                is_active = dto.is_active ?? true,
                is_imported = true,

                paper_material_code = NullIfEmpty(paperCode),
                wave_material_code = NullIfEmpty(waveCode),
                coating_material_code = NullIfEmpty(coatingCode),
                lamination_material_code = NullIfEmpty(laminationCode),
                material_signature = signature,

                unit_cost_to_stage = unitCost,
                total_cost_to_stage = Math.Round(unitCost * qty, 2),

                description = string.IsNullOrWhiteSpace(dto.description)
                    ? null
                    : dto.description.Trim(),

                updated_at = AppTime.NowVnUnspecified()
            };

            await _repo.AddAsync(entity, ct);
            await _repo.SaveChangesAsync(ct);

            return new CreateSubProductResponse
            {
                success = true,
                id = entity.id,
                message = "Created sub product successfully"
            };
        }

        public async Task<UpdateSubProductResponse> UpdateAsync(
            int id,
            UpdateSubProductDto dto,
            CancellationToken ct = default)
        {
            var entity = await _repo.GetByIdTrackingAsync(id, ct);
            if (entity == null)
            {
                return new UpdateSubProductResponse
                {
                    success = false,
                    message = "Sub product not found",
                    id = id
                };
            }

            if (dto.product_type_id.HasValue)
            {
                if (dto.product_type_id.Value <= 0)
                    throw new ArgumentException("product_type_id must be > 0");

                var productTypeExists = await _repo.ProductTypeExistsAsync(dto.product_type_id.Value, ct);
                if (!productTypeExists)
                    throw new ArgumentException($"product_type_id {dto.product_type_id.Value} does not exist");

                entity.product_type_id = dto.product_type_id.Value;
            }

            if (dto.width.HasValue)
                entity.width = dto.width;

            if (dto.length.HasValue)
                entity.length = dto.length;

            if (dto.product_process != null)
            {
                entity.product_process = string.IsNullOrWhiteSpace(dto.product_process)
                    ? null
                    : NormalizeProcess(dto.product_process);
            }

            if (dto.quantity.HasValue)
                entity.quantity = dto.quantity.Value;

            if (dto.is_active.HasValue)
                entity.is_active = dto.is_active.Value;

            if (dto.description != null)
            {
                entity.description = string.IsNullOrWhiteSpace(dto.description)
                    ? null
                    : dto.description.Trim();
            }

            entity.updated_at = AppTime.NowVnUnspecified();

            await _repo.SaveChangesAsync(ct);

            return new UpdateSubProductResponse
            {
                success = true,
                id = entity.id,
                message = "Updated sub product successfully"
            };
        }

        public async Task<DeleteSubProductResponse> DeleteAsync(
            int id,
            CancellationToken ct = default)
        {
            var entity = await _repo.GetByIdTrackingAsync(id, ct);
            if (entity == null)
            {
                return new DeleteSubProductResponse
                {
                    success = false,
                    id = id,
                    message = "Sub product not found"
                };
            }

            _repo.Remove(entity);
            await _repo.SaveChangesAsync(ct);

            return new DeleteSubProductResponse
            {
                success = true,
                id = id,
                message = "Deleted sub product successfully"
            };
        }

        public async Task<SubProductImportReceiptBatchResponseDto> GenerateImportReceiptsAsync(
    List<int> subProductIds,
    CancellationToken ct = default)
        {
            subProductIds = (subProductIds ?? new List<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (subProductIds.Count == 0)
                throw new ArgumentException("sub_product_ids is required");

            var items = await _db.sub_products
                .Include(x => x.product_type)
                .Where(x => subProductIds.Contains(x.id))
                .OrderBy(x => x.id)
                .ToListAsync(ct);

            if (items.Count == 0)
                throw new InvalidOperationException("Không tìm thấy sub_product để tạo phiếu nhập bán thành phẩm.");

            var foundIds = items
                .Select(x => x.id)
                .ToHashSet();

            var missingIds = subProductIds
                .Where(x => !foundIds.Contains(x))
                .ToList();

            if (missingIds.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Không tìm thấy sub_product: {string.Join(", ", missingIds)}");
            }

            var now = AppTime.NowVnUnspecified();
            var receiptNo = $"PNBTP-{now:yyyyMMddHHmmss}";
            var fileName = $"{receiptNo}.pdf";
            var publicId = $"sub-product-imports/{receiptNo}";

            var pdfBytes = SubProductImportReceiptPdfHelper.GeneratePdf(
                items,
                receiptNo,
                now);

            string cloudUrl;

            await using (var stream = new MemoryStream(pdfBytes))
            {
                cloudUrl = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                    stream,
                    fileName,
                    "application/pdf",
                    publicId);
            }

            foreach (var item in items)
            {
                item.import_file = cloudUrl;
                item.updated_at = now;
            }

            await _db.SaveChangesAsync(ct);

            return new SubProductImportReceiptBatchResponseDto
            {
                success = true,
                import_file = cloudUrl,
                file_name = fileName,
                total_selected = items.Count,

                items = items.Select(x => new SubProductImportReceiptItemDto
                {
                    sub_product_id = x.id,
                    product_type_id = x.product_type_id,
                    product_type_name = x.product_type != null ? x.product_type.name : null,

                    width = x.width,
                    length = x.length,
                    product_process = x.product_process,
                    quantity = x.quantity,

                    is_active = x.is_active,
                    is_imported = x.is_imported,
                    import_file = x.import_file,

                    paper_material_code = x.paper_material_code,
                    wave_material_code = x.wave_material_code,
                    coating_material_code = x.coating_material_code,
                    lamination_material_code = x.lamination_material_code,
                    material_signature = x.material_signature,

                    cost_estimate_id = x.cost_estimate_id,
                    unit_cost_to_stage = x.unit_cost_to_stage,
                    total_cost_to_stage = x.total_cost_to_stage
                }).ToList(),

                message = $"Tạo phiếu nhập bán thành phẩm thành công cho {items.Count} dòng."
            };
        }

        public async Task<ImportPendingSubProductsResponseDto> ImportPendingSubProductsAsync(
    List<int>? ids = null,
    CancellationToken ct = default)
        {
            ids = ids?
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var now = AppTime.NowVnUnspecified();

            var query = _db.sub_products
                .Where(x =>
                    x.is_active == false &&
                    x.is_imported == false &&
                    x.quantity > 0);

            if (ids != null && ids.Count > 0)
                query = query.Where(x => ids.Contains(x.id));

            var pendings = await query
                .OrderBy(x => x.updated_at)
                .ThenBy(x => x.id)
                .ToListAsync(ct);

            var response = new ImportPendingSubProductsResponseDto
            {
                success = true,
                total_pending = pendings.Count
            };

            if (pendings.Count == 0)
            {
                response.message = "Không có bán thành phẩm pending cần nhập kho.";
                return response;
            }

            foreach (var pending in pendings)
            {
                pending.product_process = NormalizeProcess(pending.product_process);

                pending.material_signature = string.IsNullOrWhiteSpace(pending.material_signature)
                    ? SubProductCompatibilityHelper.BuildMaterialSignature(
                        pending.paper_material_code,
                        pending.wave_material_code,
                        pending.coating_material_code,
                        pending.lamination_material_code)
                    : pending.material_signature;

                pending.unit_cost_to_stage = Math.Round(pending.unit_cost_to_stage, 4);
                pending.total_cost_to_stage = Math.Round(pending.unit_cost_to_stage * pending.quantity, 2);

                var active = await FindActiveMatchedSubProductForImportAsync(
                    pending,
                    ct);

                if (active == null)
                {
                    pending.is_active = true;
                    pending.is_imported = true;
                    pending.imported_to_sub_product_id = null;
                    pending.updated_at = now;

                    response.activated_count++;

                    response.items.Add(new ImportPendingSubProductItemDto
                    {
                        source_sub_product_id = pending.id,
                        target_sub_product_id = pending.id,
                        quantity_imported = pending.quantity,
                        action = "ACTIVATED",
                        message = "Không có dòng active trùng signature, chuyển chính dòng pending thành tồn kho active."
                    });

                    continue;
                }

                var importQty = pending.quantity;

                active.quantity += importQty;
                active.total_cost_to_stage = Math.Round(active.unit_cost_to_stage * active.quantity, 2);
                active.updated_at = now;

                pending.quantity = 0;
                pending.is_active = false;
                pending.is_imported = true;
                pending.imported_to_sub_product_id = active.id;
                pending.updated_at = now;

                response.merged_count++;

                response.items.Add(new ImportPendingSubProductItemDto
                {
                    source_sub_product_id = pending.id,
                    target_sub_product_id = active.id,
                    quantity_imported = importQty,
                    action = "MERGED",
                    message = $"Đã gộp vào sub_product active id={active.id} vì trùng product_type, size, path, NVL signature và unit_cost."
                });
            }

            await _db.SaveChangesAsync(ct);

            response.message =
                $"Đã xử lý {pendings.Count} dòng pending. " +
                $"Kích hoạt mới={response.activated_count}, gộp={response.merged_count}.";

            return response;
        }

        private static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? NormalizeMaterialCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var s = RemoveDiacritics(value)
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");

            s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

            return s switch
            {
                "NONE" => null,
                "NULL" => null,

                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "KEO_DAU" => "KEO_PHU_DAU",
                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                "MANG_12_MIC" => "MANG_12MIC",
                "MANG_12MIC" => "MANG_12MIC",

                _ => s
            };
        }

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var chars = normalized
                .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                             != System.Globalization.UnicodeCategory.NonSpacingMark)
                .ToArray();

            return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
        }

        private async Task<sub_product?> FindActiveMatchedSubProductForImportAsync(
    sub_product pending,
    CancellationToken ct)
        {
            var process = NormalizeProcess(pending.product_process);

            var materialSignature = string.IsNullOrWhiteSpace(pending.material_signature)
                ? SubProductCompatibilityHelper.BuildMaterialSignature(
                    pending.paper_material_code,
                    pending.wave_material_code,
                    pending.coating_material_code,
                    pending.lamination_material_code)
                : pending.material_signature;

            var pendingUnitCost = Math.Round(pending.unit_cost_to_stage, 4);

            var candidates = await _db.sub_products
                .Where(x =>
                    x.id != pending.id &&
                    x.is_active == true &&
                    x.is_imported == true &&
                    x.product_type_id == pending.product_type_id &&
                    x.width == pending.width &&
                    x.length == pending.length &&
                    x.quantity >= 0)
                .ToListAsync(ct);

            return candidates.FirstOrDefault(x =>
            {
                var candidateProcess = NormalizeProcess(x.product_process);

                var candidateSignature = string.IsNullOrWhiteSpace(x.material_signature)
                    ? SubProductCompatibilityHelper.BuildMaterialSignature(
                        x.paper_material_code,
                        x.wave_material_code,
                        x.coating_material_code,
                        x.lamination_material_code)
                    : x.material_signature;

                var candidateUnitCost = Math.Round(x.unit_cost_to_stage, 4);

                return
                    string.Equals(candidateProcess, process, StringComparison.OrdinalIgnoreCase)
                    &&
                    string.Equals(candidateSignature, materialSignature, StringComparison.OrdinalIgnoreCase)
                    &&
                    candidateUnitCost == pendingUnitCost;
            });
        }

        private static string NormalizeProcess(string? value)
        {
            return NormalizeProcessPath(value);
        }

        private static string NormalizeProcessPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return string.Join(",",
                value.Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToUpperInvariant().Replace(" ", "_").Replace("-", "_"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
}