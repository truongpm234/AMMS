using AMMS.Application.Helpers;
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
        private readonly IMissingMaterialRepository _missingRepo;
        public PurchaseService(IPurchaseRepository repo, IOrderRepository orderRepo, IMissingMaterialRepository missingRepo)
        {
            _repo = repo;
            _orderRepo = orderRepo; 
            _missingRepo = missingRepo;
        }

        public async Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(
            CreatePurchaseRequestDto dto,
            int? createdBy,
            CancellationToken ct = default)
        {
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            foreach (var i in dto.Items)
            {
                if (i.material_id <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                var exists = await _repo.MaterialExistsAsync(i.material_id, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.material_id} not found");
            }

            var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

            var p = new purchase
            {
                code = code,
                supplier_id = dto.supplier_id,
                created_by = createdBy,
                status = "Pending",
                created_at = AppTime.NowVnUnspecified(),
            };

            await _repo.AddPurchaseAsync(p, ct);
            await _repo.SaveChangesAsync(ct);

            var items = dto.Items.Select(x => new purchase_item
            {
                purchase_id = p.purchase_id,
                material_id = x.material_id,
                qty_ordered = x.quantity,
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

        public Task<PagedResultLite<PurchaseOrderCardDto>> GetPurchaseOrdersAsync(
            string? status,
            int page,
            int pageSize,
            CancellationToken ct = default)
            => _repo.GetPurchaseOrdersAsync(status, page, pageSize, ct);

        public Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
            int page, int pageSize, CancellationToken ct = default)
            => _repo.GetPendingPurchasesAsync(page, pageSize, ct);

        public async Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(
            CreatePurchaseRequestDto dto,
            CancellationToken ct = default)
        {
            if (dto == null)
                throw new ArgumentException("Body is required");

            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            foreach (var i in dto.Items)
            {
                if (i.material_id <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                if (i.price.HasValue && i.price.Value < 0)
                    throw new ArgumentException("Price must be >= 0");

                var exists = await _repo.MaterialExistsAsync(i.material_id, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.material_id} not found");
            }

            const string createdByName = "manager";
            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found. Please create it first.");

            var normalizedItems = dto.Items.Select(x => new
            {
                SupplierId = x.supplier_id ?? dto.supplier_id,
                MaterialId = x.material_id,
                Quantity = x.quantity,
                Price = x.price
            }).ToList();

            if (normalizedItems.Any(x => x.SupplierId == null))
                throw new ArgumentException("SupplierId is required (each item.SupplierId or dto.SupplierId)");

            var supplierIds = normalizedItems.Select(x => x.SupplierId!.Value).Distinct().ToList();
            foreach (var sid in supplierIds)
            {
                var supplierOk = await _repo.SupplierExistsAsync(sid, ct);
                if (!supplierOk) throw new ArgumentException($"SupplierId {sid} not found");
            }

            var groups = normalizedItems
                .GroupBy(x => x.SupplierId!.Value)
                .ToList();

            var createdPurchases = new List<(
                int PurchaseId,
                string Code,
                int SupplierId,
                string SupplierName,
                DateTime? CreatedAt,
                decimal TotalQty
            )>();

            foreach (var g in groups)
            {
                var supplierId = g.Key;
                var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

                var p = new purchase
                {
                    code = code,
                    supplier_id = supplierId,
                    created_by = managerId,
                    status = "Ordered",
                    created_at = AppTime.NowVnUnspecified(),
                };

                await _repo.AddPurchaseAsync(p, ct);
                await _repo.SaveChangesAsync(ct);

                var items = g.Select(x => new purchase_item
                {
                    purchase_id = p.purchase_id,
                    material_id = x.MaterialId,
                    qty_ordered = x.Quantity,
                    price = x.Price
                }).ToList();

                await _repo.AddPurchaseItemsAsync(items, ct);
                await _repo.SaveChangesAsync(ct);

                var supplierName = await _repo.GetSupplierNameAsync(supplierId, ct) ?? "N/A";
                var totalQty = g.Sum(x => x.Quantity);

                createdPurchases.Add((
                    p.purchase_id,
                    p.code ?? code,
                    supplierId,
                    supplierName,
                    p.created_at,
                    totalQty
                ));
            }

            // ❌ IMPORTANT: KHÔNG set orders.is_buy ở đây nữa
            // vì bạn muốn chỉ is_buy=true khi mua đủ (remaining missing == 0)

            // ✅ Recalc missing to make it decrease immediately (55 -> 11)
            await _missingRepo.RecalculateAndSaveAsync(ct);

            if (createdPurchases.Count == 1)
            {
                var one = createdPurchases[0];
                return new PurchaseOrderListItemDto(
                    one.PurchaseId,
                    one.Code,
                    one.SupplierName,
                    one.CreatedAt,
                    createdByName,
                    one.TotalQty,
                    "Ordered",
                    null,
                    null
                );
            }
            else
            {
                var totalAll = createdPurchases.Sum(x => x.TotalQty);
                var createdAt = createdPurchases
                    .Select(x => x.CreatedAt)
                    .Where(x => x != null)
                    .OrderBy(x => x)
                    .FirstOrDefault();

                var codes = string.Join(", ", createdPurchases.Select(x => x.Code));
                var first = createdPurchases.OrderBy(x => x.PurchaseId).First();

                return new PurchaseOrderListItemDto(
                    first.PurchaseId,
                    $"BATCH({createdPurchases.Count}): {codes}",
                    "MIXED",
                    createdAt,
                    createdByName,
                    totalAll,
                    "Ordered",
                    null,
                    codes
                );
            }
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