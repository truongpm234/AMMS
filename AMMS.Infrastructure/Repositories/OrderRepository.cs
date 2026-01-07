// AMMS.Infrastructure/Repositories/OrderRepository.cs
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
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

        // ===== MAIN PAGED WITH FULFILL ===================================
        public async Task<List<OrderResponseDto>> GetPagedWithFulfillAsync(int skip, int take, CancellationToken ct = default)
        {
            // ===== Helpers ===================================================
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

            // 1) Lấy page orders (kèm fallback customer name)
            var orders = await (
                from o in _db.orders.AsNoTracking()

                    // quote -> customer
                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join c in _db.customers.AsNoTracking() on q.customer_id equals c.customer_id into cj
                from c in cj.DefaultIfEmpty()

                    // order_request fallback
                join r in _db.order_requests.AsNoTracking() on o.order_id equals r.order_id into rj
                from r in rj.DefaultIfEmpty()

                orderby o.order_date descending, o.order_id descending
                select new
                {
                    o.order_id,
                    o.code,
                    o.order_date,
                    o.delivery_date,
                    Status = o.status ?? "",

                    // ✅ FIX: customer_name fallback theo thứ tự:
                    // 1) orders.customer (nếu có customer_id)
                    // 2) quote->customer
                    // 3) order_request
                    customer_name =
                        // 1) nếu order có navigation customer (customer_id != null)
                        (o.customer != null
                            ? (o.customer.company_name ?? o.customer.contact_name ?? "")
                            : "") != ""
                        ? (o.customer!.company_name ?? o.customer!.contact_name ?? "")
                        : (
                            // 2) fallback quote->customer
                            (c != null
                                ? (c.company_name ?? c.contact_name ?? "")
                                : "") != ""
                            ? (c!.company_name ?? c!.contact_name ?? "")
                            : (
                                // 3) fallback order_request
                                (r != null ? (r.customer_name ?? "") : "")
                            )
                        ),

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

            // 2) Chỉ order có status Not Enough mới tính thiếu NVL
            var orderIdsNeedCalc = orders
                .Where(o => IsNotEnoughStatus(o.Status))
                .Select(o => o.order_id)
                .ToList();

            Dictionary<int, List<MissingMaterialDto>> missingByOrder = new();
            var ordersWithBom = new HashSet<int>();

            if (orderIdsNeedCalc.Count > 0)
            {
                // 2.1) BOM lines cho các order cần tính
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

                    // 2.2) Usage 30 ngày gần nhất từ stock_moves (OUT)
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

                    // 2.3) Tính thiếu NVL theo BOM + safety (30% usage 30 ngày)
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

            // 3) Build response
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
                        canFulfill = (missingMaterials == null || missingMaterials.Count == 0);
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
                    missing_materials = canFulfill == false
                        ? (missingMaterials ?? new List<MissingMaterialDto>())
                        : null
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
                    Order_id = o.order_id,
                    Code = o.code,
                    Order_date = o.order_date,
                    Delivery_date = o.delivery_date,
                    Status = o.status,
                    Payment_status = o.payment_status,
                    Quote_id = o.quote_id,
                    Total_amount = o.total_amount
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

            cost_estimate? estimate = null;
            if (req != null)
            {
                estimate = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.order_request_id == req.order_request_id, ct);
            }

            var item = order.order_items.OrderBy(i => i.item_id).FirstOrDefault();
            string customerName = string.Empty;
            string? customerEmail = null;
            string? customerPhone = null;

            if (order.customer_id.HasValue)
            {
                var customer = await _db.customers.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.customer_id == order.customer_id.Value, ct);

                if (customer != null)
                {
                    customerName = customer.company_name ?? customer.contact_name ?? customerName;
                    customerEmail = customer.email;
                    customerPhone = customer.phone;
                }
            }

            if (req != null)
            {
                if (string.IsNullOrWhiteSpace(customerName))
                    customerName = req.customer_name;
                customerEmail ??= req.customer_email;
                customerPhone ??= req.customer_phone;
            }

            var productName = item?.product_name ?? req?.product_name ?? string.Empty;
            var quantity = item?.quantity ?? req?.quantity ?? 0;

            var finalCost = estimate?.final_total_cost ?? order.total_amount ?? 0m;

            var deposit = estimate != null
                ? estimate.deposit_amount
                : Math.Round(finalCost * 0.30m, 0);

            var urlDesign = item?.design_url ?? req?.design_file_path;

            DateTime? prodStart = order.productions
                .Select(p => p.start_date)
                .Where(d => d != null)
                .OrderBy(d => d)
                .FirstOrDefault();

            DateTime? prodEnd = order.productions
                .Select(p => p.end_date)
                .Where(d => d != null)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            string approverName = order.productions
                .OrderByDescending(p => p.start_date ?? p.end_date ?? order.order_date)
                .Select(p => p.manager != null ? p.manager.full_name : null)
                .FirstOrDefault()
                ?? "Chưa cập nhật";

            string? specification = null;
            if (item != null)
            {
                var parts = new List<string>();
                if (parts.Count > 0) specification = string.Join(" | ", parts);
            }

            return new OrderDetailDto
            {
                order_id = order.order_id,
                code = order.code,
                status = order.status ?? "Scheduled",
                payment_status = order.payment_status ?? "Unpaid",
                order_date = (DateTime)order.order_date,
                delivery_date = order.delivery_date,
                production_id = order.production_id,
                customer_name = customerName,
                customer_email = customerEmail,
                customer_phone = customerPhone,
                detail_address = req?.detail_address,
                product_name = productName,
                quantity = quantity,
                production_start_date = prodStart,
                production_end_date = prodEnd,
                approver_name = approverName,
                specification = specification,
                note = req?.description,
                final_total_cost = finalCost,
                deposit_amount = deposit,
                rush_amount = estimate?.rush_amount ?? 0m,
                file_url = urlDesign,
                contract_file = null
            };
        }


        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        // ✅ CHANGED: Get ALL missing materials (NOT by orderId)
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
                        is_enough = isEnoughSummary
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

        public async Task<List<order>> GetAllOrderInprocessStatus()
        {
            return _db.orders.Where(o => o.productions.Any(p => p.status == "InProcessing")).ToList();

        }
    }
}
