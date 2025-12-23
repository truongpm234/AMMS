using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class MaterialRepository : IMaterialRepository
    {
        private readonly AppDbContext _db;
        public MaterialRepository(AppDbContext db) => _db = db;

        public Task<material?> GetByCodeAsync(string code)
        {
            var c = (code ?? "").Trim().ToLower();
            return _db.materials.AsNoTracking()
                .FirstOrDefaultAsync(m => m.code.ToLower() == c);
        }

        public async Task<List<material>> GetAll()
        {
            return await _db.materials.AsNoTracking().ToListAsync();
        }

        public async Task<material> GetByIdAsync(int id)
        {
            return await _db.materials
                .FirstOrDefaultAsync(m => m.material_id == id);
        }

        public async Task UpdateAsync(material entity)
        {
            _db.materials.Update(entity);
            await Task.CompletedTask;
        }

        public async Task SaveChangeAsync()
        {
            await _db.SaveChangesAsync();
        }

        public async Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
     int page,
     int pageSize,
     CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            var skip = (page - 1) * pageSize;

            // ===== 1. Định nghĩa khoảng 30 ngày gần nhất (Dùng DateTime.Now.Date => Kind không phải UTC) =====
            var today = DateTime.Now.Date;
            var historyStartDate = today.AddDays(-30);
            var historyEndDate = today;

            // ===== 2. Lấy dữ liệu BOM + OrderItem + Material (chỉ JOIN, không nhân chia ở SQL) =====
            //  - Lưu ý: oi.quantity giả sử là int / int? -> cast sang decimal
            var bomRows = await (
                from b in _db.boms.AsNoTracking()
                join oi in _db.order_items.AsNoTracking()
                    on b.order_item_id equals oi.item_id
                join m in _db.materials.AsNoTracking()
                    on b.material_id equals m.material_id
                select new
                {
                    m.material_id,
                    m.code,
                    m.name,
                    m.unit,
                    StockQty = m.stock_qty ?? 0m,
                    OrderQty = (decimal)(oi.quantity),          // nếu quantity là int
                    QtyPerProduct = b.qty_per_product ?? 0m,
                    WastagePercent = b.wastage_percent ?? 0m
                }
            ).ToListAsync(ct);

            // Nếu không có BOM nào thì trả về rỗng luôn
            if (!bomRows.Any())
            {
                return new PagedResultLite<MaterialShortageDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = false,
                    Data = new List<MaterialShortageDto>()
                };
            }

            // ===== 3. Lấy usage 30 ngày gần nhất từ stock_moves (OUT) =====
            var usageLast30List = await _db.stock_moves
                .AsNoTracking()
                .Where(s =>
                    s.type == "OUT" &&
                    s.move_date >= historyStartDate &&
                    s.move_date <= historyEndDate &&
                    s.material_id != null)
                .GroupBy(s => s.material_id!.Value)
                .Select(g => new
                {
                    MaterialId = g.Key,
                    UsageLast30Days = g.Sum(s => s.qty ?? 0m)
                })
                .ToListAsync(ct);

            var usageDict = usageLast30List
                .ToDictionary(x => x.MaterialId, x => x.UsageLast30Days);

            // ===== 4. Tính toán RequiredQty, ShortageQty, NeedToBuyQty hoàn toàn ở C# =====
            var allMaterials = bomRows
                .GroupBy(x => new
                {
                    x.material_id,
                    x.code,
                    x.name,
                    x.unit,
                    x.StockQty
                })
                .Select(g =>
                {
                    decimal requiredQty = 0m;

                    // Tính tổng RequiredQty cho từng material
                    foreach (var r in g)
                    {
                        var orderQty = r.OrderQty;        // đã là decimal
                        var qtyPerProduct = r.QtyPerProduct;   // decimal
                        var wastePercent = r.WastagePercent;  // decimal

                        // baseQty = số sp * định mức
                        var baseQty = orderQty * qtyPerProduct;

                        // factor = 1 + % hao hụt / 100
                        var factor = 1m + (wastePercent / 100m);

                        var lineRequired = baseQty * factor;
                        if (lineRequired < 0m) lineRequired = 0m;

                        requiredQty += lineRequired;
                    }

                    var materialId = g.Key.material_id;
                    var stockQty = g.Key.StockQty;

                    // Usage 30 ngày gần nhất
                    usageDict.TryGetValue(materialId, out var usageLast30);
                    var safetyQty = usageLast30 * 0.30m;   // 30% usage 30 ngày

                    // Tổng nhu cầu = Required + safety (30% usage)
                    var totalNeeded = requiredQty + safetyQty;

                    var shortageQty = totalNeeded > stockQty
                        ? (totalNeeded - stockQty)
                        : 0m;

                    // Theo yêu cầu hiện tại: NeedToBuyQty >= ShortageQty (đã có thêm 30% safety)
                    var needToBuyQty = shortageQty;

                    return new
                    {
                        MaterialId = materialId,
                        g.Key.code,
                        g.Key.name,
                        g.Key.unit,
                        StockQty = stockQty,
                        RequiredQty = requiredQty,
                        ShortageQty = shortageQty,
                        NeedToBuyQty = needToBuyQty
                    };
                })
                .Where(x => x.ShortageQty > 0m)
                .OrderByDescending(x => x.ShortageQty)
                .ThenBy(x => x.name)
                .ToList();

            // ===== 5. Paging trên bộ nhớ =====
            var paged = allMaterials
                .Skip(skip)
                .Take(pageSize + 1)
                .ToList();

            var hasNext = paged.Count > pageSize;
            if (hasNext)
                paged = paged.Take(pageSize).ToList();

            // ===== 6. Map sang DTO =====
            var dtoList = paged
                .Select(x => new MaterialShortageDto(
                    x.MaterialId,
                    x.code,
                    x.name,
                    x.unit,
                    x.StockQty,
                    x.RequiredQty,
                    x.ShortageQty,
                    x.NeedToBuyQty
                ))
                .ToList();

            return new PagedResultLite<MaterialShortageDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = dtoList
            };
        }
    }
}
