using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class TaskRepository : ITaskRepository
    {
        private readonly AppDbContext _db;
        public TaskRepository(AppDbContext db) => _db = db;

        public Task AddRangeAsync(IEnumerable<task> tasks)
        {
            _db.tasks.AddRange(tasks);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync() => _db.SaveChangesAsync();

        public Task<task?> GetByIdAsync(int taskId)
            => _db.tasks.FirstOrDefaultAsync(x => x.task_id == taskId);

        public Task<task?> GetNextTaskAsync(int prodId, int currentSeqNum)
            => _db.tasks
                .Where(x => x.prod_id == prodId && x.seq_num > currentSeqNum)
                .OrderBy(x => x.seq_num)
                .FirstOrDefaultAsync();
        public Task<task?> GetPrevTaskAsync(int prodId, int seqNum)
        {
            return _db.tasks
                .Where(x => x.prod_id == prodId && x.seq_num < seqNum)
                .OrderByDescending(x => x.seq_num)
                .FirstOrDefaultAsync();
        }
        public async Task<int> SuggestQtyGoodAsync(int taskId, CancellationToken ct = default)
        {
            var row = await (
                from t in _db.tasks.AsNoTracking()
                join pr in _db.productions.AsNoTracking() on t.prod_id equals pr.prod_id
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id
                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()
                join req in _db.order_requests.AsNoTracking() on q.order_request_id equals req.order_request_id into rj
                from req in rj.DefaultIfEmpty()
                join ce in _db.cost_estimates.AsNoTracking() on req.order_request_id equals ce.order_request_id into cej
                from ce in cej.DefaultIfEmpty()
                join ptp in _db.product_type_processes.AsNoTracking() on t.process_id equals ptp.process_id into ptpj
                from ptp in ptpj.DefaultIfEmpty()
                where t.task_id == taskId
                select new
                {
                    process_code = ptp.process_code,
                    order_qty = (int?)req.quantity,
                    sheets_total = (int?)ce.sheets_total,
                    sheets_required = (int?)ce.sheets_required,
                    n_up = (int?)ce.n_up,
                }
            ).FirstOrDefaultAsync(ct);

            if (row == null)
                return 1;

            var pcode = (row.process_code ?? "").Trim().ToUpperInvariant();

            var orderQty = row.order_qty ?? 0;
            var sheetsTotal = row.sheets_total ?? 0;
            var sheetsRequired = row.sheets_required ?? 0;

            return SafeInt(
                sheetsTotal,
                fallback: SafeInt(sheetsRequired, fallback: SafeInt(orderQty, 1))
            );
        }
        private static int SafeInt(int v, int fallback = 1) => v > 0 ? v : fallback;
    }
}
