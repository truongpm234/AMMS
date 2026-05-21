using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.SubProduct;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public async Task<PagedResultLite<SubProductDto>> GetPagedAsync(
            int page,
            int pageSize,
            bool? isActive = null,
            bool? isImported = null,
            CancellationToken ct = default)
        {
            return await _repo.GetPagedAsync(page, pageSize, isActive, isImported, ct);
        }

        public async Task<UpdateSubProductResponse> UpdateAsync(int id, UpdateSubProductDto dto, CancellationToken ct = default)
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
                    throw new ArgumentException($"product_type_id {dto.product_type_id.Value} not found");

                entity.product_type_id = dto.product_type_id.Value;
            }

            if (dto.width.HasValue)
            {
                if (dto.width.Value < 0)
                    throw new ArgumentException("width must be >= 0");

                entity.width = dto.width.Value;
            }

            if (dto.length.HasValue)
            {
                if (dto.length.Value < 0)
                    throw new ArgumentException("length must be >= 0");

                entity.length = dto.length.Value;
            }

            if (dto.quantity.HasValue)
            {
                if (dto.quantity.Value < 0)
                    throw new ArgumentException("quantity must be >= 0");

                entity.quantity = dto.quantity.Value;
            }

            if (dto.product_process != null)
                entity.product_process = string.IsNullOrWhiteSpace(dto.product_process)
                    ? null
                    : dto.product_process.Trim();

            if (dto.description != null)
                entity.description = string.IsNullOrWhiteSpace(dto.description)
                    ? null
                    : dto.description.Trim();

            if (dto.is_active.HasValue)
                entity.is_active = dto.is_active.Value;

            entity.updated_at = AppTime.NowVnUnspecified();

            await _repo.SaveChangesAsync(ct);

            return new UpdateSubProductResponse
            {
                success = true,
                message = "Sub product updated successfully",
                id = entity.id,
                updated_at = entity.updated_at
            };
        }

        public async Task<CreateSubProductResponse> CreateAsync(CreateSubProductDto dto, CancellationToken ct = default)
        {
            if (dto.product_type_id <= 0)
                throw new ArgumentException("product_type_id is required");

            var productTypeExists = await _repo.ProductTypeExistsAsync(dto.product_type_id, ct);
            if (!productTypeExists)
                throw new ArgumentException($"product_type_id {dto.product_type_id} not found");

            if (dto.width.HasValue && dto.width.Value < 0)
                throw new ArgumentException("width must be >= 0");

            if (dto.length.HasValue && dto.length.Value < 0)
                throw new ArgumentException("length must be >= 0");

            if (dto.quantity.HasValue && dto.quantity.Value < 0)
                throw new ArgumentException("quantity must be >= 0");

            var entity = new sub_product
            {
                product_type_id = dto.product_type_id,
                width = dto.width,
                length = dto.length,
                product_process = string.IsNullOrWhiteSpace(dto.product_process) ? null : dto.product_process.Trim(),
                quantity = dto.quantity ?? 0,
                is_active = dto.is_active ?? true,
                is_imported = true,
                description = string.IsNullOrWhiteSpace(dto.description) ? null : dto.description.Trim(),
                updated_at = AppTime.NowVnUnspecified()
            };

            await _repo.AddAsync(entity, ct);
            await _repo.SaveChangesAsync(ct);

            return new CreateSubProductResponse
            {
                success = true,
                message = "Sub product created successfully",
                id = entity.id
            };
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            var entity = await _repo.GetByIdTrackingAsync(id, ct);
            if (entity == null)
                return false;

            entity.is_active = false;
            entity.updated_at = AppTime.NowVnUnspecified();

            await _repo.SaveChangesAsync(ct);
            return true;
        }

        public async Task<SubProductImportReceiptResponseDto> GenerateImportReceiptAsync(
    int subProductId,
    CancellationToken ct = default)
        {
            if (subProductId <= 0)
                throw new ArgumentException("subProductId must be > 0");

            var sp = await _db.sub_products
                .Include(x => x.product_type)
                .FirstOrDefaultAsync(x => x.id == subProductId, ct);

            if (sp == null)
                throw new InvalidOperationException("Sub product not found.");

            var pdfBytes = SubProductImportReceiptPdfHelper.GeneratePdf(sp);

            var fileName = $"phieu_nhap_ban_thanh_pham_{sp.id:D6}.pdf";
            var publicId = $"sub-products/import-receipts/sub_product_{sp.id:D6}";

            await using var ms = new MemoryStream(pdfBytes);

            var cloudUrl = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                ms,
                fileName,
                "application/pdf",
                publicId);

            sp.import_file = cloudUrl;
            sp.updated_at = AppTime.NowVnUnspecified();

            await _db.SaveChangesAsync(ct);

            return new SubProductImportReceiptResponseDto
            {
                success = true,
                sub_product_id = sp.id,
                import_file = cloudUrl,
                message = "Tạo phiếu nhập bán thành phẩm thành công."
            };
        }

        public async Task<ImportPendingSubProductsResponseDto> ImportPendingSubProductsAsync(
            CancellationToken ct = default)
        {
            var now = AppTime.NowVnUnspecified();

            var pendings = await _db.sub_products
                .Where(x =>
                    x.is_active == false &&
                    x.is_imported == false &&
                    x.quantity > 0)
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
                var pendingProcess = NormProcess(pending.product_process);

                var matchedActive = activeRows.FirstOrDefault(x =>
                    x.id != pending.id &&
                    x.product_type_id == pending.product_type_id &&
                    x.width == pending.width &&
                    x.length == pending.length &&
                    NormProcess(x.product_process) == pendingProcess);

                if (matchedActive != null)
                {
                    var importQty = pending.quantity;

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
                        quantity = pending.quantity,
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
                $"Gộp vào tồn hiện có: {response.merged_count}, chuyển active mới: {response.activated_count}.";

            return response;
        }

        private static string NormProcess(string? value)
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
                return line;

            var value = oldValue.Trim();

            if (value.Length + line.Length + 3 > 255)
                value = value[..Math.Min(value.Length, 180)];

            return $"{value} | {line}";
        }
    }
}
