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

                    customer_name =
                        o.customer != null
                            ? (o.customer.company_name ?? o.customer.contact_name ?? "")
                            : (c != null ? (c.company_name ?? c.contact_name ?? "") : ""),

                    first_item_product_name = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => i.product_name)
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
                    ProcessName = p.process_name
                })
                .ToListAsync(ct);

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
                    stages = stages
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
        private static int? GetCurrentSeq(List<TaskRow> tasks)
        {
            // đang làm: start != null && end == null
            var inProg = tasks.FirstOrDefault(x => x.StartTime != null && x.EndTime == null);
            if (inProg?.SeqNum != null) return inProg.SeqNum;

            // chưa làm tới: end == null (chưa start hoặc chưa end)
            var next = tasks.FirstOrDefault(x => x.EndTime == null);
            if (next?.SeqNum != null) return next.SeqNum;

            // done hết
            return null;
        }

        private static decimal ComputeProgressByStages(
            List<StepRow> steps,
            int? currentSeq,
            List<TaskRow> tasks)
        {
            var total = steps.Count;
            if (total <= 0) return 0m;

            // nếu all task done => 100
            if (tasks.Count > 0 && tasks.All(x => x.EndTime != null))
                return 100m;

            if (!currentSeq.HasValue) return 0m;

            // tìm index của current stage trong steps
            var idx = steps.FindIndex(s => s.SeqNum == currentSeq.Value);
            if (idx < 0) idx = 0;

            // idx=0 (stage 1) => 0%
            // idx=1 (stage 2) => 1/total
            var completedBefore = idx;

            var percent = completedBefore * 100m / total;
            return Math.Round(percent, 1);
        }
    }
}
