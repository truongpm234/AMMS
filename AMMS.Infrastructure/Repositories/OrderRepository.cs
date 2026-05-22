using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly AppDbContext _db;

        public OrderRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<OrderResponseDto>> GetPagedWithFulfillAsync(int skip, int take, CancellationToken ct = default)
        {
            static string ToUtcString(DateTime? dt)
            {
                if (dt is null) return "";
                var v = DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
                return v.ToString("O");
            }

            static bool IsNotEnoughStatus(string? status)
            {
                if (string.IsNullOrWhiteSpace(status)) return false;

                var s = status.Trim();
                return s.Equals("Not Enough", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("Not enough", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("0", StringComparison.OrdinalIgnoreCase);
            }

            var orders = await (
                from o in _db.orders.AsNoTracking()

                join q in _db.quotes.AsNoTracking()
                    on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking()
                    on o.order_id equals r.order_id into rj
                from r in rj.DefaultIfEmpty()

                join p in _db.productions.AsNoTracking()
                    on o.production_id equals p.prod_id into pj
                from p in pj.DefaultIfEmpty()

                orderby o.order_date descending, o.order_id descending

    select new
    {
        o.order_id,
        o.code,
        o.order_date,
        o.delivery_date,
        Status = o.status ?? "",
        is_production_ready = o.is_production_ready,
        customer_name = r != null ? (r.customer_name ?? "") : "Khách hàng",
        is_full_process = p != null ? (bool?)p.is_full_process : null,
        import_recieve_path = p != null ? p.import_recieve_path : null,
        production_method = p != null ? p.prod_method : null,
        sub_product_id = p != null ? p.sub_product_id : null,
        layout_confirmed = o.layout_confirmed,
        sub_product_used_qty = p != null ? p.sub_product_used_qty : 0,
        nvl_qty = p != null ? p.nvl_qty : 0,
        FirstItem = _db.order_items.AsNoTracking()
            .Where(i => i.order_id == o.order_id)
            .OrderBy(i => i.item_id)
            .Select(i => new
            {
                i.product_name,
                i.product_type_id,
                i.quantity
            })
            .FirstOrDefault()
    }
)
.Skip(skip)
.Take(take)
.ToListAsync(ct);

            if (orders.Count == 0) return new List<OrderResponseDto>();

            var orderIdsNeedCalc = orders
                .Where(o => IsNotEnoughStatus(o.Status))
                .Select(o => o.order_id)
                .ToList();

            Dictionary<int, List<MissingMaterialDto>> missingByOrder = new();
            var ordersWithBom = new HashSet<int>();

            if (orderIdsNeedCalc.Count > 0)
            {
                var bomLines = await (
                    from oi in _db.order_items.AsNoTracking()
                    join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                    join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                    where oi.order_id != null && orderIdsNeedCalc.Contains(oi.order_id.Value)
                    select new
                    {
                        OrderId = oi.order_id!.Value,
                        MaterialId = m.material_id,
                        MaterialName = m.name,
                        StockQty = m.stock_qty ?? 0m,

                        Quantity = (decimal)oi.quantity,
                        QtyPerProduct = b.qty_per_product ?? 0m,
                        WastagePercent = b.wastage_percent ?? 0m
                    }
                ).ToListAsync(ct);

                if (bomLines.Count > 0)
                {
                    ordersWithBom = bomLines.Select(x => x.OrderId).ToHashSet();

                    var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
                    var historyStart = DateTime.SpecifyKind(today.AddDays(-30), DateTimeKind.Unspecified);
                    var historyEndExclusive = DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Unspecified);

                    var materialIds = bomLines.Select(x => x.MaterialId).Distinct().ToList();

                    var usageLast30List = await _db.stock_moves
                        .AsNoTracking()
                        .Where(s =>
                            s.type == "OUT" &&
                            s.move_date >= historyStart &&
                            s.move_date < historyEndExclusive &&
                            s.material_id != null &&
                            materialIds.Contains(s.material_id.Value))
                        .GroupBy(s => s.material_id!.Value)
                        .Select(g => new
                        {
                            MaterialId = g.Key,
                            UsageLast30Days = g.Sum(x => x.qty ?? 0m)
                        })
                        .ToListAsync(ct);

                    var usageDict = usageLast30List
                        .ToDictionary(
                            x => x.MaterialId,
                            x => Math.Round(x.UsageLast30Days, 4)
                        );

                    missingByOrder = bomLines
                        .GroupBy(x => new { x.OrderId, x.MaterialId, x.MaterialName, x.StockQty })
                        .Select(g =>
                        {
                            decimal requiredQty = 0m;

                            foreach (var r in g)
                            {
                                var qty = r.Quantity;
                                var qtyPerProduct = Math.Round(r.QtyPerProduct, 4);
                                var wastePercent = Math.Round(r.WastagePercent, 2);

                                var baseQty = Math.Round(qty * qtyPerProduct, 4);
                                var factor = Math.Round(1m + (wastePercent / 100m), 4);
                                var lineRequired = Math.Round(baseQty * factor, 4);

                                if (lineRequired < 0m) lineRequired = 0m;
                                requiredQty += lineRequired;
                            }

                            usageDict.TryGetValue(g.Key.MaterialId, out var usage30);
                            var safetyQty = Math.Round(usage30 * 0.30m, 4);

                            var needed = Math.Round(requiredQty + safetyQty, 4);
                            var available = Math.Round(g.Key.StockQty, 4);

                            var missing = needed - available;
                            if (missing < 0m) missing = 0m;

                            return new
                            {
                                g.Key.OrderId,
                                g.Key.MaterialId,
                                g.Key.MaterialName,
                                Needed = needed,
                                Available = available,
                                Missing = missing
                            };
                        })
                        .Where(x => x.Missing > 0m)
                        .GroupBy(x => x.OrderId)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(x => new MissingMaterialDto
                            {
                                material_id = x.MaterialId,
                                material_name = x.MaterialName,
                                needed = x.Needed,
                                available = x.Available
                            }).ToList()
                        );
                }
            }

            return orders.Select(o =>
            {
                missingByOrder.TryGetValue(o.order_id, out var missingMaterials);

                bool canFulfill;

                if (IsNotEnoughStatus(o.Status))
                {
                    if (!ordersWithBom.Contains(o.order_id))
                    {
                        canFulfill = false;
                        missingMaterials ??= new List<MissingMaterialDto>();
                    }
                    else
                    {
                        canFulfill = missingMaterials == null || missingMaterials.Count == 0;
                    }
                }
                else
                {
                    canFulfill = true;
                }

                return new OrderResponseDto
                {
                    order_id = o.order_id.ToString(),
                    code = o.code,
                    customer_name = o.customer_name ?? "",
                    product_name = o.FirstItem?.product_name,
                    product_id = o.FirstItem?.product_type_id?.ToString(),
                    quantity = o.FirstItem?.quantity ?? 0,
                    created_at = ToUtcString(o.order_date),
                    delivery_date = ToUtcString(o.delivery_date),
                    status = o.Status,
                    can_fulfill = canFulfill,
                    is_production_ready = o.is_production_ready,
                    is_full_process = o.is_full_process,
                    production_method = o.production_method,
                    sub_product_id = o.sub_product_id,
                    sub_product_used_qty = o.sub_product_used_qty,
                    layout_confirmed = o.layout_confirmed,
                    nvl_qty = o.nvl_qty,
                    missing_materials = canFulfill == false
                        ? (missingMaterials ?? new List<MissingMaterialDto>())
                        : null,

                    import_recieve_path = o.import_recieve_path
                };
            }).ToList();
        }

        public async Task AddOrderAsync(order entity)
        {
            await _db.orders.AddAsync(entity);
        }

        public void Update(order entity)
        {
            _db.orders.Update(entity);
        }

        public Task<order?> GetByIdForUpdateAsync(int orderId, CancellationToken ct = default)
        {
            return _db.orders
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);
        }

        public async Task<order?> GetByIdAsync(int id)
        {
            return await _db.orders.FindAsync(id);
        }

        public Task<int> CountAsync()
        {
            return _db.orders.AsNoTracking().CountAsync();
        }

        public Task<List<OrderListDto>> GetPagedAsync(int skip, int take)
        {
            return _db.orders
                .AsNoTracking()
                .OrderByDescending(o => o.order_date)
                .Skip(skip)
                .Take(take)
                .Select(o => new OrderListDto
                {
                    order_id = o.order_id,
                    code = o.code,
                    order_date = o.order_date,
                    delivery_date = o.delivery_date,
                    status = o.status,
                    payment_status = o.payment_status,
                    quote_id = o.quote_id,
                    total_amount = o.total_amount,
                    is_enough = o.is_enough,
                    is_buy = o.is_buy,
                    production_id = o.production_id,
                    layout_confirmed = o.layout_confirmed,
                    is_production_ready = o.is_production_ready,
                    confirmed_delivery_at = o.confirmed_delivery_at
                })
                .ToListAsync();
        }

        public async Task<order?> GetByCodeAsync(string code)
        {
            return await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.code == code);
        }

        public async Task DeleteAsync(int id)
        {
            var order = await GetByIdAsync(id);
            if (order != null)
            {
                _db.orders.Remove(order);
            }
        }

        public async Task<int> SaveChangesAsync()
        {
            return await _db.SaveChangesAsync();
        }

        public Task AddOrderItemAsync(order_item entity)
            => _db.order_items.AddAsync(entity).AsTask();

        public async Task<string> GenerateNextOrderCodeAsync()
        {
            var last = await _db.orders.AsNoTracking()
                .OrderByDescending(x => x.order_id)
                .Select(x => x.code)
                .FirstOrDefaultAsync();

            int nextNum = 1;
            if (!string.IsNullOrWhiteSpace(last))
            {
                var digits = new string(last.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n)) nextNum = n + 1;
            }

            return $"ORD-{nextNum:00}";
        }

        public async Task<OrderDetailDto?> GetDetailByIdAsync(int orderId, CancellationToken ct = default)
        {
            static string MapProcessCode(string code) => (code ?? "").Trim().ToUpperInvariant() switch
            {
                "IN" => "In",
                "RALO" => "Ra lô",
                "CAT" => "Cắt",
                "CAN_MANG" => "Cán",
                "CAN" => "Cán",
                "BOI" => "Bồi",
                "PHU" => "Phủ",
                "DUT" => "Dứt",
                "DAN" => "Dán",
                "BE" => "Bế",
                _ => code
            };

            static string BuildProductionProcessText(order_request req, cost_estimate est)
            {
                var codes = new List<string>();

                if (!string.IsNullOrWhiteSpace(est.production_processes))
                {
                    codes = est.production_processes
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim().ToUpperInvariant())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()
                        .ToList();
                }
                else if (est.process_costs is { Count: > 0 })
                {
                    codes = est.process_costs
                        .Select(p => p.process_code)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => c.Trim().ToUpperInvariant())
                        .Distinct()
                        .ToList();
                }

                if (codes.Count == 0)
                    return "Không có / Không áp dụng";

                return string.Join(", ", codes.Select(MapProcessCode));
            }

            static string FormatVND(decimal? amount)
                => string.Format("{0:N0}", amount ?? 0).Replace(",", ".");

            var order = await _db.orders
                .AsNoTracking()
                .Include(o => o.order_items)
                .Include(o => o.productions)
                    .ThenInclude(p => p.manager)
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null) return null;

            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.order_id == orderId, ct);

            quote? q = null;
            if (req?.quote_id is int quoteId && quoteId > 0)
            {
                q = await _db.quotes.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.quote_id == quoteId, ct);
            }

            cost_estimate? est = null;
            if (req != null)
            {
                var estQuery = _db.cost_estimates
                    .AsNoTracking()
                    .Include(e => e.process_costs)
                    .Where(e => e.order_request_id == req.order_request_id);

                if (req.accepted_estimate_id is int aeid && aeid > 0)
                    est = await estQuery.FirstOrDefaultAsync(e => e.estimate_id == aeid, ct);

                est ??= await estQuery
                    .OrderByDescending(e => e.is_active)
                    .ThenByDescending(e => e.estimate_id)
                    .FirstOrDefaultAsync(ct);
            }

            string? displayPaperCode = null;
            string? displayPaperName = null;
            string? displayWaveType = null;

            if (est != null)
            {
                displayPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                    est.paper_alternative,
                    est.paper_code);

                displayWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                    est.wave_alternative,
                    est.wave_type);

                if (!string.IsNullOrWhiteSpace(displayPaperCode))
                {
                    displayPaperName = await _db.materials
                        .AsNoTracking()
                        .Where(x => x.code == displayPaperCode)
                        .Select(x => x.name)
                        .FirstOrDefaultAsync(ct) ?? est.paper_name ?? displayPaperCode;
                }
                else
                {
                    displayPaperName = est.paper_name;
                }
            }
            var item = order.order_items.OrderBy(i => i.item_id).FirstOrDefault();

            var productName = item?.product_name ?? req?.product_name ?? string.Empty;
            var quantity = item?.quantity ?? req?.quantity ?? 0;

            string customerName = req?.customer_name ?? "Khách hàng";
            string? customerEmail = req?.customer_email;
            string? customerPhone = req?.customer_phone;

            DateTime? prodStart = order.productions
                .Select(p => p.actual_start_date)
                .Where(d => d != null)
                .OrderBy(d => d)
                .FirstOrDefault();

            DateTime? prodEnd = order.productions
                .Select(p => p.end_date)
                .Where(d => d != null)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            string approverName = order.productions
                .OrderByDescending(p => p.actual_start_date ?? p.end_date ?? order.order_date)
                .Select(p => p.manager != null ? p.manager.full_name : null)
                .FirstOrDefault()
                ?? "Quản lí";

            var urlDesign = item?.design_url ?? req?.design_file_path;

            var finalCost = est?.final_total_cost ?? order.total_amount ?? 0m;

            var depositFallback = est?.deposit_amount ?? 0m;

            object? quoteFields = null;

            if (req != null && est != null && q != null)
            {
                var address = $"{req.detail_address}";
                var delivery = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A";
                var request_date = req.order_request_date?.ToString("dd/MM/yyyy HH:mm") ?? "N/A";
                var product_name = req.product_name ?? "";
                quantity = req.quantity ?? 0;

                var coating_type = MapCoatingType(est.coating_type);
                var paper_name = displayPaperName ?? "N/A";
                var wave_type = string.IsNullOrWhiteSpace(displayWaveType) ? "N/A" : displayWaveType;

                var design_type = req.is_send_design == true
                    ? "Tự gửi file thiết kế"
                    : "Sử dụng bản thiết kế của doanh nghiệp";

                var material_cost =
                    est.paper_cost +
                    est.ink_cost +
                    est.coating_glue_cost +
                    est.mounting_glue_cost +
                    est.lamination_cost;

                var labor_cost = est.process_costs != null
                    ? est.process_costs
                        .Where(p => p.estimate_id == est.estimate_id)
                        .Sum(p => p.total_cost)
                    : 0m;

                var other_fees = est.design_cost;
                var rush_amount = est.rush_amount;
                var sub_total = est.subtotal;
                var final_total = est.final_total_cost;
                var discount_percent = est.discount_percent;
                var discount_amount = est.discount_amount;
                var deposit = est.deposit_amount;
                var production_process = BuildProductionProcessText(req, est);
                var expired_at = q.created_at.AddHours(24);
                var expired = expired_at.ToString("dd/MM/yyyy HH:mm");


                var contract_paper_code = est.paper_code;
                var contract_paper_name = est.paper_name;
                var contract_wave_type = est.wave_type;
                var paper_alternative = est.paper_alternative;
                var wave_alternative = est.wave_alternative;

                quoteFields = new
                {
                    request_date,
                    paper_code = displayPaperCode,
                    paper_name,
                    coating_type,
                    wave_type,
                    contract_paper_code,
                    contract_paper_name,
                    contract_wave_type,
                    paper_alternative,
                    wave_alternative,
                    design_type,
                    production_process,
                    material_cost,
                    labor_cost,
                    other_fees,
                    rush_amount,
                    sub_total,
                    discount_percent,
                    discount_amount,
                    lamination_material_code = est.lamination_material_code,
                    lamination_material_name = est.lamination_material_name,
                };
            }

            return new OrderDetailDto
            {
                order_id = order.order_id,
                code = order.code,
                status = order.status ?? "Scheduled",
                payment_status = order.payment_status ?? "Unpaid",
                order_date = AsUnspecified(order.order_date) ?? default,
                delivery_date = AsUnspecified(order.delivery_date),
                production_id = order.production_id,
                layout_confirmed = order.layout_confirmed,
                customer_name = customerName,
                customer_email = customerEmail,
                customer_phone = customerPhone,
                detail_address = req?.detail_address,
                ink_type_names = est.ink_type_names,
                product_name = productName,
                quantity = quantity,

                production_start_date = AsUnspecified(prodStart),
                production_end_date = AsUnspecified(prodEnd),
                approver_name = approverName,

                specification = null,
                note = req?.description,

                final_total_cost = finalCost,
                deposit_amount = depositFallback,
                rush_amount = est?.rush_amount ?? 0m,

                file_url = urlDesign,
                contract_file = null,

                lamination_material_id = est?.lamination_material_id,
                lamination_material_code = est?.lamination_material_code,
                lamination_material_name = est?.lamination_material_name,

                quote_fields = quoteFields
            };
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        public async Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            var lines = await (
                from oi in _db.order_items.AsNoTracking()
                join o in _db.orders.AsNoTracking() on oi.order_id equals o.order_id
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                select new
                {
                    m.material_id,
                    material_name = m.name,
                    unit = m.unit,
                    request_date = o.delivery_date,

                    // ✅ is_enough from orders
                    is_enough = o.is_enough,
                    is_buy = o.is_buy,

                    available = (double)(m.stock_qty ?? 0m),
                    unit_price = (double)(m.cost_price ?? 0m),

                    needed_line =
                        (double)oi.quantity
                        * (double)(b.qty_per_product ?? 0m)
                        * (1.0 + ((double)(b.wastage_percent ?? 0m) / 100.0))
                }
            ).ToListAsync(ct);

            static decimal SafeToDecimal(double v, int round)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0m;
                var max = (double)decimal.MaxValue;
                if (v > max) return decimal.MaxValue;
                if (v < -max) return decimal.MinValue;
                return Math.Round((decimal)v, round);
            }

            static decimal SafeMul(decimal a, decimal b)
            {
                try { return a * b; }
                catch (OverflowException) { return decimal.MaxValue; }
            }

            var grouped = lines
                .GroupBy(x => new { x.material_id, x.material_name, x.unit })
                .Select(g =>
                {
                    var neededD = g.Sum(t => t.needed_line);
                    var availableD = g.Max(t => t.available);
                    var requestDate = g.Min(t => t.request_date);
                    var unitPriceD = g.Max(t => t.unit_price);

                    var needed = SafeToDecimal(neededD, 4);
                    var available = SafeToDecimal(availableD, 4);

                    // ✅ BASE missing ONLY (no buffer, no rounding)
                    var missingBase = needed - available;
                    if (missingBase < 0m) missingBase = 0m;

                    var unitPrice = SafeToDecimal(unitPriceD, 2);
                    var totalPriceBase = SafeMul(missingBase, unitPrice);
                    totalPriceBase = Math.Round(totalPriceBase, 2);

                    // summary is_enough
                    var anyFalse = g.Any(x => x.is_enough == false);
                    var anyTrue = g.Any(x => x.is_enough == true);
                    bool? isEnoughSummary =
                        anyFalse ? false :
                        (anyTrue && !g.Any(x => x.is_enough == null) ? true : (bool?)null);
                    var anyBuyFalse = g.Any(x => x.is_buy == false);
                    var anyBuyTrue = g.Any(x => x.is_buy == true);
                    bool? isBuySummary = g.Any(x => x.is_buy == true) ? true : (bool?)false;


                    return new MissingMaterialDto
                    {
                        material_id = g.Key.material_id,
                        material_name = g.Key.material_name,
                        unit = g.Key.unit,
                        request_date = requestDate,

                        needed = needed,
                        available = available,

                        // ✅ quantity = missingBase
                        quantity = missingBase,
                        total_price = totalPriceBase,
                        is_buy = isBuySummary

                    };
                })
                .Where(x => x.quantity > 0m)
                .OrderByDescending(x => x.quantity)
                .ToList();

            var pageRows = grouped.Skip(skip).Take(pageSize + 1).ToList();
            var hasNext = pageRows.Count > pageSize;
            if (hasNext) pageRows.RemoveAt(pageRows.Count - 1);

            return new PagedResultLite<MissingMaterialDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = pageRows
            };
        }

        public async Task<string> DeleteDesignFilePath(int orderRequestId)
        {
            var designFilePath = _db.order_requests.SingleOrDefault(f => f.order_request_id == orderRequestId);
            if (designFilePath != null)
            {
                designFilePath.design_file_path = "";
                _db.SaveChanges();
                return "Delete Success";
            }
            return "Delete False";
        }

        public async Task<object> BuyMaterialAndRecalcOrdersAsync(
            int materialId,
            decimal quantity,
            int managerUserId,
            CancellationToken ct = default)
        {
            if (materialId <= 0) throw new ArgumentException("materialId invalid");
            if (quantity <= 0) throw new ArgumentException("quantity must be > 0");

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync<object>(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

                // 1) Load material (tracked)
                var material = await _db.materials
                    .AsTracking()
                    .FirstOrDefaultAsync(m => m.material_id == materialId, ct);

                if (material == null)
                    throw new ArgumentException($"material_id={materialId} not found");

                // 2) Update stock
                material.stock_qty = (material.stock_qty ?? 0m) + quantity;

                // 3) Add stock_move IN
                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = materialId,
                    type = "IN",
                    qty = quantity,
                    ref_doc = $"BUY-MATERIAL-{materialId}",
                    user_id = managerUserId,
                    move_date = now,
                    note = "Buy material via API"
                }, ct);

                await _db.SaveChangesAsync(ct);

                // 4) Find affected orders (orders that use this material in BOM and not enough yet)
                var affectedOrderIds = await (
                    from o in _db.orders.AsNoTracking()
                    join oi in _db.order_items.AsNoTracking() on o.order_id equals oi.order_id
                    join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                    where b.material_id == materialId
                          && (o.is_enough == null || o.is_enough == false)
                    select o.order_id
                ).Distinct().ToListAsync(ct);

                if (affectedOrderIds.Count == 0)
                {
                    await tx.CommitAsync(ct);
                    return new
                    {
                        materialId,
                        addedQty = quantity,
                        newStockQty = material.stock_qty ?? 0m,
                        affectedOrders = 0,
                        ordersUpdated = 0,
                        message = "Stock updated. No affected orders to recalc."
                    };
                }

                // 5) Preload usage 30 days OUT for all materials used in affected orders
                //    (to keep your missing-material logic consistent)
                var materialIdsInAffectedOrders = await (
                    from oi in _db.order_items.AsNoTracking()
                    join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                    where oi.order_id != null && affectedOrderIds.Contains(oi.order_id.Value)
                          && b.material_id != null
                    select b.material_id!.Value
                ).Distinct().ToListAsync(ct);

                var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
                var historyStart = DateTime.SpecifyKind(today.AddDays(-30), DateTimeKind.Unspecified);
                var historyEndExclusive = DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Unspecified);

                var usageLast30 = await _db.stock_moves
                    .AsNoTracking()
                    .Where(s =>
                        s.type == "OUT" &&
                        s.move_date >= historyStart &&
                        s.move_date < historyEndExclusive &&
                        s.material_id != null &&
                        materialIdsInAffectedOrders.Contains(s.material_id.Value))
                    .GroupBy(s => s.material_id!.Value)
                    .Select(g => new
                    {
                        MaterialId = g.Key,
                        UsageLast30Days = g.Sum(x => x.qty ?? 0m)
                    })
                    .ToListAsync(ct);

                var usageDict = usageLast30.ToDictionary(
                    x => x.MaterialId,
                    x => Math.Round(x.UsageLast30Days, 4)
                );

                // 6) Recalc each order + update is_buy / is_enough
                var ordersToUpdate = await _db.orders
                    .AsTracking()
                    .Where(o => affectedOrderIds.Contains(o.order_id))
                    .ToListAsync(ct);

                int updated = 0;
                int nowEnough = 0;

                foreach (var o in ordersToUpdate)
                {
                    // mark is_buy true because you performed a buy action
                    o.is_buy = true;

                    var enough = await IsOrderEnoughByBomAsync(o.order_id, usageDict, ct);

                    o.is_enough = enough;
                    if (enough) nowEnough++;

                    updated++;
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new
                {
                    materialId,
                    addedQty = quantity,
                    newStockQty = material.stock_qty ?? 0m,
                    affectedOrders = affectedOrderIds.Count,
                    ordersUpdated = updated,
                    ordersNowEnough = nowEnough,
                    message = "Stock updated + orders recalculated"
                };
            });
        }

        /// <summary>
        /// Check if an order is enough materials based on BOM + wastage + safety(30% usage last 30 days),
        /// using CURRENT materials.stock_qty.
        /// </summary>
        private async Task<bool> IsOrderEnoughByBomAsync(
            int orderId,
            Dictionary<int, decimal> usageDict,
            CancellationToken ct)
        {
            // load BOM lines for that order
            var bomLines = await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                where oi.order_id == orderId
                select new
                {
                    MaterialId = m.material_id,
                    StockQty = m.stock_qty ?? 0m,
                    Quantity = (decimal)oi.quantity,
                    QtyPerProduct = b.qty_per_product ?? 0m,
                    WastagePercent = b.wastage_percent ?? 0m
                }
            ).ToListAsync(ct);

            if (bomLines.Count == 0)
            {
                // No BOM => cannot guarantee enough -> keep false to be safe
                return false;
            }

            // group per material
            var perMaterial = bomLines
                .GroupBy(x => x.MaterialId)
                .Select(g =>
                {
                    decimal required = 0m;
                    foreach (var r in g)
                    {
                        var baseQty = Math.Round(r.Quantity * Math.Round(r.QtyPerProduct, 4), 4);
                        var factor = Math.Round(1m + (Math.Round(r.WastagePercent, 2) / 100m), 4);
                        var lineRequired = Math.Round(baseQty * factor, 4);
                        if (lineRequired < 0m) lineRequired = 0m;
                        required += lineRequired;
                    }

                    usageDict.TryGetValue(g.Key, out var usage30);
                    var safety = Math.Round(usage30 * 0.30m, 4);

                    var needed = Math.Round(required + safety, 4);
                    var available = Math.Round(g.Max(x => x.StockQty), 4);

                    var missing = needed - available;
                    if (missing < 0m) missing = 0m;

                    return new { MaterialId = g.Key, Missing = missing };
                })
                .ToList();

            // If any missing > 0 => not enough
            return perMaterial.All(x => x.Missing <= 0m);
        }

        public async Task<List<order>> GetAllOrderInprocessStatus()
        {
            return _db.orders.Where(o => o.productions.Any(p => p.status == "InProcessing")).ToList();


        }

        public async Task MarkOrdersBuyByMaterialAsync(int materialId, CancellationToken ct = default)
        {
            // Orders nào có BOM chứa materialId -> set is_buy=true
            var orderIds = await (
                from o in _db.orders.AsNoTracking()
                join oi in _db.order_items.AsNoTracking() on o.order_id equals oi.order_id
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                where b.material_id == materialId
                select o.order_id
            ).Distinct().ToListAsync(ct);

            if (orderIds.Count == 0) return;

            var orders = await _db.orders
                .AsTracking()
                .Where(o => orderIds.Contains(o.order_id))
                .ToListAsync(ct);

            foreach (var o in orders)
                o.is_buy = true;
        }

        public async Task RecalculateIsEnoughForOrdersAsync(CancellationToken ct = default)
        {
            // 1) Load lines nhưng dùng double để tránh Npgsql overflow khi đọc numeric quá lớn
            var lines = await (
    from oi in _db.order_items.AsNoTracking()
    join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
    join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
    where oi.order_id != null && b.material_id != null
    select new
    {
        OrderId = oi.order_id!.Value,
        MaterialId = b.material_id!.Value,

        // ✅ CAST trước, rồi mới ?? 0d
        StockQtyD = (double?)m.stock_qty ?? 0d,
        QtyD = (double)oi.quantity,
        QtyPerProductD = (double?)b.qty_per_product ?? 0d,
        WastagePercentD = (double?)b.wastage_percent ?? 0d,
    }
).ToListAsync(ct);


            if (lines.Count == 0) return;

            static decimal SafeToDecimal(double v, int round = 4)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0m;
                var max = (double)decimal.MaxValue;
                if (v > max) return decimal.MaxValue;
                if (v < -max) return decimal.MinValue;
                return Math.Round((decimal)v, round);
            }

            // 2) Tính required trong memory (double -> decimal safe)
            var reqByOrderMaterial = lines
                .GroupBy(x => new { x.OrderId, x.MaterialId })
                .Select(g =>
                {
                    double requiredLineSumD = 0;

                    foreach (var r in g)
                    {
                        var factor = 1.0 + (r.WastagePercentD / 100.0);
                        var reqLine = r.QtyD * r.QtyPerProductD * factor;
                        if (reqLine < 0) reqLine = 0;
                        requiredLineSumD += reqLine;
                    }

                    var required = SafeToDecimal(requiredLineSumD, 4);
                    var stockQty = SafeToDecimal(g.Max(t => t.StockQtyD), 4);

                    return new
                    {
                        g.Key.OrderId,
                        Required = required,
                        StockQty = stockQty
                    };
                })
                .ToList();

            var isEnoughMap = reqByOrderMaterial
                .GroupBy(x => x.OrderId)
                .ToDictionary(
                    g => g.Key,
                    g => g.All(x => x.StockQty >= x.Required)
                );

            var orderIds = isEnoughMap.Keys.ToList();

            var orders = await _db.orders
                .AsTracking()
                .Where(o => orderIds.Contains(o.order_id))
                .ToListAsync(ct);

            foreach (var o in orders)
                o.is_enough = isEnoughMap.TryGetValue(o.order_id, out var ok) ? ok : (bool?)null;
        }

        public async Task MarkOrdersBuyByMaterialsAsync(List<int> materialIds, CancellationToken ct = default)
        {
            materialIds = materialIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            if (materialIds.Count == 0) return;

            var orderIds = await (
                from o in _db.orders.AsNoTracking()
                join oi in _db.order_items.AsNoTracking() on o.order_id equals oi.order_id
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                where b.material_id != null && materialIds.Contains(b.material_id.Value)
                select o.order_id
            ).Distinct().ToListAsync(ct);

            if (orderIds.Count == 0) return;

            var orders = await _db.orders
                .AsTracking()
                .Where(o => orderIds.Contains(o.order_id))
                .ToListAsync(ct);

            foreach (var o in orders)
                o.is_buy = true;
        }

        public async Task<List<order_item>> GetOrderItemsByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _db.order_items
                .Where(x => x.order_id == orderId)
                .ToListAsync(ct);
        }

        public async Task<bool> IsOrderEnoughByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var bomHasNullMaterial = await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                where oi.order_id == orderId
                select b.material_id
            ).AnyAsync(x => x == null, ct);

            if (bomHasNullMaterial)
                return false;

            return await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id
                where oi.order_id == orderId
                group new { oi, b, m } by b.material_id into g
                select new
                {
                    Required = g.Sum(x =>
                        ((decimal)x.oi.quantity)
                        * (x.b.qty_per_product ?? 0m)
                        * (1m + ((x.b.wastage_percent ?? 0m) / 100m))
                    ),
                    StockQty = g.Max(x => x.m.stock_qty ?? 0m)
                }
            ).AllAsync(x => x.StockQty >= x.Required, ct);
        }

        private static DateTime? AsUnspecified(DateTime? dt)
        {
            if (dt == null) return null;
            return DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified);
        }
        static string MapCoatingType(string? coatingType) => (coatingType ?? "").Trim().ToUpperInvariant() switch
        {
            "KEO_NUOC" => "Keo nước",
            "KEO_DAU" => "Keo dầu",
            "" => "Keo Nước",
            _ => coatingType ?? "Keo nước"
        };

        public async Task<OrderProductionTrackingRawResult> GetProductionTrackingByOrderStatusAsync(
    string status,
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            var orderRows = await _db.orders
                .AsNoTracking()
                .Where(o => o.status == status)
                .OrderByDescending(o => o.order_date)
                .ThenByDescending(o => o.order_id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .ToListAsync(ct);

            var hasNext = orderRows.Count > pageSize;

            if (hasNext)
                orderRows.RemoveAt(orderRows.Count - 1);

            if (orderRows.Count == 0)
            {
                return new OrderProductionTrackingRawResult
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = false
                };
            }

            var orderIds = orderRows
                .Select(o => o.order_id)
                .Distinct()
                .ToList();

            var orderProductionIds = orderRows
                .Where(o => o.production_id != null)
                .Select(o => o.production_id!.Value)
                .Distinct()
                .ToList();

            var requests = await _db.order_requests
                .AsNoTracking()
                .Where(r => r.order_id != null && orderIds.Contains(r.order_id.Value))
                .Select(r => new OrderRequestLiteRaw
                {
                    order_request_id = r.order_request_id,
                    order_id = r.order_id
                })
                .ToListAsync(ct);

            var prodOrders = await _db.prod_orders
                .AsNoTracking()
                .Where(po => orderIds.Contains(po.order_id))
                .Select(po => new ProdOrderLiteRaw
                {
                    order_id = po.order_id,
                    prod_id = po.prod_id,
                    single_prod_id = po.single_prod_id
                })
                .ToListAsync(ct);

            var prodIdsFromProdOrders = prodOrders
                .Select(x => x.prod_id)
                .Concat(
                    prodOrders
                        .Where(x => x.single_prod_id != null)
                        .Select(x => x.single_prod_id!.Value)
                )
                .Distinct()
                .ToList();

            var allRelatedProdIds = orderProductionIds
                .Concat(prodIdsFromProdOrders)
                .Distinct()
                .ToList();

            var productions = await _db.productions
                .AsNoTracking()
                .Where(p =>
                    (p.order_id != null && orderIds.Contains(p.order_id.Value)) ||
                    allRelatedProdIds.Contains(p.prod_id))
                .ToListAsync(ct);

            var productionIds = productions
                .Select(p => p.prod_id)
                .Distinct()
                .ToList();

            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id != null && productionIds.Contains(t.prod_id.Value))
                .OrderBy(t => t.seq_num)
                .ThenBy(t => t.task_id)
                .ToListAsync(ct);

            var taskIds = tasks
                .Select(t => t.task_id)
                .Distinct()
                .ToList();

            var taskLogs = await _db.task_logs
                .AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .OrderByDescending(l => l.log_time)
                .ThenByDescending(l => l.log_id)
                .ToListAsync(ct);

            return new OrderProductionTrackingRawResult
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,

                Orders = orderRows,
                Requests = requests,
                Productions = productions,
                Tasks = tasks,
                TaskLogs = taskLogs,
                ProdOrders = prodOrders
            };
        }

        public async Task<OrdersByProcessRawResult> GetOrdersByProcessCodeRawAsync(
    string processCode,
    CancellationToken ct = default)
        {
            processCode = (processCode ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(processCode))
            {
                return new OrdersByProcessRawResult();
            }

            // 1. Tìm process_id trong product_type_process theo process_code
            var matchedProcessIds = await _db.product_type_processes
                .AsNoTracking()
                .Where(p =>
                    p.process_code != null &&
                    p.process_code.ToUpper() == processCode)
                .Select(p => p.process_id)
                .Distinct()
                .ToListAsync(ct);

            // 2. Tìm task có liên quan đến công đoạn
            var candidateTasks = await _db.tasks
                .AsNoTracking()
                .Where(t =>
                    (t.process_id != null && matchedProcessIds.Contains(t.process_id.Value)) ||
                    ((t.name ?? "").ToUpper() == processCode) ||
                    ((t.machine ?? "").ToUpper() == processCode))
                .ToListAsync(ct);

            if (candidateTasks.Count == 0)
            {
                return new OrdersByProcessRawResult();
            }

            var candidateProdIds = candidateTasks
                .Where(t => t.prod_id != null)
                .Select(t => t.prod_id!.Value)
                .Distinct()
                .ToList();

            if (candidateProdIds.Count == 0)
            {
                return new OrdersByProcessRawResult();
            }

            // 3. Tìm production ứng viên
            var candidateProductions = await _db.productions
                .AsNoTracking()
                .Where(p => candidateProdIds.Contains(p.prod_id))
                .ToListAsync(ct);

            // 4. Tìm order liên quan qua prod_orders
            var candidateProdOrders = await _db.prod_orders
                .AsNoTracking()
                .Where(po =>
                    candidateProdIds.Contains(po.prod_id) ||
                    (po.single_prod_id != null && candidateProdIds.Contains(po.single_prod_id.Value)))
                .Select(po => new ProdOrderLiteRaw
                {
                    order_id = po.order_id,
                    prod_id = po.prod_id,
                    single_prod_id = po.single_prod_id
                })
                .ToListAsync(ct);

            var orderIdsFromProductions = candidateProductions
                .Where(p => p.order_id != null)
                .Select(p => p.order_id!.Value)
                .Distinct()
                .ToList();

            var orderIdsFromProdOrders = candidateProdOrders
                .Select(x => x.order_id)
                .Distinct()
                .ToList();

            var orderIdsFromOrderProductionId = await _db.orders
                .AsNoTracking()
                .Where(o =>
                    o.production_id != null &&
                    candidateProdIds.Contains(o.production_id.Value))
                .Select(o => o.order_id)
                .Distinct()
                .ToListAsync(ct);

            var candidateOrderIds = orderIdsFromProductions
                .Concat(orderIdsFromProdOrders)
                .Concat(orderIdsFromOrderProductionId)
                .Distinct()
                .ToList();

            if (candidateOrderIds.Count == 0)
            {
                return new OrdersByProcessRawResult();
            }

            // 5. Lấy orders
            var orders = await _db.orders
                .AsNoTracking()
                .Where(o => candidateOrderIds.Contains(o.order_id))
                .OrderByDescending(o => o.order_date)
                .ThenByDescending(o => o.order_id)
                .ToListAsync(ct);

            if (orders.Count == 0)
            {
                return new OrdersByProcessRawResult();
            }

            var orderIds = orders
                .Select(o => o.order_id)
                .Distinct()
                .ToList();

            // 6. Lấy production_id gắn trực tiếp trong orders
            var orderProductionIds = orders
                .Where(o => o.production_id != null)
                .Select(o => o.production_id!.Value)
                .Distinct()
                .ToList();

            // 7. Lấy prod_orders theo order
            var prodOrders = await _db.prod_orders
                .AsNoTracking()
                .Where(po => orderIds.Contains(po.order_id))
                .Select(po => new ProdOrderLiteRaw
                {
                    order_id = po.order_id,
                    prod_id = po.prod_id,
                    single_prod_id = po.single_prod_id
                })
                .ToListAsync(ct);

            var prodIdsFromProdOrders = prodOrders
                .Select(x => x.prod_id)
                .Concat(
                    prodOrders
                        .Where(x => x.single_prod_id != null)
                        .Select(x => x.single_prod_id!.Value)
                )
                .Distinct()
                .ToList();

            // 8. Gom toàn bộ production liên quan tới order
            var allRelatedProdIds = orderProductionIds
                .Concat(prodIdsFromProdOrders)
                .Concat(
                    candidateProductions
                        .Where(p => p.order_id != null && orderIds.Contains(p.order_id.Value))
                        .Select(p => p.prod_id)
                )
                .Distinct()
                .ToList();

            var productions = await _db.productions
                .AsNoTracking()
                .Where(p =>
                    allRelatedProdIds.Contains(p.prod_id) ||
                    (p.order_id != null && orderIds.Contains(p.order_id.Value)))
                .ToListAsync(ct);

            var productionIds = productions
                .Select(p => p.prod_id)
                .Distinct()
                .ToList();

            // 9. Lấy tasks của các production
            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(t =>
                    t.prod_id != null &&
                    productionIds.Contains(t.prod_id.Value))
                .OrderBy(t => t.seq_num)
                .ThenBy(t => t.task_id)
                .ToListAsync(ct);

            // 10. Lấy process info cho task
            var processIdsOfTasks = tasks
                .Where(t => t.process_id != null)
                .Select(t => t.process_id!.Value)
                .Distinct()
                .ToList();

            var processRows = await _db.product_type_processes
                .AsNoTracking()
                .Where(p => processIdsOfTasks.Contains(p.process_id))
                .Select(p => new
                {
                    p.process_id,
                    p.process_code,
                    p.process_name
                })
                .ToListAsync(ct);

            var taskProcesses = tasks
                .Select(t =>
                {
                    var p = t.process_id == null
                        ? null
                        : processRows.FirstOrDefault(x => x.process_id == t.process_id.Value);

                    return new TaskProcessLiteRaw
                    {
                        task_id = t.task_id,
                        process_id = t.process_id,
                        process_code = p?.process_code,
                        process_name = p?.process_name
                    };
                })
                .ToList();

            return new OrdersByProcessRawResult
            {
                Orders = orders,
                Productions = productions,
                Tasks = tasks,
                ProdOrders = prodOrders,
                TaskProcesses = taskProcesses
            };
        }
    }
}
