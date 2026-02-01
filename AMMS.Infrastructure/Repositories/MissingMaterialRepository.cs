using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities.AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class MissingMaterialRepository : IMissingMaterialRepository
    {
        private readonly AppDbContext _db;

        public MissingMaterialRepository(AppDbContext db)
        {
            _db = db;
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        public async Task<PagedResultLite<MissingMaterialDto>> GetPagedFromDbAsync(int page, int pageSize, CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            // ✅ show cả trường hợp remaining = 0 nhưng is_buy = true
            // (để UI thấy "đã mua đủ")
            var query = _db.missing_materials.AsNoTracking()
                .OrderByDescending(x => x.is_buy)         // mua đủ lên trước
                .ThenByDescending(x => x.quantity)        // còn thiếu nhiều lên trước
                .ThenByDescending(x => x.created_at);

            var rows = await query.Skip(skip).Take(pageSize + 1).ToListAsync(ct);
            var hasNext = rows.Count > pageSize;
            if (hasNext) rows.RemoveAt(rows.Count - 1);

            return new PagedResultLite<MissingMaterialDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = rows.Select(x => new MissingMaterialDto
                {
                    material_id = x.material_id,
                    material_name = x.material_name,
                    unit = x.unit,
                    request_date = x.request_date,
                    needed = x.needed,
                    available = x.available,

                    // ✅ remaining missing in DB (đã trừ outstanding Ordered)
                    quantity = x.quantity,
                    total_price = x.total_price,

                    // ✅ is_buy = true khi remaining==0 (và baseMissing>0)
                    is_buy = x.is_buy
                }).ToList()
            };
        }

        public async Task<object> RecalculateAndSaveAsync(CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // 1) clear table (tính lại toàn bộ)
                _db.missing_materials.RemoveRange(_db.missing_materials);
                await _db.SaveChangesAsync(ct);

                // 2) chỉ tính cho order "thiếu" (giống tinh thần hiện tại)
                var orderIds = await _db.orders.AsNoTracking()
                    .Where(o =>
                        o.is_enough == null || o.is_enough == false ||
                        (o.status != null && (o.status == "Not Enough" || o.status == "Not enough")))
                    .Select(o => o.order_id)
                    .Distinct()
                    .ToListAsync(ct);

                if (orderIds.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { insertedRows = 0, message = "No target orders to recalculate." };
                }

                // 3) load BOM lines
                var bomLines = await (
                    from oi in _db.order_items.AsNoTracking()
                    join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                    join o in _db.orders.AsNoTracking() on oi.order_id equals o.order_id
                    join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                    where oi.order_id != null && orderIds.Contains(oi.order_id.Value)
                    select new
                    {
                        MaterialId = m.material_id,
                        MaterialName = m.name,
                        Unit = m.unit,
                        StockQty = m.stock_qty ?? 0m,
                        CostPrice = m.cost_price ?? 0m,

                        DeliveryDate = o.delivery_date,

                        OrderQty = (decimal)oi.quantity,
                        QtyPerProduct = b.qty_per_product ?? 0m,
                        WastagePercent = b.wastage_percent ?? 0m
                    }
                ).ToListAsync(ct);

                if (bomLines.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { insertedRows = 0, message = "No BOM lines found." };
                }

                // 4) usage 30 ngày OUT
                var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
                var historyStart = DateTime.SpecifyKind(today.AddDays(-30), DateTimeKind.Unspecified);
                var historyEndExclusive = DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Unspecified);

                var materialIdsInBom = bomLines.Select(x => x.MaterialId).Distinct().ToList();

                var usageLast30 = await _db.stock_moves.AsNoTracking()
                    .Where(s =>
                        s.type == "OUT" &&
                        s.move_date >= historyStart &&
                        s.move_date < historyEndExclusive &&
                        s.material_id != null &&
                        materialIdsInBom.Contains(s.material_id.Value))
                    .GroupBy(s => s.material_id!.Value)
                    .Select(g => new { MaterialId = g.Key, Usage = g.Sum(x => x.qty ?? 0m) })
                    .ToListAsync(ct);

                var usageDict = usageLast30.ToDictionary(x => x.MaterialId, x => Math.Round(x.Usage, 4));

                // ✅ 5) OUTSTANDING purchase orders (status = "Ordered") - chỉ PO, không tính request Pending, không tính Delivered
                var orderedOutstandingList = await (
                    from pi in _db.purchase_items.AsNoTracking()
                    join p in _db.purchases.AsNoTracking() on pi.purchase_id equals p.purchase_id
                    where p.status == "Ordered"
                          && pi.material_id != null
                          && materialIdsInBom.Contains(pi.material_id.Value)
                    group pi by pi.material_id!.Value into g
                    select new
                    {
                        MaterialId = g.Key,
                        QtyOrdered = g.Sum(x => x.qty_ordered ?? 0m)
                    }
                ).ToListAsync(ct);

                var orderedOutstandingDict = orderedOutstandingList.ToDictionary(
                    x => x.MaterialId,
                    x => Math.Round(x.QtyOrdered, 4)
                );

                // 6) group theo material → needed = required + safety(30% usage)
                var now = DateTime.UtcNow;


                var insertRows = bomLines
                    .GroupBy(x => new { x.MaterialId, x.MaterialName, x.Unit })
                    .Select(g =>
                    {
                        decimal requiredQty = 0m;

                        foreach (var r in g)
                        {
                            var baseQty = Math.Round(r.OrderQty * Math.Round(r.QtyPerProduct, 4), 4);
                            var factor = Math.Round(1m + (Math.Round(r.WastagePercent, 2) / 100m), 4);
                            var lineRequired = Math.Round(baseQty * factor, 4);
                            if (lineRequired < 0m) lineRequired = 0m;
                            requiredQty += lineRequired;
                        }

                        usageDict.TryGetValue(g.Key.MaterialId, out var usage30);
                        var safetyQty = Math.Round(usage30 * 0.30m, 4); // ✅ safety giữ nguyên

                        var needed = Math.Round(requiredQty + safetyQty, 4);
                        var available = Math.Round(g.Max(x => x.StockQty), 4);

                        // ✅ base missing theo BOM/stock
                        var baseMissing = needed - available;
                        if (baseMissing < 0m) baseMissing = 0m;

                        // ✅ subtract outstanding Ordered PO
                        orderedOutstandingDict.TryGetValue(g.Key.MaterialId, out var orderedOutstanding);
                        var remaining = baseMissing - orderedOutstanding;
                        if (remaining < 0m) remaining = 0m;

                        var unitPrice = Math.Round(g.Max(x => x.CostPrice), 2);
                        var totalPrice = Math.Round(remaining * unitPrice, 2);

                        var requestDate = g
                        .Select(x => x.DeliveryDate)
                        .Where(d => d != null)
                        .OrderBy(d => d)
                        .FirstOrDefault();

                        var requestDateUtc = ToUtc(requestDate);


                        // ✅ is_buy=true khi đã order đủ để remaining=0 (và thực sự có thiếu ban đầu)
                        var isBuy = baseMissing > 0m && remaining == 0m;

                        return new missing_material
                        {
                            material_id = g.Key.MaterialId,
                            material_name = g.Key.MaterialName ?? "",
                            unit = g.Key.Unit ?? "",

                            request_date = requestDateUtc,

                            needed = needed,
                            available = available,

                            // ✅ store remaining missing (sau khi trừ PO Ordered)
                            quantity = remaining,
                            total_price = totalPrice,

                            is_buy = isBuy,
                            created_at = now
                        };
                    })
                    // ✅ Insert cả trường hợp remaining=0 nhưng baseMissing>0 (để show is_buy=true)
                    .Where(x => x.needed > x.available)
                    .ToList();

                await _db.missing_materials.AddRangeAsync(insertRows, ct);
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                return new
                {
                    insertedRows = insertRows.Count,
                    message = "Recalculated & saved missing materials (baseMissing - orderedOutstanding)."
                };
            });
        }

        private static DateTime ToUtc(DateTime dt)
        {
            return dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc) // Unspecified => treat as UTC
            };
        }

        private static DateTime? ToUtc(DateTime? dt)
        {
            if (dt == null) return null;
            return ToUtc(dt.Value);
        }
    }
}
