using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Infrastructure.DBContext;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Helpers;

public sealed class ProductionDependencyCheckResult
{
    public bool can_start { get; set; } = true;

    public List<ProductionDependencyIssueDto> issues { get; set; } = new();

    public string message =>
        can_start
            ? "OK"
            : string.Join(" | ", issues.Select(x => x.message));
}

public sealed class ProductionDependencyIssueDto
{
    public int order_id { get; set; }

    public int current_task_id { get; set; }

    public string? current_process_code { get; set; }

    public string? previous_process_code { get; set; }

    public int? previous_task_id { get; set; }

    public string? previous_task_status { get; set; }

    public string message { get; set; } = "";
}

public static class ProductionDependencyValidator
{
    public static async Task<ProductionDependencyCheckResult> CheckProductionCanStartAsync(
        AppDbContext db,
        int prodId,
        CancellationToken ct = default)
    {
        var firstTask = await db.tasks
            .AsNoTracking()
            .Include(x => x.process)
            .Where(x => x.prod_id == prodId)
            .OrderBy(x => x.seq_num)
            .ThenBy(x => x.task_id)
            .FirstOrDefaultAsync(ct);

        if (firstTask == null)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        message = $"Production {prodId} chưa có task."
                    }
                }
            };
        }

        return await CheckTaskCanStartAsync(db, firstTask.task_id, ct);
    }

    public static async Task<ProductionDependencyCheckResult> CheckTaskCanStartAsync(
        AppDbContext db,
        int taskId,
        CancellationToken ct = default)
    {
        var currentTask = await db.tasks
            .AsNoTracking()
            .Include(x => x.process)
            .Include(x => x.prod)
            .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

        if (currentTask == null)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        current_task_id = taskId,
                        message = $"Task {taskId} không tồn tại."
                    }
                }
            };
        }

        if (!currentTask.prod_id.HasValue || currentTask.prod == null)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        current_task_id = taskId,
                        message = $"Task {taskId} chưa gắn production."
                    }
                }
            };
        }

        var currentProcessCode = Norm(currentTask.process?.process_code);

        if (string.IsNullOrWhiteSpace(currentProcessCode))
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        current_task_id = taskId,
                        message = $"Task {taskId} chưa có process_code."
                    }
                }
            };
        }

        var orderIds = await ResolveOrderIdsOfTaskAsync(db, currentTask.prod_id.Value, ct);

        var result = new ProductionDependencyCheckResult();

        foreach (var orderId in orderIds)
        {
            var route = await GetOrderRouteAsync(db, orderId, ct);

            if (route.Count == 0)
                continue;

            var previousCode = ResolvePreviousCode(route, currentProcessCode);

            if (string.IsNullOrWhiteSpace(previousCode))
                continue;

            var previous = await FindProcessTaskForOrderAsync(
                db,
                orderId,
                previousCode,
                ct);

            var previousFinished =
                previous != null &&
                IsFinished(previous.status, previous.end_time);

            if (!previousFinished)
            {
                result.can_start = false;

                result.issues.Add(new ProductionDependencyIssueDto
                {
                    order_id = orderId,
                    current_task_id = taskId,
                    current_process_code = currentProcessCode,
                    previous_process_code = previousCode,
                    previous_task_id = previous?.task_id,
                    previous_task_status = previous?.status,
                    message =
                        $"Order {orderId}: công đoạn {currentProcessCode} chưa được start/finish vì công đoạn trước đó {previousCode} chưa Finished. " +
                        $"previous_task_id={(previous?.task_id.ToString() ?? "null")}, status={(previous?.status ?? "null")}."
                });
            }
        }

        return result;
    }

    private static async Task<List<int>> ResolveOrderIdsOfTaskAsync(
        AppDbContext db,
        int prodId,
        CancellationToken ct)
    {
        var prod = await db.productions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

        if (prod == null)
            return new List<int>();

        if (string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
        {
            return await db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.prod_id == prodId &&
                    x.status == "Active")
                .Select(x => x.order_id)
                .Distinct()
                .ToListAsync(ct);
        }

        if (prod.order_id.HasValue)
            return new List<int> { prod.order_id.Value };

        return new List<int>();
    }

    private static async Task<List<string>> GetOrderRouteAsync(
        AppDbContext db,
        int orderId,
        CancellationToken ct)
    {
        var processCsv = await db.order_items
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .OrderBy(x => x.item_id)
            .Select(x => x.production_process)
            .FirstOrDefaultAsync(ct);

        return ParseCodes(processCsv);
    }

    private static string? ResolvePreviousCode(
        List<string> route,
        string currentCode)
    {
        var idx = route.FindIndex(x =>
            string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));

        if (idx <= 0)
            return null;

        return route[idx - 1];
    }

    private sealed class ProcessTaskRef
    {
        public int task_id { get; set; }

        public int? prod_id { get; set; }

        public string? prod_kind { get; set; }

        public string? process_code { get; set; }

        public string? status { get; set; }

        public DateTime? end_time { get; set; }
    }

    private static async Task<ProcessTaskRef?> FindProcessTaskForOrderAsync(
        AppDbContext db,
        int orderId,
        string processCode,
        CancellationToken ct)
    {
        var directTasks = await (
            from t in db.tasks.AsNoTracking()
            join p in db.productions.AsNoTracking()
                on t.prod_id equals p.prod_id
            join pp in db.product_type_processes.AsNoTracking()
                on t.process_id equals pp.process_id into ppj
            from pp in ppj.DefaultIfEmpty()
            where p.order_id == orderId
            select new ProcessTaskRef
            {
                task_id = t.task_id,
                prod_id = t.prod_id,
                prod_kind = p.prod_kind,
                process_code = pp != null ? pp.process_code : null,
                status = t.status,
                end_time = t.end_time
            }
        ).ToListAsync(ct);

        var matched = directTasks
            .Where(x => string.Equals(
                Norm(x.process_code),
                Norm(processCode),
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => IsFinished(x.status, x.end_time))
            .ThenByDescending(x => x.task_id)
            .FirstOrDefault();

        if (matched != null)
            return matched;

        var qtyRows = await db.task_qtys
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .ToListAsync(ct);

        var hasGroupQty = qtyRows.Any(x =>
            string.Equals(
                Norm(x.process_code),
                Norm(processCode),
                StringComparison.OrdinalIgnoreCase) &&
            x.qty_good > 0);

        if (hasGroupQty)
        {
            return new ProcessTaskRef
            {
                task_id = 0,
                prod_id = null,
                prod_kind = "GROUP_QTY",
                process_code = processCode,
                status = "Finished",
                end_time = AppTime.NowVnUnspecified()
            };
        }

        return null;
    }

    private static bool IsFinished(string? status, DateTime? endTime)
    {
        return string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
               || endTime != null;
    }

    private static List<string> ParseCodes(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new List<string>();

        return csv
            .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Norm(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }
}