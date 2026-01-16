using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class PurchaseRepository : IPurchaseRepository
    {
        private readonly AppDbContext _db;

        public PurchaseRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task AddPurchaseAsync(purchase entity, CancellationToken ct = default)
            => await _db.purchases.AddAsync(entity, ct);

        public async Task AddPurchaseItemsAsync(IEnumerable<purchase_item> items, CancellationToken ct = default)
            => await _db.purchase_items.AddRangeAsync(items, ct);

        public Task<bool> MaterialExistsAsync(int materialId, CancellationToken ct = default)
            => _db.materials.AsNoTracking().AnyAsync(m => m.material_id == materialId, ct);

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);

        public async Task<string> GenerateNextPurchaseCodeAsync(CancellationToken ct = default)
        {
            var lastCode = await _db.purchases
                .AsNoTracking()
                .Where(p => p.code != null && p.code.StartsWith("PO"))
                .OrderByDescending(p => p.purchase_id)
                .Select(p => p.code!)
                .FirstOrDefaultAsync(ct);

            int next = 1;
            if (!string.IsNullOrWhiteSpace(lastCode))
            {
                var digits = new string(lastCode.SkipWhile(c => !char.IsDigit(c)).ToArray());
                if (int.TryParse(digits, out var n)) next = n + 1;
            }

            return $"PO{next:D4}";
        }

        public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken ct = default)
            => _db.suppliers.AsNoTracking().AnyAsync(s => s.supplier_id == supplierId, ct);

        public async Task<string?> GetSupplierNameAsync(int? supplierId, CancellationToken ct = default)
        {
            if (!supplierId.HasValue) return null;

            return await _db.suppliers.AsNoTracking()
                .Where(s => s.supplier_id == supplierId.Value)
                .Select(s => s.name)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<int?> GetManagerUserIdAsync(CancellationToken ct = default)
        {
            return await _db.users.AsNoTracking()
                .Where(u => u.username == "manager")
                .Select(u => (int?)u.user_id)
                .FirstOrDefaultAsync(ct);
        }

        // =========================
        // ✅ NEW: Paging helpers
        // =========================
        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        private async Task<PagedResultLite<PurchaseOrderListItemDto>> ToPagedAsync(
            IQueryable<PurchaseOrderListItemDto> query,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            NormalizePaging(ref page, ref pageSize);

            var skip = (page - 1) * pageSize;

            var rows = await query
                .Skip(skip)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = rows.Count > pageSize;
            if (hasNext) rows.RemoveAt(rows.Count - 1);

            return new PagedResultLite<PurchaseOrderListItemDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = rows
            };
        }

        // =========================
        // ✅ NEW/CHANGED: Build list query (all/pending)
        // - add eta_date, status
        // - join stock_moves(IN) + users => receivedByName
        // - join materials (optional) => unit (MIXED/1 unit/null)
        // =========================
        private IQueryable<PurchaseOrderListItemDto> BuildPurchaseListQuery(string? statusFilter)
        {
            var q =
                from p in _db.purchases.AsNoTracking()
                where statusFilter == null || p.status == statusFilter

                join s in _db.suppliers.AsNoTracking()
                    on p.supplier_id equals s.supplier_id into sup
                from s in sup.DefaultIfEmpty()

                join i in _db.purchase_items.AsNoTracking()
                    on p.purchase_id equals i.purchase_id into items
                from i in items.DefaultIfEmpty()

                    // ✅ CHANGED: join materials để lấy unit (nếu bạn cần unit tổng)
                join m in _db.materials.AsNoTracking()
                    on i.material_id equals m.material_id into mats
                from m in mats.DefaultIfEmpty()

                    // ✅ CHANGED: join stock_moves IN
                join sm in _db.stock_moves.AsNoTracking().Where(x => x.type == "IN")
                    on p.purchase_id equals sm.purchase_id into sms
                from sm in sms.DefaultIfEmpty()

                    // ✅ CHANGED: join users để lấy tên người nhận
                join u in _db.users.AsNoTracking()
                    on sm.user_id equals u.user_id into us
                from u in us.DefaultIfEmpty()

                group new { p, s, i, m, sm, u } by new
                {
                    p.purchase_id,
                    p.code,
                    p.created_at,
                    p.status,    // ✅
                    SupplierName = (string?)(s != null ? s.name : null)
                }
                into g
                orderby g.Key.purchase_id descending
                select new PurchaseOrderListItemDto(
                    g.Key.purchase_id,
                    g.Key.code,
                    g.Key.SupplierName ?? "N/A",
                    g.Key.created_at,
                    "manager",

                    // total qty ordered
                    g.Sum(x => (decimal?)(x.i != null ? (x.i.qty_ordered ?? 0) : 0)) ?? 0m,

                    // ✅ CHANGED: eta_date + status + receivedByName + unit
                    g.Key.status ?? "Pending",
                    g.Max(x => x.u != null ? x.u.full_name : null),

                    // unit: 0 unit => null, 1 unit => that unit, many => MIXED
                    g.Select(x => x.m != null ? x.m.unit : null)
                        .Where(v => v != null)
                        .Distinct()
                        .Count() == 0
                        ? null
                        : g.Select(x => x.m != null ? x.m.unit : null)
                            .Where(v => v != null)
                            .Distinct()
                            .Count() == 1
                            ? g.Select(x => x.m != null ? x.m.unit : null)
                                .Where(v => v != null)
                                .Max()
                            : "MIXED"
                );

            return q;
        }

        // =========================
        // ✅ CHANGED: Get all purchases (paged)
        // =========================
        public async Task<PagedResultLite<PurchaseOrderCardDto>> GetPurchaseOrdersAsync(
    string? status,
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            // ✅ 0) Normalize status input (optional)
            // FE có thể gửi "pending" / "Pending" => normalize lại
            string? statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();

            // ✅ 1) Base purchases (FILTER + PAGING trên purchases.status)
            var baseQuery = _db.purchases.AsNoTracking();

            if (statusFilter != null)
            {
                // ✅ status lấy từ DB => filter trực tiếp DB
                if (!string.IsNullOrWhiteSpace(status))
                {
                    var normalizedStatus = status.Trim().ToLower();

                    baseQuery = baseQuery.Where(p =>
                        (p.status ?? "Ordered").ToLower() == normalizedStatus
                    );
                }

            }

            var purchases = await baseQuery
                .OrderByDescending(p => p.purchase_id)
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(p => new
                {
                    p.purchase_id,
                    p.code,
                    p.supplier_id,
                    SupplierName = p.supplier != null ? p.supplier.name : "N/A",
                    p.created_at,

                    // ✅ LẤY STATUS 100% từ purchases.status
                    Status = p.status ?? "Ordered",

                    // ✅ created_by_name từ users
                    CreatedByName = p.created_byNavigation != null
                        ? (p.created_byNavigation.full_name ?? "N/A")
                        : "N/A"
                })
                .ToListAsync(ct);

            var hasNext = purchases.Count > pageSize;
            if (hasNext) purchases.RemoveAt(purchases.Count - 1);

            var purchaseIds = purchases.Select(x => x.purchase_id).ToList();

            // ✅ 2) Items theo purchaseId
            var items = await _db.purchase_items
                .AsNoTracking()
                .Where(i => i.purchase_id != null && purchaseIds.Contains(i.purchase_id.Value))
                .Select(i => new
                {
                    i.id,
                    PurchaseId = i.purchase_id!.Value,
                    i.material_id,
                    i.material_code,
                    i.material_name,
                    i.qty_ordered,
                    i.unit
                })
                .ToListAsync(ct);

            var itemsMap = items
                .GroupBy(x => x.PurchaseId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new PurchaseOrderItemDto
                    {
                        Id = x.id,
                        MaterialId = x.material_id ?? 0,
                        MaterialCode = x.material_code ?? "",
                        MaterialName = x.material_name ?? "",
                        QtyOrdered = x.qty_ordered ?? 0m,
                        Unit = x.unit ?? ""
                    }).ToList()
                );

            // ✅ 3) Received info từ stock_moves (IN) + users
            // NOTE: không dùng để tính status nữa, chỉ để show UI
            var receivedInfoMap = await (
                from sm in _db.stock_moves.AsNoTracking()
                where sm.type == "IN"
                      && sm.purchase_id != null
                      && purchaseIds.Contains(sm.purchase_id.Value)
                join u in _db.users.AsNoTracking()
                    on sm.user_id equals u.user_id into us
                from u in us.DefaultIfEmpty()
                group new { sm, u } by sm.purchase_id!.Value into g
                select new
                {
                    PurchaseId = g.Key,
                    ReceivedAt = g.Max(x => x.sm.move_date),
                    ReceivedByName = g.Max(x => x.u != null ? x.u.full_name : null)
                }
            ).ToDictionaryAsync(x => x.PurchaseId, ct);

            // ✅ 4) Build response
            var data = purchases.Select(p =>
            {
                itemsMap.TryGetValue(p.purchase_id, out var poItems);
                poItems ??= new List<PurchaseOrderItemDto>();

                receivedInfoMap.TryGetValue(p.purchase_id, out var r);

                var totalQty = poItems.Sum(x => x.QtyOrdered);

                // ✅ Nếu DB có status khác 3 status chuẩn => bạn có thể ép về 3 status
                // Ở đây mình giữ nguyên DB để khớp 100%.
                // Nếu muốn ép: xem đoạn "Gợi ý chuẩn hoá status" bên dưới.
                return new PurchaseOrderCardDto
                {
                    PurchaseId = p.purchase_id,
                    Code = p.code,
                    SupplierId = p.supplier_id,
                    SupplierName = p.SupplierName,
                    CreatedAt = p.created_at ?? DateTime.MinValue,
                    CreatedByName = p.CreatedByName,
                    Status = p.Status,
                    ReceivedAt = r?.ReceivedAt,
                    ReceivedByName = r?.ReceivedByName,
                    TotalQty = totalQty,
                    Items = poItems
                };
            }).ToList();

            return new PagedResultLite<PurchaseOrderCardDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        // =========================
        // ✅ CHANGED: Get pending purchases (paged)
        // =========================
        public Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            var query = BuildPurchaseListQuery(statusFilter: "Pending");
            return ToPagedAsync(query, page, pageSize, ct);
        }

        // ===========================================================
        // ✅ CHANGED: Receive purchase by purchaseId (NOT receive all)
        // - fix case "Nothing to receive" nhưng thực ra đã đủ => set Received
        // ===========================================================
        public async Task<object> ReceiveAllPendingPurchasesAsync(
            int purchaseId,
            int managerUserId,
            CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync<object>(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // 1) Lấy purchase
                var purchase = await _db.purchases
                    .AsTracking()
                    .FirstOrDefaultAsync(p => p.purchase_id == purchaseId, ct);

                if (purchase == null)
                    throw new ArgumentException($"Purchase {purchaseId} not found");

                // 2) Items của PO
                var items = await _db.purchase_items
                    .AsNoTracking()
                    .Where(i => i.purchase_id == purchaseId)
                    .ToListAsync(ct);

                if (items.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { message = "Purchase has no items", purchaseId };
                }

                // 3) Tổng đã nhận theo material (IN)
                var receivedMap = await _db.stock_moves
                    .AsNoTracking()
                    .Where(m => m.purchase_id == purchaseId
                                && m.type == "IN"
                                && m.material_id != null)
                    .GroupBy(m => m.material_id!.Value)
                    .Select(g => new
                    {
                        MaterialId = g.Key,
                        Qty = g.Sum(x => x.qty ?? 0m)
                    })
                    .ToDictionaryAsync(x => x.MaterialId, x => x.Qty, ct);

                static bool IsFullyReceived(
                    List<purchase_item> its,
                    Dictionary<int, decimal> receivedBefore,
                    Dictionary<int, decimal> addNow)
                {
                    foreach (var it in its)
                    {
                        if (it.material_id == null) continue;

                        var materialId = it.material_id.Value;
                        var ordered = it.qty_ordered ?? 0m;

                        receivedBefore.TryGetValue(materialId, out var receivedQty);
                        addNow.TryGetValue(materialId, out var addQty);

                        if (receivedQty + addQty < ordered) return false;
                    }
                    return true;
                }

                var now = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);

                var stockMoves = new List<stock_move>();
                var materialAddMap = new Dictionary<int, decimal>();

                // 4) Nhận phần còn thiếu
                foreach (var it in items)
                {
                    if (it.material_id == null) continue;

                    var materialId = it.material_id.Value;
                    var ordered = it.qty_ordered ?? 0m;

                    receivedMap.TryGetValue(materialId, out var received);
                    var remain = ordered - received;

                    if (remain <= 0) continue;

                    stockMoves.Add(new stock_move
                    {
                        material_id = materialId,
                        type = "IN",
                        qty = remain,
                        ref_doc = purchase.code,
                        user_id = managerUserId,
                        move_date = now,
                        note = "Receive PO by id",
                        purchase_id = purchaseId
                    });

                    if (!materialAddMap.ContainsKey(materialId))
                        materialAddMap[materialId] = 0m;

                    materialAddMap[materialId] += remain;
                }

                // ✅ CHANGED: không còn gì để receive -> vẫn set Received nếu đã đủ từ trước
                if (stockMoves.Count == 0)
                {
                    var fullyAlready = IsFullyReceived(items, receivedMap, new Dictionary<int, decimal>());

                    if (fullyAlready && !string.Equals(purchase.status, "Delivered", StringComparison.OrdinalIgnoreCase))
                    {
                        purchase.status = "Delivered";
                        await _db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);

                        return new
                        {
                            message = "Already fully Delivered (no remaining qty). Status updated to Delivered.",
                            purchaseId,
                            status = purchase.status
                        };
                    }

                    await tx.CommitAsync(ct);
                    return new { message = "Nothing to Delivered", purchaseId, status = purchase.status };
                }

                // 5) add stock_moves
                await _db.stock_moves.AddRangeAsync(stockMoves, ct);

                // 6) update materials.stock_qty
                var materialIds = materialAddMap.Keys.ToList();
                var materials = await _db.materials
                    .AsTracking()
                    .Where(m => materialIds.Contains(m.material_id))
                    .ToListAsync(ct);

                foreach (var m in materials)
                {
                    m.stock_qty = (m.stock_qty ?? 0m) + materialAddMap[m.material_id];
                }

                // 7) set purchase Received nếu đủ
                var fullyReceived = IsFullyReceived(items, receivedMap, materialAddMap);
                if (fullyReceived)
                    purchase.status = "Delivered";

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new
                {
                    purchaseId,
                    stockMovesCreated = stockMoves.Count,
                    materialsUpdated = materials.Count,
                    status = purchase.status
                };
            });
        }
    }
}