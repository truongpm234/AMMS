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
    int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            // 1) BOM -> OrderItem -> Material, group theo material
            var requiredByMaterialQuery =
                from b in _db.boms.AsNoTracking()
                join oi in _db.order_items.AsNoTracking() on b.order_item_id equals oi.item_id
                join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                group new { b, oi, m } by new
                {
                    m.material_id,
                    m.code,
                    m.name,
                    m.unit,
                    StockQty = (decimal?)(m.stock_qty) ?? 0m
                }
                into g
                select new
                {
                    g.Key.material_id,
                    g.Key.code,
                    g.Key.name,
                    g.Key.unit,
                    StockQty = g.Key.StockQty,

                    // RequiredQty = SUM( order_item.qty * bom.qty_per_product * (1 + wastage%/100) )
                    RequiredQty = g.Sum(x =>
                        ((decimal)x.oi.quantity)
                        * ((decimal?)(x.b.qty_per_product) ?? 0m)
                        * (1m + (((decimal?)(x.b.wastage_percent) ?? 0m) / 100m))
                    )
                };

            // 2) Tính shortage thành field SQL-translatable
            var shortageQuery = requiredByMaterialQuery
                .Select(x => new
                {
                    x.material_id,
                    x.code,
                    x.name,
                    x.unit,
                    x.StockQty,
                    x.RequiredQty,
                    ShortageQty = x.RequiredQty > x.StockQty ? (x.RequiredQty - x.StockQty) : 0m
                });

            // 3) Lọc + sort trên field (KHÔNG dùng DTO)
            var filtered = shortageQuery
                .Where(x => x.ShortageQty > 0m)
                .OrderByDescending(x => x.ShortageQty)
                .ThenBy(x => x.name);

            // 4) paging
            var list = await filtered
                .Skip(skip)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = list.Count > pageSize;
            if (hasNext) list = list.Take(pageSize).ToList();

            // 5) map DTO sau khi lấy dữ liệu về
            var dtoList = list.Select(x => new MaterialShortageDto(
                x.material_id,
                x.code,
                x.name,
                x.unit,
                x.StockQty,
                x.RequiredQty,
                x.ShortageQty,
                x.ShortageQty // NeedToBuyQty = ShortageQty
            )).ToList();

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
