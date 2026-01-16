using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Materials;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class MaterialPurchaseRequestService : IMaterialPurchaseRequestService
    {
        private readonly AppDbContext _db;

        public MaterialPurchaseRequestService(AppDbContext db)
        {
            _db = db;
        }
        private async Task<List<MaterialShortageDto>> GetShortagesForOrderAsync(
            int orderId,
            CancellationToken ct)
        {
            var raw = await (
                from oi in _db.order_items
                join b in _db.boms on oi.item_id equals b.order_item_id
                join m in _db.materials on b.material_id equals m.material_id
                where oi.order_id == orderId
                select new
                {
                    OrderQty = (decimal)oi.quantity,
                    BomQtyPerProduct = b.qty_per_product ?? 0m,
                    WastagePercent = b.wastage_percent ?? 0m,
                    Material = m
                }
            ).ToListAsync(ct);

            if (!raw.Any())
                return new List<MaterialShortageDto>();

            var grouped = raw
    .GroupBy(x => x.Material.material_id)
    .Select(g =>
    {
        var m = g.First().Material;

        decimal required = g.Sum(x =>
        {
            var factor = 1m + (x.WastagePercent / 100m);
            return x.OrderQty * x.BomQtyPerProduct * factor;
        });

        decimal stock = m.stock_qty ?? 0m;
        decimal shortage = required > stock ? required - stock : 0m;

        return new MaterialShortageDto
        {
            MaterialId = m.material_id,
            Code = m.code,
            Name = m.name,
            Unit = m.unit,
            StockQty = stock,
            RequiredQty = required,
            ShortageQty = shortage,
            NeedToBuyQty = shortage
        };
    }).Where(x => x.ShortageQty > 0m).ToList();

            return grouped;
        }
        private async Task<string> GenerateNextPurchaseCodeAsync(CancellationToken ct)
        {
            var lastCode = await _db.purchases.AsNoTracking()
                .OrderByDescending(p => p.purchase_id)
                .Select(p => p.code)
                .FirstOrDefaultAsync(ct);

            int nextNum = 1;
            if (!string.IsNullOrWhiteSpace(lastCode))
            {
                var digits = new string(lastCode.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n))
                    nextNum = n + 1;
            }

            return $"PO-{nextNum:0000}";
        }

        public async Task<AutoPurchaseResultDto> CreateFromOrderAsync(
    int orderId,
    int managerId,
    CancellationToken ct = default)
        {
            var order = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null)
                throw new KeyNotFoundException("Order not found");

            var shortages = await GetShortagesForOrderAsync(orderId, ct);
            if (!shortages.Any())
                throw new InvalidOperationException("Đơn hàng không thiếu nguyên vật liệu.");

            var code = await GenerateNextPurchaseCodeAsync(ct);

            var purchase = new purchase
            {
                code = code,
                supplier_id = null,      
                created_by = managerId,
                status = "Pending",
                created_at = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
            };

            await _db.purchases.AddAsync(purchase, ct);
            await _db.SaveChangesAsync(ct);

            const decimal bufferPercent = 0.30m; // mua dư 30%

            foreach (var s in shortages)
            {
                if (s.ShortageQty <= 0) continue;

                var buyQty = s.ShortageQty * (1 + bufferPercent);

                buyQty = decimal.Round(buyQty, 2, MidpointRounding.AwayFromZero);

                s.NeedToBuyQty = buyQty;

                var item = new purchase_item
                {
                    purchase_id = purchase.purchase_id,
                    material_id = s.MaterialId,
                    qty_ordered = buyQty,   
                    price = 0m        
                };

                await _db.purchase_items.AddAsync(item, ct);
            }

            await _db.SaveChangesAsync(ct);

            return new AutoPurchaseResultDto
            {
                PurchaseId = purchase.purchase_id,
                PurchaseCode = purchase.code!,
                Items = shortages  
            };
        }
    }
}
