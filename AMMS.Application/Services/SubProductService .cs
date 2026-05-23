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

            var entity = new sub_product
            {
                product_type_id = dto.product_type_id,
                width = dto.width,
                length = dto.length,
                product_process = string.IsNullOrWhiteSpace(dto.product_process)
                    ? null
                    : NormalizeProcess(dto.product_process),
                quantity = dto.quantity ?? 0,
                is_active = dto.is_active ?? true,

                // Dòng tạo tay được xem là tồn kho thật.
                is_imported = true,

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

            var items = await (
                from sp in _db.sub_products.AsNoTracking()
                join pt0 in _db.product_types.AsNoTracking()
                    on sp.product_type_id equals pt0.product_type_id into ptj
                from pt in ptj.DefaultIfEmpty()
                where subProductIds.Contains(sp.id)
                orderby sp.id
                select new SubProductImportReceiptPdfItem
                {
                    id = sp.id,
                    product_type_id = sp.product_type_id,
                    product_type_name = pt != null ? pt.name : null,
                    width = sp.width,
                    length = sp.length,
                    product_process = sp.product_process,
                    quantity = sp.quantity,
                    source_order_id = sp.source_order_id,
                    source_prod_id = sp.source_prod_id,
                    source_task_id = sp.source_task_id,
                    description = sp.description
                }
            ).ToListAsync(ct);

            if (items.Count == 0)
                throw new InvalidOperationException("Sub products not found");

            var foundIds = items.Select(x => x.id).ToHashSet();
            var missingIds = subProductIds.Where(x => !foundIds.Contains(x)).ToList();

            if (missingIds.Count > 0)
                throw new InvalidOperationException($"Sub product not found: {string.Join(", ", missingIds)}");

            var now = AppTime.NowVnUnspecified();
            var receiptNo = $"PNBTP-{now:yyyyMMddHHmmss}";
            var fileName = $"phieu-nhap-ban-thanh-pham-{now:yyyyMMddHHmmss}.pdf";
            var publicId = $"sub-products/import-receipts/{receiptNo}";

            var pdfBytes = SubProductImportReceiptPdfHelper.GeneratePdf(
                items,
                receiptNo,
                now);

            await using var ms = new MemoryStream(pdfBytes);

            var cloudUrl = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                ms,
                fileName,
                "application/pdf",
                publicId);

            await _db.sub_products
                .Where(x => subProductIds.Contains(x.id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.import_file, cloudUrl)
                    .SetProperty(x => x.updated_at, now), ct);

            return new SubProductImportReceiptBatchResponseDto
            {
                success = true,
                import_file = cloudUrl,
                file_name = fileName,
                total_selected = items.Count,
                message = $"Đã tạo phiếu nhập bán thành phẩm cho {items.Count} dòng.",
                items = items.Select(x => new SubProductImportReceiptItemDto
                {
                    sub_product_id = x.id,
                    product_type_id = x.product_type_id,
                    product_type_name = x.product_type_name,
                    width = x.width,
                    length = x.length,
                    product_process = x.product_process,
                    quantity = x.quantity,
                    import_file = cloudUrl
                }).ToList()
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
                response.message = "Không có bán thành phẩm chờ nhập kho.";
                return response;
            }

            var productTypeIds = pendings
                .Select(x => x.product_type_id)
                .Distinct()
                .ToList();

            var activeRows = await _db.sub_products
                .Where(x =>
                    x.is_active == true &&
                    x.is_imported == true &&
                    productTypeIds.Contains(x.product_type_id))
                .ToListAsync(ct);

            foreach (var pending in pendings)
            {
                var pendingProcess = NormalizeProcess(pending.product_process);
                var importQty = pending.quantity;

                var matchedActive = activeRows.FirstOrDefault(x =>
                    x.id != pending.id &&
                    x.product_type_id == pending.product_type_id &&
                    x.width == pending.width &&
                    x.length == pending.length &&
                    NormalizeProcess(x.product_process) == pendingProcess);

                if (matchedActive != null)
                {
                    matchedActive.quantity += importQty;
                    matchedActive.updated_at = now;

                    pending.quantity = 0;
                    pending.is_imported = true;
                    pending.is_active = false;
                    pending.updated_at = now;
                    pending.description = AppendDescription(
                        pending.description,
                        $"Đã nhập gộp vào sub_product_id={matchedActive.id} lúc {now:yyyy-MM-dd HH:mm:ss}.");

                    response.merged_count++;

                    response.rows.Add(new ImportPendingSubProductRowDto
                    {
                        pending_sub_product_id = pending.id,
                        merged_into_sub_product_id = matchedActive.id,
                        action = "MERGED",
                        quantity = importQty,
                        product_type_id = pending.product_type_id,
                        width = pending.width,
                        length = pending.length,
                        product_process = pending.product_process
                    });
                }
                else
                {
                    pending.is_active = true;
                    pending.is_imported = true;
                    pending.updated_at = now;
                    pending.description = AppendDescription(
                        pending.description,
                        $"Đã chuyển thành tồn kho active lúc {now:yyyy-MM-dd HH:mm:ss}.");

                    activeRows.Add(pending);

                    response.activated_count++;

                    response.rows.Add(new ImportPendingSubProductRowDto
                    {
                        pending_sub_product_id = pending.id,
                        merged_into_sub_product_id = null,
                        action = "ACTIVATED",
                        quantity = importQty,
                        product_type_id = pending.product_type_id,
                        width = pending.width,
                        length = pending.length,
                        product_process = pending.product_process
                    });
                }
            }

            await _db.SaveChangesAsync(ct);

            response.message =
                $"Đã xử lý {response.total_pending} dòng chờ nhập kho. " +
                $"Gộp vào tồn hiện có: {response.merged_count}, active mới: {response.activated_count}.";

            return response;
        }

        private static string NormalizeProcess(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static string? AppendDescription(string? oldValue, string line)
        {
            if (string.IsNullOrWhiteSpace(oldValue))
                return line.Length > 255 ? line[..255] : line;

            var value = oldValue.Trim();
            var next = $"{value} | {line}";

            return next.Length <= 255 ? next : next[..255];
        }
    }
}