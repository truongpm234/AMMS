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

                join m in _db.materials.AsNoTracking()
                    on i.material_id equals m.material_id into mats
                from m in mats.DefaultIfEmpty()

                join sm in _db.stock_moves.AsNoTracking().Where(x => x.type == "IN")
                    on p.purchase_id equals sm.purchase_id into sms
                from sm in sms.DefaultIfEmpty()

                join u in _db.users.AsNoTracking()
                    on sm.user_id equals u.user_id into us
                from u in us.DefaultIfEmpty()

                group new { p, s, i, m, sm, u } by new
                {
                    p.purchase_id,
                    p.code,
                    p.created_at,
                    p.eta_date,
                    p.status,
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
                    g.Sum(x => (decimal?)(x.i != null ? (x.i.qty_ordered ?? 0) : 0)) ?? 0m,
                    g.Key.eta_date,
                    g.Key.status ?? "Pending",
                    g.Max(x => x.u != null ? x.u.full_name : null),

                    // ✅ unit (1 unit => trả unit đó, nhiều => "MIXED", không có item => null)
                    g.Select(x => x.m != null ? x.m.unit : null).Where(v => v != null).Distinct().Count() == 0
                        ? null
                        : g.Select(x => x.m != null ? x.m.unit : null).Where(v => v != null).Distinct().Count() == 1
                            ? g.Select(x => x.m != null ? x.m.unit : null).Where(v => v != null).Max()
                            : "MIXED"
                );

            return q;
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        // ✅ NEW
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

        public async Task<PagedResultLite<PurchaseOrderWithItemsDto>> GetPurchaseOrdersAsync(
    int page, int pageSize, CancellationToken ct = default)
        {
            return await GetOrdersWithItemsCoreAsync(statusFilter: null, page, pageSize, ct);
        }

        public async Task<PagedResultLite<PurchaseOrderWithItemsDto>> GetPendingPurchasesAsync(
    int page, int pageSize, CancellationToken ct = default)
        {
            return await GetOrdersWithItemsCoreAsync(statusFilter: "Pending", page, pageSize, ct);
        }

        private async Task<PagedResultLite<PurchaseOrderWithItemsDto>> GetOrdersWithItemsCoreAsync(
    string? statusFilter,
    int page,
    int pageSize,
    CancellationToken ct)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            // 1) Page purchases (join supplier 1 lần, ít nhất có thể)
            var purchaseRows = await _db.purchases
                .AsNoTracking()
                .Where(p => statusFilter == null || p.status == statusFilter)
                .OrderByDescending(p => p.purchase_id)
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(p => new
                {
                    p.purchase_id,
                    p.code,
                    p.created_at,
                    p.eta_date,
                    Status = p.status ?? "Pending",
                    SupplierName = p.supplier != null ? p.supplier.name : "N/A"
                })
                .ToListAsync(ct);

            var hasNext = purchaseRows.Count > pageSize;
            if (hasNext) purchaseRows.RemoveAt(purchaseRows.Count - 1);

            var purchaseIds = purchaseRows.Select(x => x.purchase_id).ToList();

            // 2) Items của các purchase trong page (KHÔNG join materials)
            var itemRows = await _db.purchase_items
                .AsNoTracking()
                .Where(i => i.purchase_id != null && purchaseIds.Contains(i.purchase_id.Value))
                .OrderBy(i => i.id)
                .Select(i => new
                {
                    PurchaseId = i.purchase_id!.Value,
                    i.id,
                    MaterialId = i.material_id ?? 0,
                    i.material_code,
                    i.material_name,
                    i.unit,
                    Qty = (decimal)(i.qty_ordered ?? 0m),
                    i.price
                })
                .ToListAsync(ct);

            var itemsByPurchase = itemRows
                .GroupBy(x => x.PurchaseId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new PurchaseItemLineDto(
                            x.id,
                            x.MaterialId,
                            x.material_code,
                            x.material_name,
                            x.unit,
                            x.Qty,
                            x.price
                        ))
                        .ToList()
                );

            // 3) received_by_name (join stock_moves + users, chỉ cho page hiện tại)
            var receivedBy = await (
                from sm in _db.stock_moves.AsNoTracking()
                where sm.type == "IN"
                      && sm.purchase_id != null
                      && purchaseIds.Contains(sm.purchase_id.Value)
                join u in _db.users.AsNoTracking()
                    on sm.user_id equals u.user_id into us
                from u in us.DefaultIfEmpty()
                group u by sm.purchase_id!.Value into g
                select new
                {
                    PurchaseId = g.Key,
                    Name = g.Max(x => x != null ? x.full_name : null)
                }
            ).ToDictionaryAsync(x => x.PurchaseId, x => x.Name, ct);

            // 4) Build response
            var data = purchaseRows.Select(p =>
            {
                itemsByPurchase.TryGetValue(p.purchase_id, out var lines);
                lines ??= new List<PurchaseItemLineDto>();

                var totalQty = lines.Sum(x => x.qty_ordered);

                receivedBy.TryGetValue(p.purchase_id, out var rName);

                return new PurchaseOrderWithItemsDto(
                    p.purchase_id,
                    p.code,
                    p.SupplierName,
                    p.created_at,
                    "manager",
                    totalQty,
                    p.eta_date,
                    p.Status,
                    rName,
                    lines
                );
            }).ToList();

            return new PagedResultLite<PurchaseOrderWithItemsDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
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

        public async Task<object> ReceiveAllPendingPurchasesAsync(int managerUserId, CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync<object>(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var pending = await _db.purchases
                    .AsTracking()
                    .Where(p => p.status == "Pending")
                    .ToListAsync(ct);

                if (pending.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { processed = 0, message = "No pending purchase orders." };
                }

                var purchaseIds = pending.Select(p => p.purchase_id).ToList();

                var items = await _db.purchase_items
                    .AsNoTracking()
                    .Where(i => i.purchase_id != null && purchaseIds.Contains(i.purchase_id.Value))
                    .ToListAsync(ct);

                if (items.Count == 0)
                {
                    foreach (var po in pending) po.status = "Received";
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return new { processed = pending.Count, stockMovesCreated = 0, materialsUpdated = 0, statusSetTo = "Received" };
                }

                var now = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);

                var stockMoves = new List<stock_move>(items.Count);
                var materialAddMap = new Dictionary<int, decimal>();
                var codeMap = pending.ToDictionary(x => x.purchase_id, x => x.code);

                foreach (var it in items)
                {
                    if (it.material_id == null) continue;
                    var qty = (decimal)(it.qty_ordered ?? 0);
                    if (qty <= 0) continue;

                    var materialId = it.material_id.Value;
                    var refDoc = it.purchase_id.HasValue && codeMap.TryGetValue(it.purchase_id.Value, out var c) ? c : null;

                    stockMoves.Add(new stock_move
                    {
                        material_id = materialId,
                        type = "IN",
                        qty = qty,
                        ref_doc = refDoc,
                        user_id = managerUserId,
                        move_date = now,
                        note = "Auto receive from PO",
                        purchase_id = it.purchase_id
                    });

                    if (!materialAddMap.ContainsKey(materialId)) materialAddMap[materialId] = 0;
                    materialAddMap[materialId] += qty;
                }

                if (stockMoves.Count > 0)
                    await _db.stock_moves.AddRangeAsync(stockMoves, ct);

                var materialIds = materialAddMap.Keys.ToList();
                var materials = await _db.materials
                    .AsTracking()
                    .Where(m => materialIds.Contains(m.material_id))
                    .ToListAsync(ct);

                foreach (var m in materials)
                {
                    m.stock_qty = (m.stock_qty ?? 0) + materialAddMap[m.material_id];
                }

                foreach (var po in pending)
                    po.status = "Received";

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new
                {
                    processed = pending.Count,
                    stockMovesCreated = stockMoves.Count,
                    materialsUpdated = materials.Count,
                    statusSetTo = "Received"
                };
            });
        }
    }
}
