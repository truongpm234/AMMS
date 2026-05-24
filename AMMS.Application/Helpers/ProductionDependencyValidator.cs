using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
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
    private static readonly HashSet<string> SubHeadCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RALO",
        "CAT",
        "IN"
    };

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

        if (!currentTask.prod_id.HasValue)
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

        var currentProd = await db.productions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.prod_id == currentTask.prod_id.Value, ct);

        if (currentProd == null)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        current_task_id = taskId,
                        message = $"Không tìm thấy production của task {taskId}."
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

        /*
         * 1. Check riêng cho case SUB/BOTH:
         * Production sau chỉ được start khi production đầu RALO,CAT,IN đã Importing.
         *
         * FIX quan trọng:
         * Không được nhận diện production đầu SUB bằng prod_method = SUB/BOTH.
         * Vì production SPLIT BE,DUT,DAN cũng kế thừa prod_method = SUB/BOTH.
         * Chỉ nhận diện bằng actual path task của production đó.
         */
        var subHeadGate = await CheckSubHeadProductionMustBeImportingAsync(
            db,
            currentProd,
            currentTask.task_id,
            ct);

        if (!subHeadGate.can_start)
            return subHeadGate;

        /*
         * 2. Check pipeline nội bộ trong cùng production.
         * Ví dụ:
         * - GROUP PHU,CAN: CAN chỉ được start khi PHU Finished.
         * - SPLIT BE,DUT,DAN: DUT chỉ được start khi BE Finished.
         * - SINGLE RALO,CAT,IN: CAT chỉ được start khi RALO Finished.
         */
        var internalPipeline = await CheckInternalProductionPipelineAsync(
            db,
            currentTask,
            ct);

        if (!internalPipeline.ok)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = false,
                issues = new List<ProductionDependencyIssueDto>
                {
                    new()
                    {
                        current_task_id = taskId,
                        current_process_code = currentProcessCode,
                        message = internalPipeline.message
                    }
                }
            };
        }

        /*
         * 3. Check pipeline logic theo order path.
         * Đây là phần nối giữa các production:
         * - GROUP PHU phải đợi SINGLE IN của từng order.
         * - SPLIT BE phải đợi GROUP CAN.
         *
         * Nhưng không được check bừa theo order_id rồi lấy nhầm production sau.
         * Phải tìm đúng previous process code theo order path.
         */
        var orderIds = await ResolveOrderIdsOfProductionAsync(
            db,
            currentProd.prod_id,
            ct);

        if (orderIds.Count == 0)
            return new ProductionDependencyCheckResult { can_start = true };

        var result = new ProductionDependencyCheckResult
        {
            can_start = true
        };

        foreach (var orderId in orderIds)
        {
            var route = await GetOrderRouteAsync(db, orderId, ct);

            if (route.Count == 0)
                continue;

            var previousCode = ResolvePreviousCode(route, currentProcessCode);

            if (string.IsNullOrWhiteSpace(previousCode))
                continue;

            var previous = await FindPreviousStageForOrderAsync(
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
                        $"Order {orderId}: công đoạn {currentProcessCode} chưa được bắt đầu vì công đoạn trước đó {previousCode} chưa Finished."
                });
            }
        }

        return result;
    }

    private static async Task<ProductionDependencyCheckResult> CheckSubHeadProductionMustBeImportingAsync(
        AppDbContext db,
        production currentProd,
        int currentTaskId,
        CancellationToken ct)
    {
        var currentCodes = await GetProductionProcessCodesAsync(
            db,
            currentProd.prod_id,
            ct);

        /*
         * Nếu chính production hiện tại là production đầu RALO,CAT,IN
         * thì không tự chặn chính nó.
         */
        if (IsSubHeadProductionByCodes(currentCodes))
        {
            return new ProductionDependencyCheckResult
            {
                can_start = true
            };
        }

        /*
         * Chỉ áp dụng rule này cho SUB/BOTH.
         * NVL full process thì không cần bắt buộc production đầu SUB Importing.
         */
        var currentMethod = Norm(currentProd.prod_method);

        if (currentMethod != "SUB" && currentMethod != "BOTH")
        {
            return new ProductionDependencyCheckResult
            {
                can_start = true
            };
        }

        var orderIds = await ResolveOrderIdsOfProductionAsync(
            db,
            currentProd.prod_id,
            ct);

        if (orderIds.Count == 0)
        {
            return new ProductionDependencyCheckResult
            {
                can_start = true
            };
        }

        var result = new ProductionDependencyCheckResult
        {
            can_start = true
        };

        foreach (var orderId in orderIds)
        {
            /*
             * FIX chính:
             * Chỉ tìm production đầu bằng actual task path RALO/CAT/IN.
             * Không dùng prod_method = SUB/BOTH để nhận diện.
             */
            var headProductions = await FindSubHeadProductionsByOrderAsync(
                db,
                orderId,
                excludeProdId: currentProd.prod_id,
                ct);

            if (headProductions.Count == 0)
            {
                result.can_start = false;
                result.issues.Add(new ProductionDependencyIssueDto
                {
                    order_id = orderId,
                    current_task_id = currentTaskId,
                    message =
                        $"Order {orderId}: không tìm thấy production đầu RALO,CAT,IN để xác nhận đầu vào SUB."
                });

                continue;
            }

            foreach (var head in headProductions)
            {
                var allTasksFinished = await AreAllProductionTasksFinishedAsync(
                    db,
                    head.prod_id,
                    ct);

                if (!allTasksFinished)
                {
                    result.can_start = false;
                    result.issues.Add(new ProductionDependencyIssueDto
                    {
                        order_id = orderId,
                        current_task_id = currentTaskId,
                        message =
                            $"Order {orderId}: production đầu RALO,CAT,IN chưa hoàn thành task. " +
                            $"prod_id={head.prod_id}, status={head.status}."
                    });

                    continue;
                }

                if (!string.Equals(head.status, "Importing", StringComparison.OrdinalIgnoreCase))
                {
                    result.can_start = false;
                    result.issues.Add(new ProductionDependencyIssueDto
                    {
                        order_id = orderId,
                        current_task_id = currentTaskId,
                        message =
                            $"Order {orderId}: production đầu RALO,CAT,IN đã Finished task nhưng chưa chuyển Importing. " +
                            $"Vui lòng gọi API PUT /api/Productions/mark-importing/{head.prod_id} trước khi start production sau. " +
                            $"Current prod_id={head.prod_id}, status={head.status}."
                    });
                }
            }
        }

        return result;
    }

    private sealed class SubHeadProductionRef
    {
        public int prod_id { get; init; }

        public string? code { get; init; }

        public string? status { get; init; }
    }

    private static async Task<List<SubHeadProductionRef>> FindSubHeadProductionsByOrderAsync(
        AppDbContext db,
        int orderId,
        int excludeProdId,
        CancellationToken ct)
    {
        var candidates = await db.productions
            .AsNoTracking()
            .Where(x =>
                x.order_id == orderId &&
                x.prod_id != excludeProdId &&
                (
                    x.status == null ||
                    x.status.ToUpper() != "CANCELLED"
                ))
            .Select(x => new
            {
                x.prod_id,
                x.code,
                x.status
            })
            .ToListAsync(ct);

        var result = new List<SubHeadProductionRef>();

        foreach (var p in candidates)
        {
            var codes = await GetProductionProcessCodesAsync(
                db,
                p.prod_id,
                ct);

            /*
             * Chỉ nhận production có actual task path thuộc RALO,CAT,IN.
             * Như vậy SPLIT BE,DUT,DAN sẽ không bị nhận nhầm dù prod_method = SUB.
             */
            if (!IsSubHeadProductionByCodes(codes))
                continue;

            result.Add(new SubHeadProductionRef
            {
                prod_id = p.prod_id,
                code = p.code,
                status = p.status
            });
        }

        return result;
    }

    private static async Task<(bool ok, string message)> CheckInternalProductionPipelineAsync(
        AppDbContext db,
        task currentTask,
        CancellationToken ct)
    {
        if (!currentTask.prod_id.HasValue)
            return (false, "Task thiếu prod_id.");

        if (!currentTask.seq_num.HasValue)
            return (false, "Task thiếu seq_num.");

        var currentProdId = currentTask.prod_id.Value;
        var currentSeq = currentTask.seq_num.Value;

        var previousUnfinishedTasks = await db.tasks
            .AsNoTracking()
            .Include(x => x.process)
            .Where(x =>
                x.prod_id == currentProdId &&
                x.task_id != currentTask.task_id &&
                x.seq_num.HasValue &&
                x.seq_num.Value < currentSeq &&
                (
                    x.status == null ||
                    x.status.ToUpper() != "FINISHED"
                ))
            .OrderBy(x => x.seq_num)
            .ThenBy(x => x.task_id)
            .ToListAsync(ct);

        if (previousUnfinishedTasks.Count == 0)
            return (true, "");

        var first = previousUnfinishedTasks.First();

        return (false,
            $"Công đoạn trước trong cùng production chưa Finished: " +
            $"{first.process?.process_code ?? first.name}, " +
            $"task_id={first.task_id}, status={first.status}");
    }

    private sealed class PreviousStageTaskRef
    {
        public int? task_id { get; init; }

        public int? prod_id { get; init; }

        public string? prod_kind { get; init; }

        public string? process_code { get; init; }

        public string? status { get; init; }

        public DateTime? end_time { get; init; }

        public DateTime? created_at { get; init; }
    }

    private static async Task<PreviousStageTaskRef?> FindPreviousStageForOrderAsync(
        AppDbContext db,
        int orderId,
        string previousProcessCode,
        CancellationToken ct)
    {
        var previousCode = Norm(previousProcessCode);

        /*
         * 1. Tìm task trực tiếp trong production có order_id.
         * Ví dụ:
         * - SINGLE RALO,CAT,IN của order.
         * - SPLIT BE,DUT,DAN của order.
         */
        var directTasks = await (
            from t in db.tasks.AsNoTracking()

            join p in db.productions.AsNoTracking()
                on t.prod_id equals p.prod_id

            join pp0 in db.product_type_processes.AsNoTracking()
                on t.process_id equals pp0.process_id into ppj
            from pp in ppj.DefaultIfEmpty()

            where p.order_id == orderId
                  && (
                        p.status == null ||
                        p.status.ToUpper() != "CANCELLED"
                     )

            select new PreviousStageTaskRef
            {
                task_id = t.task_id,
                prod_id = p.prod_id,
                prod_kind = p.prod_kind,
                process_code = pp != null ? pp.process_code : null,
                status = t.status,
                end_time = t.end_time,
                created_at = p.created_at
            }
        ).ToListAsync(ct);

        var matchedDirect = PickBestPreviousStage(
            directTasks,
            previousCode);

        if (matchedDirect != null)
            return matchedDirect;

        /*
         * 2. Tìm task trong GROUP thông qua task_links.
         * Vì GROUP không có order_id trực tiếp trong bảng productions,
         * nên bắt buộc phải đi qua task_links/order_id.
         *
         * Đây là phần giúp SPLIT BE tìm được CAN của GROUP PHU,CAN.
         */
        var linkedGroupTasks = await (
            from tl in db.task_links.AsNoTracking()

            join gt in db.tasks.AsNoTracking()
                on tl.group_task_id equals gt.task_id

            join gp in db.productions.AsNoTracking()
                on tl.group_prod_id equals gp.prod_id

            join pp0 in db.product_type_processes.AsNoTracking()
                on gt.process_id equals pp0.process_id into ppj
            from pp in ppj.DefaultIfEmpty()

            where tl.order_id == orderId
                  && (
                        tl.status == null ||
                        tl.status.ToUpper() != "CANCELLED"
                     )
                  && (
                        gp.status == null ||
                        gp.status.ToUpper() != "CANCELLED"
                     )

            select new PreviousStageTaskRef
            {
                task_id = gt.task_id,
                prod_id = gp.prod_id,
                prod_kind = gp.prod_kind,
                process_code = pp != null ? pp.process_code : tl.process_code,
                status = gt.status,
                end_time = gt.end_time,
                created_at = gp.created_at
            }
        ).ToListAsync(ct);

        var matchedGroup = PickBestPreviousStage(
            linkedGroupTasks,
            previousCode);

        if (matchedGroup != null)
            return matchedGroup;

        /*
         * 3. Fallback theo task_qtys.
         * Dùng khi group task đã finish và có allocation output cho từng order.
         */
        var qtyRows = await db.task_qtys
            .AsNoTracking()
            .Where(x => x.order_id == orderId)
            .Select(x => new PreviousStageTaskRef
            {
                task_id = x.group_task_id,
                prod_id = null,
                prod_kind = "GROUP_QTY",
                process_code = x.process_code,
                status = "Finished",
                end_time = x.created_at,
                created_at = x.created_at
            })
            .ToListAsync(ct);

        return PickBestPreviousStage(
            qtyRows,
            previousCode);
    }

    private static PreviousStageTaskRef? PickBestPreviousStage(
        List<PreviousStageTaskRef> rows,
        string processCode)
    {
        var code = Norm(processCode);

        return rows
            .Where(x => string.Equals(
                Norm(x.process_code),
                code,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => IsFinished(x.status, x.end_time))
            .ThenByDescending(x => x.end_time ?? x.created_at ?? DateTime.MinValue)
            .ThenByDescending(x => x.task_id ?? 0)
            .FirstOrDefault();
    }

    private static async Task<List<int>> ResolveOrderIdsOfProductionAsync(
        AppDbContext db,
        int prodId,
        CancellationToken ct)
    {
        var prod = await db.productions
            .AsNoTracking()
            .Where(x => x.prod_id == prodId)
            .Select(x => new
            {
                x.prod_id,
                x.order_id,
                x.prod_kind
            })
            .FirstOrDefaultAsync(ct);

        if (prod == null)
            return new List<int>();

        if (prod.order_id.HasValue && prod.order_id.Value > 0)
            return new List<int> { prod.order_id.Value };

        /*
         * GROUP không có order_id.
         * Lấy member order từ prod_orders.
         */
        return await db.prod_orders
            .AsNoTracking()
            .Where(x =>
                x.prod_id == prodId &&
                x.status == "Active")
            .Select(x => x.order_id)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
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

    private static async Task<List<string>> GetProductionProcessCodesAsync(
        AppDbContext db,
        int prodId,
        CancellationToken ct)
    {
        var rows = await (
            from t in db.tasks.AsNoTracking()

            join pp0 in db.product_type_processes.AsNoTracking()
                on t.process_id equals pp0.process_id into ppj
            from pp in ppj.DefaultIfEmpty()

            where t.prod_id == prodId

            orderby t.seq_num, t.task_id

            select pp != null ? pp.process_code : null
        ).ToListAsync(ct);

        return rows
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSubHeadProductionByCodes(List<string> codes)
    {
        if (codes == null || codes.Count == 0)
            return false;

        /*
         * Production đầu SUB có path chỉ nằm trong RALO,CAT,IN.
         * Ví dụ hợp lệ:
         * - RALO,CAT,IN
         * - RALO,IN
         * - IN
         *
         * Không hợp lệ:
         * - BE,DUT,DAN
         * - PHU,CAN
         */
        return codes.All(x => SubHeadCodes.Contains(Norm(x)));
    }

    private static async Task<bool> AreAllProductionTasksFinishedAsync(
        AppDbContext db,
        int prodId,
        CancellationToken ct)
    {
        var tasks = await db.tasks
            .AsNoTracking()
            .Where(x => x.prod_id == prodId)
            .Select(x => new
            {
                x.status,
                x.end_time
            })
            .ToListAsync(ct);

        if (tasks.Count == 0)
            return false;

        return tasks.All(x =>
            string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
            x.end_time != null);
    }

    private static string? ResolvePreviousCode(
        List<string> route,
        string currentCode)
    {
        if (route == null || route.Count == 0)
            return null;

        var current = Norm(currentCode);

        var index = route.FindIndex(x =>
            string.Equals(
                Norm(x),
                current,
                StringComparison.OrdinalIgnoreCase));

        if (index <= 0)
            return null;

        return route[index - 1];
    }

    private static List<string> ParseCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw
            .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Norm)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Norm(string? code)
    {
        return (code ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    private static bool IsFinished(string? status, DateTime? endTime)
    {
        return string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
               || endTime != null;
    }
}