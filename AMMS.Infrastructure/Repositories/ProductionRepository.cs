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

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        /// <summary>
        /// Ngày giao gần nhất của các đơn đang sản xuất
        /// </summary>
        public async Task<DateTime?> GetNearestDeliveryDateAsync()
        {
            // Đơn đang sản xuất: start_date != null && end_date == null
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

            // 1) Base: lấy các production đang sản xuất
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            var baseRows = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join c in _db.customers.AsNoTracking() on q.customer_id equals c.customer_id into cj
                from c in cj.DefaultIfEmpty()

                where pr.start_date != null
                      && pr.end_date == null
                orderby pr.start_date descending, pr.prod_id descending
                select new
                {
                    pr.prod_id,
                    pr.order_id,
                    pr.product_type_id,
                    pr.start_date,
                    pr.end_date,
                    pr.status,
                    o.code,
                    o.delivery_date,

                    CustomerName =
                        o.customer != null
                            ? (o.customer.company_name ?? o.customer.contact_name ?? "")
                            : (c != null
                                ? (c.company_name ?? c.contact_name ?? "")
                                : ""),

                    FirstItem = _db.order_items
                        .AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new { i.product_name, i.quantity })
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
            var productTypeIds = baseRows
                .Where(x => x.product_type_id != null)
                .Select(x => x.product_type_id!.Value)
                .Distinct()
                .ToList();

            // 2) Load routing chuẩn theo product_type_id (product_type_process)
            // NOTE: đổi _db.product_type_processes thành DbSet thật của bạn
            var routeRows = await _db.product_type_processes
                .AsNoTracking()
                .Where(p => productTypeIds.Contains(p.product_type_id) && (p.is_active == null || p.is_active == true))
                .OrderBy(p => p.product_type_id)
                .ThenBy(p => p.seq_num)
                .Select(p => new RouteRow
                {
                    ProductTypeId = p.product_type_id,
                    SeqNum = p.seq_num,
                    ProcessName = p.process_name
                })
                .ToListAsync(ct);

            var routeByProductType = routeRows
                .GroupBy(x => x.ProductTypeId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SeqNum).ToList()
                );

            // 3) Load tasks để tính progress/current_stage (nhưng lấy process_name từ product_type_process cho sạch)
            // NOTE: join theo process_id (tasks.process_id -> product_type_process.process_id)
            var taskRows = await (
                from t in _db.tasks.AsNoTracking()
                join p in _db.product_type_processes.AsNoTracking() on t.process_id equals p.process_id into pj
                from p in pj.DefaultIfEmpty()
                where t.prod_id != null && prodIds.Contains(t.prod_id.Value)
                select new TaskRow
                {
                    ProdId = t.prod_id!.Value,
                    SeqNum = t.seq_num,
                    ProcessName = p != null && !string.IsNullOrWhiteSpace(p.process_name)
                        ? p.process_name
                        : t.name, // fallback nếu thiếu join
                    StartTime = t.start_time,
                    EndTime = t.end_time
                }
            ).ToListAsync(ct);

            var tasksByProd = taskRows
                .GroupBy(x => x.ProdId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SeqNum ?? int.MaxValue).ToList()
                );

            // 4) Build DTO
            var result = new List<ProducingOrderCardDto>();

            foreach (var r in baseRows)
            {
                string ComputeStatus(DateTime? start, DateTime? end, DateTime nowUtcUnspec)
                {
                    if (start == null) return "Scheduled";

                    // chưa tới lịch
                    if (nowUtcUnspec < start.Value) return "Scheduled";

                    // đang trong lịch
                    if (end == null) return "InProcessing";                 
                    if (nowUtcUnspec <= end.Value) return "InProcessing";   

                    return "Completed";
                }
                var ptid = r.product_type_id ?? 0;

                // stages theo routing chuẩn
                routeByProductType.TryGetValue(ptid, out var routeSteps);
                routeSteps ??= new List<RouteRow>();

                // tasks để tính progress/current
                tasksByProd.TryGetValue(r.prod_id, out var prodTasks);
                prodTasks ??= new List<TaskRow>();

                // stages: ưu tiên routing; nếu routing trống thì fallback theo tasks
                var stages = routeSteps.Count > 0
                    ? routeSteps.Select(x => x.ProcessName).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : prodTasks.Select(x => x.ProcessName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

                // progress: done/total theo routing (ổn định theo product_type_id)
                var total = routeSteps.Count;

                // done: count step đã EndTime != null (distinct theo seq để tránh double)
                var done = prodTasks
                    .Where(x => x.EndTime != null)
                    .Select(x => x.SeqNum ?? -1)
                    .Distinct()
                    .Count(x => x != -1);

                decimal progress = 0m;
                if (total > 0)
                    progress = Math.Round(done * 100m / total, 0);

                // current stage: task đang làm (Start != null && End == null) theo SeqNum
                string? currentStage = prodTasks
                    .OrderBy(x => x.SeqNum ?? int.MaxValue)
                    .FirstOrDefault(x => x.StartTime != null && x.EndTime == null)?.ProcessName;

                // fallback: task chưa start nhưng End == null (Ready/Unassigned)
                currentStage ??= prodTasks
                    .OrderBy(x => x.SeqNum ?? int.MaxValue)
                    .FirstOrDefault(x => x.EndTime == null)?.ProcessName;

                // fallback: nếu chưa có task -> stage đầu tiên theo routing
                if (string.IsNullOrWhiteSpace(currentStage) && routeSteps.Count > 0)
                    currentStage = routeSteps.OrderBy(x => x.SeqNum).First().ProcessName;

                var displayStatus = ComputeStatus(r.start_date, r.end_date, now);

                result.Add(new ProducingOrderCardDto
                {
                    order_id = (int)r.order_id,
                    code = r.code,
                    customer_name = r.CustomerName,
                    product_name = r.FirstItem?.product_name,
                    quantity = r.FirstItem?.quantity ?? 0,
                    delivery_date = r.delivery_date,

                    progress_percent = progress,
                    current_stage = currentStage,
                    stages = stages,

                    production_status = displayStatus
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
    }
}
