using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Shared.DTOs.Planning;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class OrderPlanningService : IOrderPlanningService
    {
        private readonly AppDbContext _db;

        public OrderPlanningService(AppDbContext db)
        {
            _db = db;
        }

        private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

        private static string GetUnit(string processCode)
        {
            processCode = Norm(processCode);
            if (processCode is "IN" or "PHU" or "CAN") return "tờ";
            if (processCode is "DAN") return "sp";
            return "tờ";
        }

        private static decimal GetRequiredUnits(string processCode, int orderQty, decimal totalAreaM2, int sheetsTotal)
        {
            processCode = Norm(processCode);

            if (processCode is "IN" or "PHU" or "CAN" or "CAN")
                return totalAreaM2 <= 0 ? 0 : totalAreaM2;

            if (processCode is "DAN")
                return orderQty <= 0 ? 0 : orderQty;

            return sheetsTotal <= 0 ? 0 : sheetsTotal;
        }

        private static decimal ConvertLogQtyToUnits(string processCode, int qtyGood, decimal areaPerSheetM2)
        {
            processCode = Norm(processCode);
            if (qtyGood <= 0) return 0m;

            if (processCode is "IN" or "PHU" or "CAN" or "CAN")
            {
                if (areaPerSheetM2 > 0)
                    return qtyGood * areaPerSheetM2;

                return qtyGood;
            }

            return qtyGood;
        }

        private async Task<Dictionary<string, decimal>> GetDailyCapacityByProcessCodeAsync(CancellationToken ct)
        {
            var rows = await _db.machines.AsNoTracking()
                .Where(m => m.is_active && m.process_code != null && m.process_code != "")
                .Select(m => new
                {
                    process_code = m.process_code!,
                    daily_capacity =
                        (decimal)m.quantity
                        * m.capacity_per_hour
                        * m.working_hours_per_day
                        * m.efficiency_percent / 100m
                })
                .ToListAsync(ct);

            return rows
                .GroupBy(x => Norm(x.process_code))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.daily_capacity));
        }

        private async Task<Dictionary<string, decimal>> GetBacklogUnitsByProcessCodeAsync(CancellationToken ct)
        {
            var logSum = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null)
                .GroupBy(l => l.task_id!.Value)
                .Select(g => new
                {
                    task_id = g.Key,
                    qty_good = g.Sum(x => x.qty_good ?? 0)
                })
                .ToDictionaryAsync(x => x.task_id, x => x.qty_good, ct);

            var openTasks = await (
                from t in _db.tasks.AsNoTracking()
                join pr in _db.productions.AsNoTracking() on t.prod_id equals pr.prod_id
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join ce in _db.cost_estimates.AsNoTracking() on q.order_request_id equals ce.order_request_id into cej
                from ce in cej.DefaultIfEmpty()

                join ptp in _db.product_type_processes.AsNoTracking() on t.process_id equals ptp.process_id into ptpj
                from ptp in ptpj.DefaultIfEmpty()

                where t.end_time == null
                      && (t.status == null || t.status != "Finished")
                select new
                {
                    t.task_id,
                    order_id = o.order_id,
                    order_request_id = (int?)q.order_request_id,

                    process_code = ptp.process_code,
                    total_area_m2 = (decimal?)ce.total_area_m2,
                    sheets_total = (int?)ce.sheets_total
                }
            ).ToListAsync(ct);

            // 3) map order_id -> total quantity (sp)
            var orderQtyMap = await _db.order_items.AsNoTracking()
                .GroupBy(i => i.order_id)
                .Select(g => new { order_id = g.Key ?? 0, qty = g.Sum(x => (int?)x.quantity) ?? 0 })
                .ToDictionaryAsync(x => x.order_id, x => x.qty, ct);

            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in openTasks)
            {
                var pcode = Norm(r.process_code);
                if (string.IsNullOrWhiteSpace(pcode)) continue;

                var orderQty = orderQtyMap.TryGetValue(r.order_id, out var q) ? q : 0;

                var area = r.total_area_m2 ?? 0m;
                var sheets = r.sheets_total ?? 0;

                var required = GetRequiredUnits(pcode, orderQty, area, sheets);
                if (required <= 0) continue;

                var areaPerSheet = (area > 0 && sheets > 0) ? (area / sheets) : 0m;

                logSum.TryGetValue(r.task_id, out var qtyGoodInt);
                var doneUnits = ConvertLogQtyToUnits(pcode, qtyGoodInt, areaPerSheet);

                var remaining = required - doneUnits;
                if (remaining < 0) remaining = 0;

                dict[pcode] = dict.TryGetValue(pcode, out var cur) ? (cur + remaining) : remaining;
            }

            return dict;
        }

        public async Task<EstimateFinishDateResponse?> EstimateFinishByOrderRequestAsync(int orderRequestId, CancellationToken ct = default)
        {
            var req = await _db.order_requests.AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            if (req == null) return null;

            var est = await _db.cost_estimates.AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            DateTime? desired = est?.desired_delivery_date ?? req.delivery_date;

            var ptCode = (req.product_type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ptCode)) return null;

            var ptId = await _db.product_types.AsNoTracking()
                .Where(x => x.code == ptCode)
                .Select(x => (int?)x.product_type_id)
                .FirstOrDefaultAsync(ct);

            if (ptId == null) return null;

            var steps = await _db.product_type_processes.AsNoTracking()
                .Where(x => x.product_type_id == ptId.Value && (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .Select(x => new { x.seq_num, x.process_name, x.process_code })
                .ToListAsync(ct);

            HashSet<string>? selected = null;
            if (!string.IsNullOrWhiteSpace(req.production_processes))
            {
                selected = req.production_processes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Norm(x))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet();
            }

            if (selected != null && selected.Count > 0)
            {
                steps = steps.Where(s => selected.Contains(Norm(s.process_code))).ToList();
            }

            var orderQty = req.quantity ?? 0;
            var totalAreaM2 = est?.total_area_m2 ?? 0m;
            var sheetsTotal = est?.sheets_total ?? 0;

            var now = AppTime.NowVnUnspecified();

            var dailyCap = await GetDailyCapacityByProcessCodeAsync(ct);
            var backlog = await GetBacklogUnitsByProcessCodeAsync(ct);

            var queueEnd = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in dailyCap)
            {
                var pcode = kv.Key;
                var cap = kv.Value;

                backlog.TryGetValue(pcode, out var bUnits);
                if (cap <= 0 || bUnits <= 0)
                {
                    queueEnd[pcode] = now;
                    continue;
                }

                var waitDays = (double)(bUnits / cap);
                queueEnd[pcode] = now.AddDays(waitDays);
            }

            var breakdown = new List<StepCapacityBreakdownDto>();
            var current = now;

            foreach (var s in steps)
            {
                var pcode = Norm(s.process_code);
                if (string.IsNullOrWhiteSpace(pcode)) continue;

                var unit = GetUnit(pcode);
                var requiredUnits = GetRequiredUnits(pcode, orderQty, totalAreaM2, sheetsTotal);
                if (requiredUnits <= 0) continue;

                var cap = dailyCap.TryGetValue(pcode, out var c) ? c : 0m;
                var backlogUnits = backlog.TryGetValue(pcode, out var b) ? b : 0m;

                var availableAt = queueEnd.TryGetValue(pcode, out var qe) ? qe : now;

                if (cap <= 0)
                {
                    var startAt = current > availableAt ? current : availableAt;
                    var finishAt = startAt.AddDays(2);

                    breakdown.Add(new StepCapacityBreakdownDto
                    {
                        process_code = pcode,
                        process_name = s.process_name ?? "",
                        unit = unit,
                        required_units = requiredUnits,
                        backlog_units = backlogUnits,
                        daily_capacity = 0m,
                        queue_available_at = availableAt,
                        wait_reason = "No machine capacity for this process_code. Using fallback +2 days.",
                        wait_days = 2m,
                        duration_days = 2m,
                        start_at = startAt,
                        finish_at = finishAt
                    });

                    current = finishAt;
                    queueEnd[pcode] = finishAt; // đẩy queue
                    continue;
                }

                var startAt2 = current > availableAt ? current : availableAt;

                var durationDays = requiredUnits / cap;
                var finishAt2 = startAt2.AddDays((double)durationDays);

                var waitDays2 = (decimal)Math.Max(0, (startAt2 - current).TotalDays);

                breakdown.Add(new StepCapacityBreakdownDto
                {
                    process_code = pcode,
                    process_name = s.process_name ?? "",
                    unit = unit,
                    required_units = requiredUnits,
                    backlog_units = backlogUnits,
                    daily_capacity = cap,
                    queue_available_at = availableAt,
                    wait_reason = availableAt > current
                        ? "Waiting for machine queue (other orders) to finish."
                        : "No queue wait; can start after previous step.",
                    wait_days = Math.Round(waitDays2, 3),
                    duration_days = Math.Round((decimal)durationDays, 3),
                    start_at = startAt2,
                    finish_at = finishAt2
                });

                current = finishAt2;
                queueEnd[pcode] = finishAt2;
            }

            var estimatedFinish = current;

            var canMeet = desired.HasValue
                ? estimatedFinish.Date <= desired.Value.Date
                : true;

            int late = 0, early = 0;
            if (desired.HasValue)
            {
                var diff = (estimatedFinish.Date - desired.Value.Date).Days;
                if (diff > 0) late = diff;
                if (diff < 0) early = -diff;
            }

            return new EstimateFinishDateResponse
            {
                order_request_id = orderRequestId,
                desired_delivery_date = desired,
                estimated_finish_date = estimatedFinish,
                can_meet_desired_date = canMeet,
                days_late_if_any = late,
                days_early_if_any = early,
                //steps = breakdown     //nếu cần thì mở cmt ra
            };
        }
    }
}
