using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class OrderMaterialRepository : IOrderMaterialRepository
    {
        private readonly AppDbContext _db;
        public OrderMaterialRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<OrderMaterialsResponse?> GetMaterialsByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var header = await (
                from o in _db.orders.AsNoTracking()
                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()
                join r in _db.order_requests.AsNoTracking() on q.order_request_id equals r.order_request_id into rj
                from r in rj.DefaultIfEmpty()
                join ce in _db.cost_estimates.AsNoTracking() on r.order_request_id equals ce.order_request_id into cej
                from ce in cej.DefaultIfEmpty()
                where o.order_id == orderId
                select new { o, r, ce }
            ).FirstOrDefaultAsync(ct);

            if (header == null) return null;

            var o1 = header.o;
            var r1 = header.r;
            var ce1 = header.ce;

            //if (r1 == null || ce1 == null)
            //{
            //    var bomLines = await (
            //        from oi in _db.order_items.AsNoTracking()
            //        join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
            //        where oi.order_id == orderId
            //        select new OrderMaterialLineDto
            //        {
            //            material_group = "BOM",
            //            material_code = b.material_code,
            //            material_name = b.material_name,
            //            unit = b.unit ?? "",
            //            quantity = b.qty_total ?? 0m
            //        }
            //    ).ToListAsync(ct);

            //    return new OrderMaterialsResponse
            //    {
            //        order_id = orderId,
            //        order_code = o1.code,
            //        order_request_id = null,
            //        items = bomLines
            //    };
            //}

            var paperCode = r1.paper_code;
            var paperName = r1.paper_name;

            if (!string.IsNullOrWhiteSpace(paperCode))
            {
                var pm = await _db.materials.AsNoTracking()
                    .Where(m => m.code == paperCode)
                    .Select(m => new { m.code, m.name, m.unit })
                    .FirstOrDefaultAsync(ct);

                if (pm != null)
                {
                    paperName = pm.name;
                }
            }

            var items = new List<OrderMaterialLineDto>();

            // PAPER
            if (ce1.sheets_total > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Giấy",
                    material_code = paperCode,
                    material_name = paperName ?? "Giấy",
                    unit = "tờ",
                    quantity = ce1.sheets_total
                });
            }

            // INK
            if (ce1.ink_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Mực in các loại",
                    material_code = "INK",
                    material_name = "Mực in",
                    unit = "kg",
                    quantity = ce1.ink_weight_kg
                });
            }

            // COATING GLUE
            if (ce1.coating_glue_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Keo phủ",
                    material_code = r1.coating_type,
                    material_name = "Keo phủ",
                    unit = "kg",
                    quantity = ce1.coating_glue_weight_kg
                });
            }

            // MOUNTING GLUE
            if (ce1.mounting_glue_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Keo bồi",
                    material_code = "KEO_BOI",
                    material_name = "Keo bồi",
                    unit = "kg",
                    quantity = ce1.mounting_glue_weight_kg
                });
            }

            // LAMINATION
            if (ce1.lamination_weight_kg > 0)
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Màng cán",
                    material_code = "MANG_12MIC",
                    material_name = "Màng cán 12 mic",
                    unit = "kg",
                    quantity = ce1.lamination_weight_kg
                });
            }

            if (!string.IsNullOrWhiteSpace(r1.wave_type))
            {
                items.Add(new OrderMaterialLineDto
                {
                    material_group = "Loại sóng",
                    material_code = r1.wave_type,
                    material_name = $"Sóng {r1.wave_type}"
                });
            }

            return new OrderMaterialsResponse
            {
                order_id = orderId,
                order_code = o1.code,
                order_request_id = r1.order_request_id,
                items = items
            };
        }
    }
}
