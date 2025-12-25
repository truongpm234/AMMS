using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            // ví dụ code: PUR000001, PUR000002...
            // lấy code lớn nhất rồi +1
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

        public async Task<PagedResultLite<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            var baseQuery =
                from p in _db.purchases.AsNoTracking()
                join s in _db.suppliers.AsNoTracking()
                    on p.supplier_id equals s.supplier_id into sup
                from s in sup.DefaultIfEmpty()
                join i in _db.purchase_items.AsNoTracking()
                    on p.purchase_id equals i.purchase_id into items
                from i in items.DefaultIfEmpty()
                group new { p, s, i } by new
                {
                    p.purchase_id,
                    p.code,
                    p.created_at,
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
                    g.Sum(x => (decimal?)(x.i != null ? (x.i.qty_ordered ?? 0) : 0)) ?? 0m
                );

            var skip = (page - 1) * pageSize;

            var rows = await baseQuery
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
            // Cách đơn giản: username = "manager"
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

                // 1) lấy PO pending
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

                // 2) lấy items theo các PO
                var items = await _db.purchase_items
                    .AsNoTracking()
                    .Where(i => i.purchase_id != null && purchaseIds.Contains(i.purchase_id.Value))
                    .ToListAsync(ct);

                if (items.Count == 0)
                {
                    // vẫn update trạng thái nếu bạn muốn, hoặc trả về báo không có item
                    foreach (var po in pending) po.status = "Received";
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return new { processed = pending.Count, stockMovesCreated = 0, materialsUpdated = 0, statusSetTo = "Received" };
                }

                var now = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);

                // 3) tạo stock_moves + gom qty theo material
                var stockMoves = new List<stock_move>(items.Count);
                var materialAddMap = new Dictionary<int, decimal>();

                foreach (var it in items)
                {
                    if (it.material_id == null) continue;
                    var qty = (decimal)(it.qty_ordered ?? 0);
                    if (qty <= 0) continue;

                    var materialId = it.material_id.Value;

                    stockMoves.Add(new stock_move
                    {
                        material_id = materialId,
                        type = "IN",
                        qty = qty,
                        ref_doc = pending.First(p => p.purchase_id == it.purchase_id).code, // hoặc query map code nhanh hơn
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

                // 4) update materials.stock_qty
                var materialIds = materialAddMap.Keys.ToList();
                var materials = await _db.materials
                    .AsTracking()
                    .Where(m => materialIds.Contains(m.material_id))
                    .ToListAsync(ct);

                foreach (var m in materials)
                {
                    m.stock_qty = (m.stock_qty ?? 0) + materialAddMap[m.material_id];
                }

                // 5) update status PO
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

        public async Task<List<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
    CancellationToken ct = default)
        {
            var query =
                from p in _db.purchases.AsNoTracking()
                where p.status == "Pending"

                join s in _db.suppliers.AsNoTracking()
                    on p.supplier_id equals s.supplier_id into sup
                from s in sup.DefaultIfEmpty()

                join i in _db.purchase_items.AsNoTracking()
                    on p.purchase_id equals i.purchase_id into items
                from i in items.DefaultIfEmpty()

                group new { p, s, i } by new
                {
                    p.purchase_id,
                    p.code,
                    p.created_at,
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
                    g.Sum(x => (decimal?)(x.i != null ? (x.i.qty_ordered ?? 0) : 0)) ?? 0m
                );

            return await query.ToListAsync(ct);
        }


    }
}
