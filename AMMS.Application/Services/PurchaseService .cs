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
            if (dto == null)
                throw new ArgumentException("Body is required");

            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("Items is required");

            // ✅ Validate items + material exists + price
            foreach (var i in dto.Items)
            {
                if (i.MaterialId <= 0) throw new ArgumentException("MaterialId invalid");
                if (i.Quantity <= 0) throw new ArgumentException("Quantity must be > 0");

                // price optional; nếu bạn muốn bắt buộc: if (i.Price is null || i.Price <= 0) throw...
                if (i.Price.HasValue && i.Price.Value < 0)
                    throw new ArgumentException("Price must be >= 0");

                var exists = await _repo.MaterialExistsAsync(i.MaterialId, ct);
                if (!exists) throw new ArgumentException($"MaterialId {i.MaterialId} not found");
            }

            // ✅ Manager (created_by)
            const string createdByName = "manager";
            var managerId = await _repo.GetManagerUserIdAsync(ct);
            if (managerId == null)
                throw new ArgumentException("User 'manager' not found. Please create it first.");

            // ✅ Resolve supplier per item:
            // - item.SupplierId ưu tiên
            // - fallback dto.SupplierId (để tương thích kiểu cũ)
            var normalizedItems = dto.Items.Select(x => new
            {
                SupplierId = x.SupplierId ?? dto.SupplierId,
                MaterialId = x.MaterialId,
                Quantity = x.Quantity,
                Price = x.Price
            }).ToList();

            if (normalizedItems.Any(x => x.SupplierId == null))
                throw new ArgumentException("SupplierId is required (each item.SupplierId or dto.SupplierId)");

            // ✅ Validate supplier exists (distinct)
            var supplierIds = normalizedItems.Select(x => x.SupplierId!.Value).Distinct().ToList();
            foreach (var sid in supplierIds)
            {
                var supplierOk = await _repo.SupplierExistsAsync(sid, ct);
                if (!supplierOk) throw new ArgumentException($"SupplierId {sid} not found");
            }

            // ✅ Group by supplier => create multiple purchases
            var groups = normalizedItems
                .GroupBy(x => x.SupplierId!.Value)
                .ToList();

            var createdPurchases = new List<(
                int PurchaseId,
                string Code,
                int SupplierId,
                string SupplierName,
                DateTime? CreatedAt,
                DateTime? EtaDate,
                decimal TotalQty
            )>();

            foreach (var g in groups)
            {
                var supplierId = g.Key;

                // code: POxxxx
                var code = await _repo.GenerateNextPurchaseCodeAsync(ct);

                var p = new purchase
                {
                    code = code,
                    supplier_id = supplierId,
                    created_by = managerId,
                    status = "Ordered",
                    eta_date = ToUnspecified(dto.EtaDate),
                    created_at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
                };

                await _repo.AddPurchaseAsync(p, ct);
                await _repo.SaveChangesAsync(ct);

                // ✅ Create purchase_items (with price)
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
                    p.code,
                    supplierId,
                    supplierName,
                    p.created_at,
                    p.eta_date,
                    totalQty
                ));
            }

            // ✅ Mark is_buy for all materials (one shot)
            await _orderRepo.MarkOrdersBuyByMaterialsAsync(
                normalizedItems.Select(x => x.MaterialId).Distinct().ToList(),
                ct
            );
            await _orderRepo.SaveChangesAsync();

            // ✅ Return 1 DTO (vì interface yêu cầu 1 object)
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
                    one.EtaDate,
                    "Ordered",
                    received_by_name: null,
                    unit_summary: null
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
                    $"BATCH({createdPurchases.Count}): {codes}", // chỉ trả về để bạn nhìn, không lưu DB
                    "MIXED",
                    createdAt,
                    createdByName,
                    totalAll,
                    ToUnspecified(dto.EtaDate),
                    "Ordered",
                    received_by_name: null,
                    unit_summary: codes
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