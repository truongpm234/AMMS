using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Productions;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class ProductionRepository : IProductionRepository
    {
        private readonly AppDbContext _db;

        public ProductionRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<DateTime?> GetNearestDeliveryDateAsync()
        {
            return await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id
                where pr.start_date != null
                      && pr.end_date == null
                      && o.delivery_date != null
                orderby o.delivery_date
                select o.delivery_date
            ).FirstOrDefaultAsync();
        }

        public Task AddAsync(production p)
        {
            _db.productions.Add(p);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync() => _db.SaveChangesAsync();

        public Task<production?> GetByIdAsync(int prodId)
            => _db.productions.FirstOrDefaultAsync(x => x.prod_id == prodId);


        public async Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            // 1) Base rows: productions + orders + first item + customer name
            var baseRows = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join c in _db.customers.AsNoTracking() on q.customer_id equals c.customer_id into cj
                from c in cj.DefaultIfEmpty()

                where pr.start_date != null
                      && pr.order_id != null
                      && pr.end_date == null
                orderby pr.start_date descending, pr.prod_id descending
                select new BaseRow
                {
                    prod_id = pr.prod_id,
                    order_id = o.order_id,
                    code = o.code,
                    delivery_date = o.delivery_date,
                    product_type_id = pr.product_type_id,
                    status = pr.status,
                    customer_name =
                        o.customer != null
                            ? (o.customer.company_name ?? o.customer.contact_name ?? "")
                            : (c != null ? (c.company_name ?? c.contact_name ?? "") : ""),

                    first_item_product_name = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => i.product_name)
                        .FirstOrDefault(),

                    first_item_production_process = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => i.production_process)
                        .FirstOrDefault(),

                    first_item_quantity = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => (int?)i.quantity)
                        .FirstOrDefault()

                }
            )
            .Skip(skip)
            .Take(pageSize + 1)
            .ToListAsync(ct);

            var hasNext = baseRows.Count > pageSize;
            if (hasNext) baseRows.RemoveAt(baseRows.Count - 1);

            if (baseRows.Count == 0)
            {
                return new PagedResultLite<ProducingOrderCardDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = false,
                    Data = new List<ProducingOrderCardDto>()
                };
            }

            var prodIds = baseRows.Select(x => x.prod_id).ToList();

            // 2) Load tasks by prod_id (để biết đang ở seq nào)
            var taskRows = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id != null && prodIds.Contains(t.prod_id.Value))
                .Select(t => new TaskRow
                {
                    ProdId = t.prod_id!.Value,
                    SeqNum = t.seq_num,
                    StartTime = t.start_time,
                    EndTime = t.end_time
                })
                .ToListAsync(ct);

            var tasksByProd = taskRows
                .GroupBy(x => x.ProdId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SeqNum ?? int.MaxValue).ToList()
                );

            // 3) Load routing steps by product_type_id from product_type_process
            var productTypeIds = baseRows
                .Select(x => x.product_type_id)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToList();

            var stepRows = await _db.product_type_processes
                .AsNoTracking()
                .Where(p => productTypeIds.Contains(p.product_type_id) && (p.is_active ?? true))
                .Select(p => new StepRow
                {
                    ProductTypeId = p.product_type_id,
                    SeqNum = p.seq_num,
                    ProcessName = p.process_name,
                    ProcessCode = p.process_code
                }).ToListAsync(ct);

            var stepsByProductType = stepRows
                .GroupBy(x => x.ProductTypeId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SeqNum).ToList()
                );

            var result = new List<ProducingOrderCardDto>();

            foreach (var r in baseRows)
            {
                tasksByProd.TryGetValue(r.prod_id, out var tasks);
                tasks ??= new List<TaskRow>();

                var ptId = r.product_type_id ?? 0;

                stepsByProductType.TryGetValue(ptId, out var steps);

                steps ??= new List<StepRow>();

                HashSet<string>? selected = null;

                var csv = (r.first_item_production_process ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(csv))
                {
                    selected = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim().ToUpperInvariant())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet();
                }

                steps ??= new List<StepRow>();

                if (selected != null && selected.Count > 0)
                {
                    steps = steps
                        .Where(s =>
                        {
                            var code = (s.ProcessCode ?? "").Trim().ToUpperInvariant();
                            return !string.IsNullOrWhiteSpace(code) && selected.Contains(code);
                        })
                        .OrderBy(s => s.SeqNum)
                        .ToList();
                }

                var stages = steps
                    .Select(s => s.ProcessName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                // current_seq lấy từ tasks
                var currentSeq = GetCurrentSeq(tasks);

                string? currentStage = null;
                if (currentSeq.HasValue)
                {
                    currentStage = steps.FirstOrDefault(x => x.SeqNum == currentSeq.Value)?.ProcessName;
                }
                else if (tasks.Count > 0 && tasks.All(x => x.EndTime != null))
                {
                    // nếu done hết
                    currentStage = stages.LastOrDefault();
                }

                // progress chia đều theo số công đoạn
                var progress = ComputeProgressByStages(steps, currentSeq, tasks);

                result.Add(new ProducingOrderCardDto
                {
                    order_id = r.order_id,
                    code = r.code,
                    customer_name = r.customer_name ?? "",
                    product_name = r.first_item_product_name,
                    quantity = r.first_item_quantity ?? 0,
                    delivery_date = r.delivery_date,
                    progress_percent = progress,
                    current_stage = currentStage,
                    stages = stages,
                    status = r.status,
                    production_status = r.status
                });
            }

            return new PagedResultLite<ProducingOrderCardDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = result
            };
        }

        public async Task<ProductionProgressResponse> GetProgressAsync(int prodId)
        {
            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.status })
                .ToListAsync();

            var total = tasks.Count;
            if (total <= 0)
                return new ProductionProgressResponse { prod_id = prodId, total_steps = 0, finished_steps = 0, progress_percent = 0 };

            // task nào có log Finished
            var finishedTaskIds = await _db.task_logs
                .AsNoTracking()
                .Where(l => l.task_id != null
                    && tasks.Select(x => x.task_id).Contains(l.task_id.Value)
                    && l.action_type == "Finished")
                .Select(l => l.task_id!.Value)
                .Distinct()
                .ToListAsync();

            var finished = tasks.Count(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                && finishedTaskIds.Contains(t.task_id));

            var percent = Math.Round((finished * 100m) / total, 1);

            return new ProductionProgressResponse
            {
                prod_id = prodId,
                total_steps = total,
                finished_steps = finished,
                progress_percent = percent
            };
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            // 1) Header: production + order + customer + first item
            var header = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id into oj
                from o in oj.DefaultIfEmpty()

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join c in _db.customers.AsNoTracking() on q.customer_id equals c.customer_id into cj
                from c in cj.DefaultIfEmpty()

                    // ⭐ CHANGED: filter theo order_id thay vì prod_id
                where pr.order_id == orderId
                orderby pr.start_date ?? pr.end_date ?? o.order_date  // optional: chọn bản ghi mới nhất
                select new
                {
                    pr,
                    o,
                    customer_name =
                        o.customer != null
                            ? (o.customer.company_name ?? o.customer.contact_name ?? "")
                            : (c != null ? (c.company_name ?? c.contact_name ?? "") : ""),

                    first_item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.product_name,
                            i.quantity,
                            i.production_process,
                            i_length = (int?)EF.Property<int?>(i, "length_mm"),
                            i_width = (int?)EF.Property<int?>(i, "width_mm"),
                            i_height = (int?)EF.Property<int?>(i, "height_mm"),
                        })
                        .FirstOrDefault()
                }
            )
            .FirstOrDefaultAsync(ct);

            if (header == null) return null;

            var dto = new ProductionDetailDto
            {
                prod_id = header.pr.prod_id,
                production_code = header.pr.code,
                production_status = header.pr.status,
                start_date = header.pr.start_date,
                end_date = header.pr.end_date,

                order_id = header.o?.order_id,
                order_code = header.o?.code,
                delivery_date = header.o?.delivery_date,
                customer_name = header.customer_name ?? "",

                product_name = header.first_item?.product_name,
                quantity = header.first_item?.quantity ?? 0,

                length_mm = header.first_item?.i_length,
                width_mm = header.first_item?.i_width,
                height_mm = header.first_item?.i_height,
            };

            var prodId = header.pr.prod_id; // ⭐ Lấy lại prodId để dùng cho tasks/logs

            // 2) Load tasks + logs – ⭐ CHANGED: dùng prodId từ header
            var tasks = await _db.tasks.AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new
                {
                    t.task_id,
                    t.prod_id,
                    t.seq_num,
                    t.name,
                    t.status,
                    t.assigned_to,
                    t.machine,
                    t.start_time,
                    t.end_time,
                    t.process_id
                })
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .OrderBy(l => l.log_time)
                .Select(l => new TaskLogDto
                {
                    log_id = l.log_id,
                    task_id = l.task_id!.Value,
                    action_type = l.action_type,
                    qty_good = l.qty_good ?? 0,
                    qty_bad = l.qty_bad ?? 0,
                    operator_id = l.operator_id,
                    log_time = l.log_time,
                    scanner_id = l.scanner_id,
                    scanned_code = l.scanned_code
                }).ToListAsync(ct);

            // 3) Load product_type_process steps (giữ nguyên)
            var ptId = header.pr.product_type_id;
            List<ProductTypeProcessStepDto> steps = new();

            if (ptId.HasValue)
            {
                steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .OrderBy(p => p.seq_num)
                    .Select(p => new ProductTypeProcessStepDto
                    {
                        process_id = p.process_id,
                        seq_num = p.seq_num,
                        process_name = p.process_name,
                        process_code = EF.Property<string?>(p, "process_code"),
                        machine = p.machine
                    })
                    .ToListAsync(ct);
            }

            // 4) Filter theo order_item.production_process như cũ
            HashSet<string>? selected = null;
            var production_processes = header.first_item?.production_process;
            if (!string.IsNullOrWhiteSpace(production_processes))
            {
                selected = production_processes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().ToUpperInvariant())
                    .ToHashSet();
            }

            var stages = new List<ProductionStageDto>();

            foreach (var s in steps)
            {
                var pcode = (s.process_code ?? "").Trim().ToUpperInvariant();
                if (selected != null && selected.Count > 0 && !string.IsNullOrWhiteSpace(pcode))
                {
                    if (!selected.Contains(pcode)) continue;
                }

                var task = tasks.FirstOrDefault(t => t.process_id == s.process_id)
                           ?? tasks.FirstOrDefault(t => (t.seq_num ?? -1) == s.seq_num);

                var stageLogs = task == null
                    ? new List<TaskLogDto>()
                    : logsByTaskId(logs, task.task_id); // hàm helper cũ

                var qtyGood = stageLogs.Sum(x => x.qty_good);
                var qtyBad = stageLogs.Sum(x => x.qty_bad);
                var denom = qtyGood + qtyBad;
                var wastePct = denom <= 0 ? 0m : Math.Round((qtyBad * 100m) / denom, 2);

                stages.Add(new ProductionStageDto
                {
                    process_id = s.process_id,
                    seq_num = s.seq_num,
                    process_name = s.process_name ?? "",
                    process_code = s.process_code,
                    machine = s.machine,

                    task_id = task?.task_id,
                    task_name = task?.name,
                    status = task?.status,
                    assigned_to = task?.assigned_to,
                    start_time = task?.start_time,
                    end_time = task?.end_time,

                    qty_good = qtyGood,
                    qty_bad = qtyBad,
                    waste_percent = wastePct,
                    last_scan_time = stageLogs.Count == 0 ? null : stageLogs.Max(x => x.log_time),

                    logs = stageLogs
                });
            }

            dto.stages = stages;
            return dto;
        }

        public async Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default)
        {
            var prod = await _db.productions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null) return null;

            var tasks = await _db.tasks.AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.seq_num, t.process_id, t.name })
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .Select(l => new
                {
                    task_id = l.task_id!.Value,
                    qty_good = l.qty_good ?? 0,
                    qty_bad = l.qty_bad ?? 0,
                    log_time = l.log_time
                })
                .ToListAsync(ct);

            // step meta (process_code/name)
            var ptId = prod.product_type_id;
            var stepMeta = new Dictionary<int, (string? code, string name, int seq)>();

            if (ptId.HasValue)
            {
                var steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .Select(p => new
                    {
                        p.process_id,
                        p.process_name,
                        p.seq_num,
                        process_code = (string?)EF.Property<string?>(p, "process_code")
                    })
                    .ToListAsync(ct);

                stepMeta = steps.ToDictionary(
                    x => x.process_id,
                    x => (x.process_code, x.process_name ?? "", x.seq_num)
                );
            }

            var stageRows = new List<StageWasteDto>();

            foreach (var t in tasks.OrderBy(x => x.seq_num ?? int.MaxValue))
            {
                var tlogs = logs.Where(x => x.task_id == t.task_id).ToList();
                var good = tlogs.Sum(x => x.qty_good);
                var bad = tlogs.Sum(x => x.qty_bad);
                var denom = good + bad;
                var waste = denom <= 0 ? 0m : Math.Round((bad * 100m) / denom, 2);

                string pname = t.name;
                string? pcode = null;
                var seq = t.seq_num ?? 0;

                if (t.process_id.HasValue && stepMeta.TryGetValue(t.process_id.Value, out var meta))
                {
                    pname = meta.name;
                    pcode = meta.code;
                    seq = meta.seq;
                }

                stageRows.Add(new StageWasteDto
                {
                    task_id = t.task_id,
                    seq_num = seq,
                    process_name = pname,
                    process_code = pcode,
                    qty_good = good,
                    qty_bad = bad,
                    waste_percent = waste,
                    first_scan = tlogs.Count == 0 ? null : tlogs.Min(x => x.log_time),
                    last_scan = tlogs.Count == 0 ? null : tlogs.Max(x => x.log_time),
                });
            }

            var totalGood = stageRows.Sum(x => (decimal)x.qty_good);
            var totalBad = stageRows.Sum(x => (decimal)x.qty_bad);
            var totalDenom = totalGood + totalBad;
            var totalWastePct = totalDenom <= 0 ? 0m : Math.Round((totalBad * 100m) / totalDenom, 2);

            return new ProductionWasteReportDto
            {
                prod_id = prodId,
                total_good = totalGood,
                total_bad = totalBad,
                total_waste_percent = totalWastePct,
                stages = stageRows.OrderBy(x => x.seq_num).ToList()
            };
        }
        public async Task<bool> TryCloseProductionIfCompletedAsync(int prodId, DateTime now, CancellationToken ct = default)
        {
            var prod = await _db.productions.FirstOrDefaultAsync(p => p.prod_id == prodId, ct);
            if (prod == null) return false;

            var tasks = await _db.tasks
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.status, t.end_time })
                .ToListAsync(ct);

            if (tasks.Count == 0) return false;

            bool allFinished = tasks.All(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                || t.end_time != null);

            if (!allFinished) return false;

            // tránh set lại nhiều lần
            if (prod.end_date == null)
                prod.end_date = now;

            prod.status = "Finished";

            await _db.SaveChangesAsync(ct);
            return true;
        }

        static List<TaskLogDto> logsByTaskId(List<TaskLogDto> all, int taskId)
        {
            return all
                .Where(x => x.task_id == taskId)
                .OrderBy(x => x.log_time)
                .ToList();
        }
        private static int? GetCurrentSeq(List<TaskRow> tasks)
        {
            var inProg = tasks.FirstOrDefault(x => x.StartTime != null && x.EndTime == null);
            if (inProg?.SeqNum != null) return inProg.SeqNum;

            var next = tasks.FirstOrDefault(x => x.EndTime == null);
            if (next?.SeqNum != null) return next.SeqNum;

            return null;
        }

        private static decimal ComputeProgressByStages(
            List<StepRow> steps,
            int? currentSeq,
            List<TaskRow> tasks)
        {
            var total = steps.Count;
            if (total <= 0) return 0m;

            if (tasks.Count > 0 && tasks.All(x => x.EndTime != null))
                return 100m;

            if (!currentSeq.HasValue) return 0m;

            // tìm index của current stage trong steps
            var idx = steps.FindIndex(s => s.SeqNum == currentSeq.Value);
            if (idx < 0) idx = 0;

            var completedBefore = idx;

            var percent = completedBefore * 100m / total;
            return Math.Round(percent, 1);
        }
        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }
    }
}
