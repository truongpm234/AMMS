using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
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

                // Chỉ tính missing cho order đang cần kiểm tra vật tư
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
                    return new
                    {
                        insertedRows = 0,
                        message = "No target orders to recalculate."
                    };
                }

                var orderRequests = await _db.order_requests.AsNoTracking()
                    .Where(r => r.order_id != null && orderIds.Contains(r.order_id.Value))
                    .Select(r => new
                    {
                        r.order_request_id,
                        r.order_id,
                        r.accepted_estimate_id,
                        r.delivery_date
                    })
                    .ToListAsync(ct);

                if (orderRequests.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new
                    {
                        insertedRows = 0,
                        message = "No order request found."
                    };
                }

                var orderRequestIds = orderRequests
                    .Select(x => x.order_request_id)
                    .Distinct()
                    .ToList();

                var acceptedEstimateIds = orderRequests
                    .Where(x => x.accepted_estimate_id != null)
                    .Select(x => x.accepted_estimate_id!.Value)
                    .Distinct()
                    .ToList();

                var estimates = await _db.cost_estimates.AsNoTracking()
                    .Where(e =>
                        orderRequestIds.Contains(e.order_request_id) &&
                        (
                            e.is_active == true ||
                            acceptedEstimateIds.Contains(e.estimate_id)
                        ))
                    .Select(e => new
                    {
                        e.estimate_id,
                        e.order_request_id,
                        e.created_at,
                        e.desired_delivery_date,

                        e.sheets_total,
                        e.paper_code,
                        e.paper_name,
                        e.paper_alternative,

                        e.ink_weight_kg,

                        e.coating_glue_weight_kg,
                        e.coating_type,

                        e.mounting_glue_weight_kg,

                        e.wave_type,
                        e.wave_sheets_required,
                        e.wave_alternative,

                        e.lamination_weight_kg,
                        e.lamination_material_id,
                        e.lamination_material_code,
                        e.lamination_material_name
                    })
                    .ToListAsync(ct);

                if (estimates.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new
                    {
                        insertedRows = 0,
                        message = "No cost estimate found."
                    };
                }

                var materials = await _db.materials.AsNoTracking()
                    .ToListAsync(ct);

                static string Norm(string? value)
                {
                    return string.IsNullOrWhiteSpace(value)
                        ? ""
                        : value.Trim().ToUpperInvariant();
                }

                static decimal RoundQty(decimal value)
                {
                    return Math.Round(value, 4, MidpointRounding.AwayFromZero);
                }

                static decimal RoundUpToHundreds(decimal value)
                {
                    if (value <= 0m)
                        return 0m;

                    return Math.Ceiling(value / 100m) * 100m;
                }

                material? FindById(int? materialId)
                {
                    if (materialId == null || materialId <= 0)
                        return null;

                    return materials.FirstOrDefault(x => x.material_id == materialId.Value);
                }

                material? FindByCodeOrName(string? value)
                {
                    var key = Norm(value);
                    if (string.IsNullOrWhiteSpace(key))
                        return null;

                    var exactCode = materials.FirstOrDefault(x => Norm(x.code) == key);
                    if (exactCode != null)
                        return exactCode;

                    var exactName = materials.FirstOrDefault(x => Norm(x.name) == key);
                    if (exactName != null)
                        return exactName;

                    return materials.FirstOrDefault(x =>
                        Norm(x.code).Contains(key) ||
                        Norm(x.name).Contains(key));
                }

                material? FindByClassOrType(params string[] keys)
                {
                    foreach (var rawKey in keys)
                    {
                        var key = Norm(rawKey);
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        var exactClass = materials.FirstOrDefault(x => Norm(x.material_class) == key);
                        if (exactClass != null)
                            return exactClass;

                        var exactType = materials.FirstOrDefault(x => Norm(x.type) == key);
                        if (exactType != null)
                            return exactType;

                        var contains = materials.FirstOrDefault(x =>
                            Norm(x.material_class).Contains(key) ||
                            Norm(x.type).Contains(key) ||
                            Norm(x.code).Contains(key) ||
                            Norm(x.name).Contains(key));

                        if (contains != null)
                            return contains;
                    }

                    return null;
                }

                var demandLines = new List<MaterialDemandLine>();

                void AddDemand(material? mat, decimal qty, DateTime? requestDate)
                {
                    qty = RoundQty(qty);

                    if (qty <= 0m)
                        return;

                    if (mat == null)
                        return;

                    demandLines.Add(new MaterialDemandLine
                    {
                        MaterialId = mat.material_id,
                        MaterialName = mat.name ?? "",
                        Unit = mat.unit ?? "",
                        StockQty = mat.stock_qty ?? 0m,
                        CostPrice = mat.cost_price ?? 0m,
                        NeededQty = qty,
                        RequestDate = requestDate
                    });
                }

                foreach (var request in orderRequests)
                {
                    var estimate = request.accepted_estimate_id != null
                        ? estimates.FirstOrDefault(e => e.estimate_id == request.accepted_estimate_id.Value)
                        : estimates
                            .Where(e => e.order_request_id == request.order_request_id)
                            .OrderByDescending(e => e.created_at)
                            .ThenByDescending(e => e.estimate_id)
                            .FirstOrDefault();

                    if (estimate == null)
                        continue;

                    DateTime? requestDate = request.delivery_date ?? estimate.desired_delivery_date;

                    // 1. Giấy: sheets_total theo paper_alternative hoặc paper_code
                    var paperKey = !string.IsNullOrWhiteSpace(estimate.paper_alternative)
                        ? estimate.paper_alternative
                        : estimate.paper_code;

                    var paperMaterial = FindByCodeOrName(paperKey);

                    AddDemand(
                        paperMaterial,
                        estimate.sheets_total,
                        requestDate
                    );

                    // 2. Mực: ink_weight_kg
                    var inkMaterial =
                        FindByClassOrType("INK", "MUC", "MỰC") ??
                        FindByCodeOrName("MUC_IN");

                    AddDemand(
                        inkMaterial,
                        estimate.ink_weight_kg,
                        requestDate
                    );

                    // 3. Keo phủ: coating_glue_weight_kg, chỉ tính nếu coating_type có giá trị
                    var coatingType = Norm(estimate.coating_type);

                    var hasCoating =
                        !string.IsNullOrWhiteSpace(coatingType) &&
                        coatingType != "NONE" &&
                        coatingType != "NO" &&
                        coatingType != "KHONG" &&
                        coatingType != "KHÔNG";

                    if (hasCoating)
                    {
                        var coatingGlueMaterial =
                            FindByCodeOrName(estimate.coating_type) ??
                            FindByClassOrType(
                                "COATING_GLUE",
                                "COATING",
                                "KEO_PHU",
                                "KEO PHU",
                                "KEO PHỦ",
                                "PHU",
                                "PHỦ"
                            );

                        AddDemand(
                            coatingGlueMaterial,
                            estimate.coating_glue_weight_kg,
                            requestDate
                        );
                    }

                    // 4. Keo bồi: mounting_glue_weight_kg
                    var mountingGlueMaterial =
                        FindByClassOrType(
                            "MOUNTING_GLUE",
                            "MOUNTING",
                            "KEO_BOI",
                            "KEO BOI",
                            "KEO BỒI",
                            "BOI",
                            "BỒI"
                        );

                    AddDemand(
                        mountingGlueMaterial,
                        estimate.mounting_glue_weight_kg,
                        requestDate
                    );

                    // 5. Sóng: wave_sheets_required theo wave_alternative hoặc wave_type
                    var waveKey = !string.IsNullOrWhiteSpace(estimate.wave_alternative)
                        ? estimate.wave_alternative
                        : estimate.wave_type;

                    var waveMaterial = FindByCodeOrName(waveKey);

                    AddDemand(
                        waveMaterial,
                        estimate.wave_sheets_required ?? 0,
                        requestDate
                    );

                    // 6. Màng cán: lamination_weight_kg, ưu tiên lamination_material_id
                    var laminationMaterial =
                        FindById(estimate.lamination_material_id) ??
                        FindByCodeOrName(estimate.lamination_material_code) ??
                        FindByCodeOrName(estimate.lamination_material_name) ??
                        FindByClassOrType(
                            "LAMINATION",
                            "MANG",
                            "MÀNG",
                            "CAN_MANG",
                            "CAN MANG",
                            "CÁN MÀNG"
                        );

                    AddDemand(
                        laminationMaterial,
                        estimate.lamination_weight_kg   ,
                        requestDate
                    );
                }

                var now = AppTime.NowVnUnspecified();

                var insertRows = demandLines
    .GroupBy(x => new
    {
        x.MaterialId,
        x.MaterialName,
        x.Unit
    })
    .Select(g =>
    {
        var needed = RoundQty(g.Sum(x => x.NeededQty));
        var available = RoundQty(g.Max(x => x.StockQty));

        var missingQty = needed - available;
        if (missingQty < 0m)
            missingQty = 0m;

        var roundedMissingQty = RoundUpToHundreds(missingQty);

        var unitPrice = Math.Round(g.Max(x => x.CostPrice), 2);
        var totalPrice = Math.Round(roundedMissingQty * unitPrice, 2);

        var requestDateValue = g
            .Select(x => x.RequestDate)
            .Where(d => d != null)
            .OrderBy(d => d)
            .FirstOrDefault();

        return new missing_material
        {
            material_id = g.Key.MaterialId,
            material_name = g.Key.MaterialName ?? "",
            unit = g.Key.Unit ?? "",
            request_date = requestDateValue,

            needed = needed,
            available = available,

            // quantity luôn làm tròn lên hàng trăm
            quantity = roundedMissingQty,

            total_price = totalPrice,

            is_buy = false,
            created_at = now
        };
    })
    .Where(x => x.quantity > 0m)
    .ToList();

                await _db.missing_materials.AddRangeAsync(insertRows, ct);
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                return new
                {
                    insertedRows = insertRows.Count,
                    message = "Recalculated & saved missing materials successfully. Purchase was not created."
                };
            });
        }

        private sealed class MaterialDemandLine
        {
            public int MaterialId { get; set; }
            public string MaterialName { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal StockQty { get; set; }
            public decimal CostPrice { get; set; }
            public decimal NeededQty { get; set; }
            public DateTime? RequestDate { get; set; }
        }
    }
}
