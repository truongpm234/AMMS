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

        public async Task<int> ScheduleOrderAsync(int orderId, int productTypeId, string? productionProcessCsv, int? managerId = 3)
        {
            var selected = ParseProcessCsv(productionProcessCsv);
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                try
                {
                    var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

                    var prod = new production
                    {
                        code = $"PRD-{DateTime.UtcNow:yyyyMMddHHmmss}",
                        order_id = orderId,
                        manager_id = managerId,
                        status = "Scheduled",
                        product_type_id = productTypeId,
                        start_date = now,
                    };

                    await _prodRepo.AddAsync(prod);
                    await _prodRepo.SaveChangesAsync();

                    var steps = await _ptpRepo.GetActiveByProductTypeIdAsync(productTypeId);
                    if (steps == null || steps.Count == 0)
                        throw new Exception("No routing (product_type_process) found. Seed first.");

                    if (selected.Count > 0)
                    {
                        steps = steps
                            .Where(s => !string.IsNullOrWhiteSpace(s.process_code) && selected.Contains(s.process_code!))
                            .OrderBy(s => s.seq_num)
                            .ToList();

                        if (steps.Count == 0)
                            throw new Exception("Selected production_process does not match any product_type_process.process_code");
                    }

                    var tasks = new List<task>();
                    var firstSeq = steps.Min(x => x.seq_num);

                    foreach (var s in steps.OrderBy(x => x.seq_num))
                    {
                        machine? m = null;

                        // 🔥 tự detect: nếu s.machine là machine_code thì lấy theo code
                        if (!string.IsNullOrWhiteSpace(s.machine))
                        {
                            m = await _machineRepo.GetByMachineCodeAsync(s.machine);
                            if (m == null)
                                m = await _machineRepo.FindMachineByProcess(s.machine); // coi như process_name
                        }

                        var manual = IsManual(m, s.machine);

                        var status = s.seq_num == firstSeq ? "Ready" : "Unassigned";

                        // ✅ chỉ set machine_code khi là máy thật
                        var taskMachine = manual ? null : m!.machine_code;

                        tasks.Add(new task
                        {
                            prod_id = prod.prod_id,
                            process_id = s.process_id,
                            seq_num = s.seq_num,
                            name = s.process_name,
                            status = status,
                            machine = taskMachine,
                            start_time = status == "Ready" ? now : null,
                            end_time = null
                        });
                    }

                    await _taskRepo.AddRangeAsync(tasks);
                    await _taskRepo.SaveChangesAsync();

                    // ✅ allocate machine cho task Ready đầu tiên
                    var firstTask = tasks.FirstOrDefault(x => x.status == "Ready" && !string.IsNullOrWhiteSpace(x.machine));
                    if (firstTask != null)
                    {
                        await _machineRepo.AllocateAsync(firstTask.machine!, need: 1);
                    }

                    await tx.CommitAsync();
                    return prod.prod_id;
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            });
        }

        private static bool IsManual(machine? m, string? stepMachine)
        {
            if (string.IsNullOrWhiteSpace(stepMachine)) return true;
            if (m == null) return true;

            var note = (m.note ?? "");
            return note.Contains("thủ công", StringComparison.OrdinalIgnoreCase)
                || note.Contains("thu cong", StringComparison.OrdinalIgnoreCase);
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
