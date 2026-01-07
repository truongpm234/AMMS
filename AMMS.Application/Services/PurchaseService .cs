using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;

namespace AMMS.Application.Services
{
    public class PurchaseService : IPurchaseService
    {
        private readonly IPurchaseRepository _repo;
        private readonly IOrderRepository _orderRepo;

        public PurchaseService(IPurchaseRepository repo, IOrderRepository orderRepo)
        {
            _repo = repo;
            _orderRepo = orderRepo;
        }

        private DateTime? ToUnspecified(DateTime? dt)
            => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified) : null;

        public async Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(
            CreatePurchaseRequestDto dto,
            int? createdBy,
            CancellationToken ct = default)
        {
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            foreach (var i in dto.Items)
            {
                if (i.MaterialId <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.Quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                var exists = await _repo.MaterialExistsAsync(i.MaterialId, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.MaterialId} not found");
            }

            var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

            var p = new purchase
            {
                code = code,
                supplier_id = dto.SupplierId,
                created_by = createdBy,
                status = "Pending",
                eta_date = ToUnspecified(dto.EtaDate),
                created_at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
            };

            await _repo.AddPurchaseAsync(p, ct);
            await _repo.SaveChangesAsync(ct);

            var items = dto.Items.Select(x => new purchase_item
            {
                purchase_id = p.purchase_id,
                material_id = x.MaterialId,
                qty_ordered = x.Quantity,
            }).ToList();

            foreach (var it in items)
            {
                if (it.material_id.HasValue)
                    await _orderRepo.MarkOrdersBuyByMaterialAsync(it.material_id.Value, ct);
            }

            await _repo.AddPurchaseItemsAsync(items, ct);
            await _repo.SaveChangesAsync(ct);
            await _orderRepo.SaveChangesAsync();

            return new CreatePurchaseRequestResponse(
                p.purchase_id,
                p.code,
                p.status ?? "Pending",
                p.created_at
            );
        }

        // ✅ CHANGED: implement đúng interface
        public Task<PagedResultLite<PurchaseOrderCardDto>> GetPurchaseOrdersAsync(
            string? status,
            int page,
            int pageSize,
            CancellationToken ct = default)
            => _repo.GetPurchaseOrdersAsync(status, page, pageSize, ct);

        // ✅ CHANGED: pending trả ListItemDto (đúng repo)
        public Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
            int page, int pageSize, CancellationToken ct = default)
            => _repo.GetPendingPurchasesAsync(page, pageSize, ct);

        public async Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(
            CreatePurchaseRequestDto dto,
            CancellationToken ct = default)
        {
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            if (dto.SupplierId.HasValue)
            {
                var supplierOk = await _repo.SupplierExistsAsync(dto.SupplierId.Value, ct);
                if (!supplierOk) throw new ArgumentException($"SupplierId {dto.SupplierId.Value} not found");
            }

            foreach (var i in dto.Items)
            {
                if (i.MaterialId <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.Quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                var exists = await _repo.MaterialExistsAsync(i.MaterialId, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.MaterialId} not found");
            }

            var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

            const string createdByName = "manager";
            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found. Please create it first.");

            var p = new purchase
            {
                code = code,
                supplier_id = dto.SupplierId,
                created_by = managerId,
                status = "Ordered",
                eta_date = ToUnspecified(dto.EtaDate),
                created_at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
            };

            await _repo.AddPurchaseAsync(p, ct);
            await _repo.SaveChangesAsync(ct);

            var items = dto.Items.Select(x => new purchase_item
            {
                purchase_id = p.purchase_id,
                material_id = x.MaterialId,
                qty_ordered = x.Quantity,
            }).ToList();

            await _repo.AddPurchaseItemsAsync(items, ct);
            await _repo.SaveChangesAsync(ct);

            await _orderRepo.MarkOrdersBuyByMaterialsAsync(
                dto.Items.Select(i => i.MaterialId).ToList(),
                ct
            );
            await _orderRepo.SaveChangesAsync();

            var totalQty = dto.Items.Sum(x => (decimal)x.Quantity);
            var supplierName = await _repo.GetSupplierNameAsync(dto.SupplierId, ct) ?? "N/A";

            // ⚠️ nếu constructor DTO của bạn khác tham số, gửi mình file DTO mình khớp lại
            return new PurchaseOrderListItemDto(
                p.purchase_id,
                p.code,
                supplierName,
                p.created_at,
                createdByName,
                totalQty,
                p.eta_date,
                p.status ?? "Ordered",
                received_by_name: null,
                unit_summary: null
            );
        }

        public async Task<object> ReceiveAllPendingPurchasesAsync(int purchaseId, ReceivePurchaseRequestDto body, CancellationToken ct = default)
        {
            var status = (body?.status ?? "").Trim();

            if (!string.Equals(status, "Delivered", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Request body status must be 'Delivered'");

            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found");

            var result = await _repo.ReceiveAllPendingPurchasesAsync(purchaseId, managerId.Value, ct);

            await _orderRepo.RecalculateIsEnoughForOrdersAsync(ct);
            await _orderRepo.SaveChangesAsync();

            return result;

        }
    }
}