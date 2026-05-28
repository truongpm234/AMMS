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
                    miss_id = x.miss_id,
                    material_id = x.material_id,
                    material_name = x.material_name,
                    unit = x.unit,
                    request_date = x.request_date,
                    needed = x.needed,
                    available = x.available,
                    quantity = x.quantity,
                    total_price = x.total_price,
                    is_buy = x.is_buy,
                    file_purpose = x.file_purpose,
                    is_active = x.is_active
                }).ToList()
            };
        }

        public async Task<object> RecalculateAndSaveAsync(CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            object? result = null;

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                async Task<int> MarkAllNoLongerMissingAsync()
                {
                    var oldRows = await _db.missing_materials
                        .Where(x => x.quantity > 0m)
                        .ToListAsync(ct);

                    foreach (var row in oldRows)
                    {
                        row.needed = 0m;
                        row.quantity = 0m;
                        row.total_price = 0m;

                        if (string.IsNullOrWhiteSpace(row.file_purpose))
                            row.file_purpose = "MATERIAL_PURCHASE";
                    }

                    await _db.SaveChangesAsync(ct);
                    return oldRows.Count;
                }

                // Không xóa bảng nữa
                // _db.missing_materials.RemoveRange(_db.missing_materials);
                // await _db.SaveChangesAsync(ct);

                var orderIds = await _db.orders.AsNoTracking()
                    .Where(o =>
                        (o.status == "LayoutPending" || o.status == "Scheduled") &&
                        (o.is_enough == null || o.is_enough == false))
                    .Select(o => o.order_id)
                    .Distinct()
                    .ToListAsync(ct);

                if (orderIds.Count == 0)
                {
                    var noLongerMissingCount = await MarkAllNoLongerMissingAsync();

                    await tx.CommitAsync(ct);

                    result = new
                    {
                        insertedRows = 0,
                        updatedRows = 0,
                        noLongerMissingRows = noLongerMissingCount,
                        message = "No target orders to recalculate. Existing missing materials were marked as no longer missing."
                    };

                    return;
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
                    var noLongerMissingCount = await MarkAllNoLongerMissingAsync();

                    await tx.CommitAsync(ct);

                    result = new
                    {
                        insertedRows = 0,
                        updatedRows = 0,
                        noLongerMissingRows = noLongerMissingCount,
                        message = "No order request found. Existing missing materials were marked as no longer missing."
                    };

                    return;
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
                    var noLongerMissingCount = await MarkAllNoLongerMissingAsync();

                    await tx.CommitAsync(ct);

                    result = new
                    {
                        insertedRows = 0,
                        updatedRows = 0,
                        noLongerMissingRows = noLongerMissingCount,
                        message = "No cost estimate found. Existing missing materials were marked as no longer missing."
                    };

                    return;
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

                    // 1. Giấy
                    var paperKey = !string.IsNullOrWhiteSpace(estimate.paper_alternative)
                        ? estimate.paper_alternative
                        : estimate.paper_code;

                    var paperMaterial = FindByCodeOrName(paperKey);

                    AddDemand(
                        paperMaterial,
                        estimate.sheets_total,
                        requestDate
                    );

                    // 2. Mực
                    var inkMaterial =
                        FindByClassOrType("INK", "MUC", "MỰC") ??
                        FindByCodeOrName("MUC_IN");

                    AddDemand(
                        inkMaterial,
                        estimate.ink_weight_kg,
                        requestDate
                    );

                    // 3. Keo phủ
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

                    // 4. Keo bồi
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

                    // 5. Sóng
                    var waveKey = !string.IsNullOrWhiteSpace(estimate.wave_alternative)
                        ? estimate.wave_alternative
                        : estimate.wave_type;

                    var waveMaterial = FindByCodeOrName(waveKey);

                    AddDemand(
                        waveMaterial,
                        estimate.wave_sheets_required ?? 0,
                        requestDate
                    );

                    // 6. Màng cán
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
                        estimate.lamination_weight_kg,
                        requestDate
                    );
                }

                var now = AppTime.NowVnUnspecified();

                var calculatedRows = demandLines
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
                            quantity = roundedMissingQty,
                            total_price = totalPrice,

                            is_buy = false,
                            created_at = now,
                            file_purpose = "MATERIAL_PURCHASE"
                        };
                    })
                    .Where(x => x.quantity > 0m)
                    .ToList();

                var calculatedMaterialIds = calculatedRows
                    .Select(x => x.material_id)
                    .Distinct()
                    .ToList();

                var existingRows = await _db.missing_materials
    .Where(x => calculatedMaterialIds.Contains(x.material_id))
    .OrderBy(x => x.material_id)
    .ThenBy(x => x.miss_id)
    .ToListAsync(ct);

                // Dòng đã mua xong + đã có PDF thì bị khóa, không update nữa.
                // Nếu material_id đó phát sinh thiếu lại thì tạo miss_id mới.
                var lockedPurchasedRows = existingRows
                    .Where(IsClosedPurchasedMissing)
                    .ToList();

                // Chỉ được gộp vào những dòng chưa bị khóa.
                var editableExistingRows = existingRows
                    .Where(x => !IsClosedPurchasedMissing(x))
                    .ToList();

                // Với mỗi material_id, chọn dòng editable có miss_id nhỏ nhất để gộp.
                var editableByMaterialId = editableExistingRows
                    .GroupBy(x => x.material_id)
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(x => x.miss_id).First()
                    );

                int insertedCount = 0;
                int updatedCount = 0;
                int lockedCreateNewCount = 0;

                foreach (var newRow in calculatedRows)
                {
                    if (editableByMaterialId.TryGetValue(newRow.material_id, out var existing))
                    {
                        // Gộp vào miss_id cũ nếu dòng đó chưa thỏa:
                        // is_buy = true, is_active = false, có PDF.
                        existing.material_name = newRow.material_name;
                        existing.unit = newRow.unit;
                        existing.request_date = newRow.request_date;

                        existing.needed = newRow.needed;
                        existing.available = newRow.available;
                        existing.quantity = newRow.quantity;
                        existing.total_price = newRow.total_price;

                        // Giữ trạng thái đã mua nếu trước đó đã true.
                        existing.is_buy = existing.is_buy == true ? true : newRow.is_buy;

                        existing.created_at = now;

                        if (string.IsNullOrWhiteSpace(existing.file_purpose))
                        {
                            existing.file_purpose = "MATERIAL_PURCHASE";
                        }

                        updatedCount++;
                    }
                    else
                    {
                        // Không có dòng editable để gộp.
                        // Có thể là material mới hoàn toàn,
                        // hoặc material_id chỉ đang có miss_id cũ đã mua xong + có PDF.
                        var hasLockedSameMaterial = lockedPurchasedRows
                            .Any(x => x.material_id == newRow.material_id);

                        if (hasLockedSameMaterial)
                            lockedCreateNewCount++;

                        newRow.file_purpose = "MATERIAL_PURCHASE";

                        // Nếu entity của bạn có is_active default true thì dòng này có thể bỏ.
                        // Nếu muốn chắc chắn miss_id mới đang active thì giữ.
                        newRow.is_active = true;

                        await _db.missing_materials.AddAsync(newRow, ct);
                        insertedCount++;
                    }
                }

                // Không set quantity = 0 nữa.
                // Chỉ đếm những dòng không còn nằm trong kết quả tính toán mới.
                var noLongerMissingRows = await _db.missing_materials
                    .Where(x =>
                        !calculatedMaterialIds.Contains(x.material_id) &&
                        x.quantity > 0m)
                    .ToListAsync(ct);

                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                result = new
                {
                    insertedRows = insertedCount,
                    updatedRows = updatedCount,
                    noLongerMissingRows = noLongerMissingRows.Count,
                    message = "Recalculated missing materials successfully"
                };

                return;
            });

            return result ?? new
            {
                insertedRows = 0,
                updatedRows = 0,
                noLongerMissingRows = 0,
                message = "Recalculate finished but no result was returned."
            };
        }

        public async Task<List<MissingMaterialPurchasePdfRowDto>> GetPurchasePdfRowsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            var rows = await _db.missing_materials
                .AsNoTracking()
                .Where(x => x.quantity > 0m)
                .OrderByDescending(x => x.quantity)
                .ThenByDescending(x => x.created_at)
                .Skip(skip)
                .Take(pageSize)
                .Select(x => new MissingMaterialPurchasePdfRowDto
                {
                    miss_id = x.miss_id,
                    material_id = x.material_id,
                    material_name = x.material_name,
                    quantity = x.quantity,
                    total_price = x.total_price
                })
                .ToListAsync(ct);

            return rows;
        }

        public async Task<int> UpdateFilePurposeAsync( string filePurpose, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePurpose))
                return 0;

            var rows = await _db.missing_materials
                .Where(x => x.quantity > 0m)
                .ToListAsync(ct);

            if (rows.Count == 0)
                return 0;

            foreach (var row in rows)
            {
                row.file_purpose = filePurpose;
            }

            await _db.SaveChangesAsync(ct);

            return rows.Count;
        }

        public async Task<List<MissingMaterialPurchasePdfRowDto>> GetPurchasePdfRowsByMissIdsAsync(
    List<long> missIds,
    CancellationToken ct = default)
        {
            missIds = missIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<long>();

            if (missIds.Count == 0)
                return new List<MissingMaterialPurchasePdfRowDto>();

            var rows = await _db.missing_materials
                .AsNoTracking()
                .Where(x =>
                    missIds.Contains(x.miss_id) &&
                    x.quantity > 0m)
                .OrderBy(x => x.miss_id)
                .Select(x => new MissingMaterialPurchasePdfRowDto
                {
                    miss_id = x.miss_id,
                    material_id = x.material_id,
                    material_name = x.material_name,
                    quantity = x.quantity,
                    total_price = x.total_price
                })
                .ToListAsync(ct);

            return rows;
        }

        public async Task<int> UpdateFilePurposeByMissIdsAsync(
            List<long> missIds,
            string filePurpose,
            CancellationToken ct = default)
        {
            missIds = missIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<long>();

            if (missIds.Count == 0 || string.IsNullOrWhiteSpace(filePurpose))
                return 0;

            var rows = await _db.missing_materials
                .Where(x => missIds.Contains(x.miss_id))
                .ToListAsync(ct);

            if (rows.Count == 0)
                return 0;

            foreach (var row in rows)
            {
                row.file_purpose = filePurpose;
            }

            await _db.SaveChangesAsync(ct);

            return rows.Count;
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

        static bool HasPurchasePdf(string? filePurpose)
        {
            if (string.IsNullOrWhiteSpace(filePurpose))
                return false;

            var value = filePurpose.Trim();

            if (value.Equals("MATERIAL_PURCHASE", StringComparison.OrdinalIgnoreCase))
                return false;

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsClosedPurchasedMissing(missing_material row)
        {
            return row.is_buy == true
                && row.is_active == false
                && HasPurchasePdf(row.file_purpose);
        }
    }
}
