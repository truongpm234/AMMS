using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities.AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

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

            var query = _db.missing_materials.AsNoTracking()
                .OrderByDescending(x => x.is_buy)
                .ThenByDescending(x => x.quantity)
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
                    quantity = x.quantity,
                    total_price = x.total_price,
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

                _db.missing_materials.RemoveRange(_db.missing_materials);
                await _db.SaveChangesAsync(ct);

                // Giữ đúng logic nghiệp vụ: chỉ tính missing cho order đang LayoutPending
                var orderIds = await _db.orders.AsNoTracking()
                    .Where(o =>
                        (o.status == "LayoutPending" || o.status == "Scheduled") &&
                        (o.is_enough == null || o.is_enough == false))
                    .Select(o => o.order_id)
                    .Distinct()
                    .ToListAsync(ct);

                if (orderIds.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { insertedRows = 0, message = "No target orders to recalculate." };
                }

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

                        OrderQty = oi.quantity,
                        QtyTotal = b.qty_total,
                        QtyPerProduct = b.qty_per_product,
                        WastagePercent = b.wastage_percent
                    }
                ).ToListAsync(ct);

                if (bomLines.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new { insertedRows = 0, message = "No BOM lines found." };
                }

                var today = AppTime.NowVnUnspecified().Date;
                var historyStart = today.AddDays(-30);
                var historyEndExclusive = today.AddDays(1);

                var materialIdsInBom = bomLines.Select(x => x.MaterialId).Distinct().ToList();

                var usageLast30 = await _db.stock_moves.AsNoTracking()
                    .Where(s =>
                        s.type == "OUT" &&
                        s.move_date >= historyStart &&
                        s.move_date < historyEndExclusive &&
                        s.material_id != null &&
                        materialIdsInBom.Contains(s.material_id.Value))
                    .GroupBy(s => s.material_id!.Value)
                    .Select(g => new
                    {
                        MaterialId = g.Key,
                        Usage = g.Sum(x => x.qty ?? 0m)
                    })
                    .ToListAsync(ct);

                var usageDict = usageLast30.ToDictionary(
                    x => x.MaterialId,
                    x => Math.Round(x.Usage, 4));

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

                var now = AppTime.NowVnUnspecified();

                var insertRows = bomLines
                    .GroupBy(x => new { x.MaterialId, x.MaterialName, x.Unit })
                    .Select(g =>
                    {
                        decimal requiredQty = 0m;

                        foreach (var r in g)
                        {
                            decimal lineRequired = 0m;

                            // Ưu tiên qty_total nếu BOM đã lưu sẵn tổng lượng cần
                            if (r.QtyTotal.HasValue && r.QtyTotal.Value > 0m)
                            {
                                lineRequired = Math.Round(r.QtyTotal.Value, 4);
                            }
                            else if (r.QtyPerProduct.HasValue && r.QtyPerProduct.Value > 0m)
                            {
                                var baseQty = Math.Round(r.OrderQty * r.QtyPerProduct.Value, 4);
                                var factor = Math.Round(1m + ((r.WastagePercent ?? 0m) / 100m), 4);
                                lineRequired = Math.Round(baseQty * factor, 4);
                            }
                            else
                            {
                                // Không lấy mặc định = 0 gây sai lệch như code cũ
                                continue;
                            }

                            if (lineRequired < 0m) lineRequired = 0m;
                            requiredQty += lineRequired;
                        }

                        usageDict.TryGetValue(g.Key.MaterialId, out var usage30);
                        var safetyQty = Math.Round(usage30 * 0.30m, 4);

                        var needed = Math.Round(requiredQty + safetyQty, 0, MidpointRounding.AwayFromZero);
                        var available = Math.Round(g.Max(x => x.StockQty), 4);

                        var baseMissing = needed - available;
                        if (baseMissing < 0m) baseMissing = 0m;

                        orderedOutstandingDict.TryGetValue(g.Key.MaterialId, out var orderedOutstanding);
                        var remaining = baseMissing - orderedOutstanding;
                        if (remaining < 0m) remaining = 0m;

                        var unitPrice = Math.Round(g.Max(x => x.CostPrice), 2);
                        var totalPrice = Math.Round(remaining * unitPrice, 2);

                        var requestDateValue = g
                            .Select(x => x.DeliveryDate)
                            .Where(d => d != null)
                            .OrderBy(d => d)
                            .FirstOrDefault();

                        var isBuy = baseMissing > 0m && remaining == 0m;

                        return new missing_material
                        {
                            material_id = g.Key.MaterialId,
                            material_name = g.Key.MaterialName ?? "",
                            unit = g.Key.Unit ?? "",
                            request_date = requestDateValue,
                            needed = needed,
                            available = available,
                            quantity = remaining,
                            total_price = totalPrice,
                            is_buy = isBuy,
                            created_at = now
                        };
                    })
                    .Where(x => x.needed > x.available)
                    .ToList();

                await _db.missing_materials.AddRangeAsync(insertRows, ct);
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                return new
                {
                    insertedRows = insertRows.Count,
                    message = "Recalculated & saved missing materials successfully."
                };
            });
        }
    }
}
