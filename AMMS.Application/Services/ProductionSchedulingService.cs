using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class ProductionSchedulingService : IProductionSchedulingService
    {
        private readonly AppDbContext _db;
        private readonly IProductionRepository _prodRepo;
        private readonly IProductTypeProcessRepository _ptpRepo;
        private readonly IMachineRepository _machineRepo;
        private readonly ITaskRepository _taskRepo;

        public ProductionSchedulingService(
            AppDbContext db,
            IProductionRepository prodRepo,
            IProductTypeProcessRepository ptpRepo,
            IMachineRepository machineRepo,
            ITaskRepository taskRepo)
        {
            _db = db;
            _prodRepo = prodRepo;
            _ptpRepo = ptpRepo;
            _machineRepo = machineRepo;
            _taskRepo = taskRepo;
        }

        public async Task<int> ScheduleOrderAsync(
    int orderId,
    int productTypeId,
    string? productionProcessCsv,
    int? managerId = 3)
        {
            var selected = ParseProcessCsv(productionProcessCsv);

            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                var now = AppTime.NowVnUnspecified();

                var order = await _db.orders
                    .AsTracking()
                    .FirstOrDefaultAsync(o => o.order_id == orderId);

                if (order == null)
                    throw new Exception($"Order {orderId} not found");

                production? prod = null;

                if (order.production_id.HasValue)
                {
                    prod = await _db.productions
                        .AsTracking()
                        .FirstOrDefaultAsync(p =>
                            p.prod_id == order.production_id.Value &&
                            p.end_date == null);
                }

                if (prod == null)
                {
                    prod = await _db.productions
                        .AsTracking()
                        .Where(p => p.order_id == orderId && p.end_date == null)
                        .OrderByDescending(p => p.prod_id)
                        .FirstOrDefaultAsync();
                }

                if (prod == null)
                {
                    prod = new production
                    {
                        code = "TMP-PROD",
                        order_id = orderId,
                        manager_id = managerId,
                        status = "Scheduled",
                        product_type_id = productTypeId,
                        start_date = AppTime.NowVnUnspecified()
                    };

                    await _db.productions.AddAsync(prod);
                    await _db.SaveChangesAsync();

                    prod.code = $"PROD-{prod.prod_id:00000}";
                    await _db.SaveChangesAsync();
                }

                if (order.production_id != prod.prod_id)
                {
                    order.production_id = prod.prod_id;
                    await _db.SaveChangesAsync();
                }

                var hasTask = await _db.tasks
                    .AsNoTracking()
                    .AnyAsync(t => t.prod_id == prod.prod_id);

                if (hasTask)
                {
                    await tx.CommitAsync();
                    return prod.prod_id;
                }

                var steps = await _ptpRepo.GetActiveByProductTypeIdAsync(productTypeId);
                if (steps == null || steps.Count == 0)
                    throw new Exception("No routing found");

                var firstSeq = steps.Min(x => x.seq_num);
                var tasks = new List<task>();

                foreach (var s in steps.OrderBy(x => x.seq_num))
                {
                    tasks.Add(new task
                    {
                        prod_id = prod.prod_id,
                        process_id = s.process_id,
                        seq_num = s.seq_num,
                        name = s.process_name,
                        status = s.seq_num == firstSeq ? "Ready" : "Unassigned",
                        start_time = s.seq_num == firstSeq ? now : null
                    });
                }

                await _taskRepo.AddRangeAsync(tasks);
                await _taskRepo.SaveChangesAsync();

                await tx.CommitAsync();
                return prod.prod_id;
            });
        }
      
        private static HashSet<string> ParseProcessCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
