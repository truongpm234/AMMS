using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepo;

        public OrderService(IOrderRepository orderRepo)
        {
            _orderRepo = orderRepo;
        }

        public async Task<order> GetOrderByCodeAsync(string code)
        {
            var order = await _orderRepo.GetByCodeAsync(code);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public async Task<PagedResultLite<OrderResponseDto>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _orderRepo.GetPagedWithFulfillAsync(skip, pageSize + 1);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<OrderResponseDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }
        public async Task<order> GetByIdAsync(int id)
        {
            var order = await _orderRepo.GetByIdAsync(id);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public Task<OrderDetailDto?> GetDetailAsync(int id, CancellationToken ct = default)
            => _orderRepo.GetDetailByIdAsync(id, ct);

        public async Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var result = await _orderRepo.GetAllMissingMaterialsAsync(page, pageSize, ct);

            static decimal RoundUpToTens(decimal value)
            {
                if (value <= 0m) return 0m;
                return Math.Ceiling(value / 10m) * 10m; 
            }

            if (result.Data == null || result.Data.Count == 0)
                return result;

            foreach (var x in result.Data)
            {
                var missingBase = x.quantity;
                if (missingBase < 0m) missingBase = 0m;

                var withBuffer = missingBase * 1.10m;

                var rounded = RoundUpToTens(withBuffer);

                decimal unitPrice = 0m;
                if (missingBase > 0m && x.total_price > 0m)
                {
                    unitPrice = x.total_price / missingBase;
                }

                x.quantity = rounded;
                x.total_price = Math.Round(rounded * unitPrice, 2);
            }

            return result;
        }

        public Task<string> DeleteDesignFilePath(int orderRequestId)
        {
            return _orderRepo.DeleteDesignFilePath(orderRequestId);
        }

        public Task<List<order>> GetAllOrderWithStatusInProcess()
        {
            return _orderRepo.GetAllOrderInprocessStatus();
        }

        public async Task<PagedResultLite<OrderProductionTrackingDto>> GetProductionTrackingByOrderStatusAsync(
    string status,
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(status))
                throw new ArgumentException("status không được để trống");

            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            status = status.Trim();

            var raw = await _orderRepo.GetProductionTrackingByOrderStatusAsync(
                status,
                page,
                pageSize,
                ct);

            var data = raw.Orders.Select(o =>
            {
                var requestIds = raw.Requests
                    .Where(r => r.order_id == o.order_id)
                    .Select(r => r.order_request_id)
                    .Distinct()
                    .ToList();

                var linkedProdIds = raw.ProdOrders
                    .Where(x => x.order_id == o.order_id)
                    .SelectMany(x =>
                        x.single_prod_id == null
                            ? new[] { x.prod_id }
                            : new[] { x.prod_id, x.single_prod_id.Value })
                    .ToHashSet();

                if (o.production_id != null)
                    linkedProdIds.Add(o.production_id.Value);

                var relatedProductions = raw.Productions
                    .Where(p =>
                        (p.order_id != null && p.order_id.Value == o.order_id) ||
                        linkedProdIds.Contains(p.prod_id))
                    .GroupBy(p => p.prod_id)
                    .Select(g => g.First())
                    .OrderBy(p => p.planned_start_date)
                    .ThenBy(p => p.prod_id)
                    .ToList();

                return new OrderProductionTrackingDto
                {
                    request_id = requestIds.Count > 0 ? requestIds[0] : null,

                    order_id = o.order_id,
                    order_status = o.status,

                    productions = relatedProductions.Select(p => new ProductionTrackingDto
                    {
                        prod_id = p.prod_id,
                        code = p.code,
                        order_id = p.order_id,
                        manager_id = p.manager_id,
                        end_date = p.end_date,
                        status = p.status,
                        product_type_id = p.product_type_id,
                        note = p.note,
                        created_at = p.created_at,
                        planned_start_date = p.planned_start_date,
                        actual_start_date = p.actual_start_date,
                        is_full_process = p.is_full_process,
                        sub_product_used_qty = p.sub_product_used_qty,
                        import_recieve_path = p.import_recieve_path,
                        sub_product_id = p.sub_product_id,
                        nvl_qty = p.nvl_qty,
                        prod_method = p.prod_method,
                        gm_note = p.gm_note,
                        mgr_note = p.mgr_note,
                        prod_kind = p.prod_kind,
                        group_process_codes = p.group_process_codes,
                        group_total_qty = p.group_total_qty,
                        gm_proposed_method = p.gm_proposed_method,

                        tasks = raw.Tasks
                            .Where(t => t.prod_id == p.prod_id)
                            .OrderBy(t => t.seq_num)
                            .ThenBy(t => t.task_id)
                            .Select(t => new TaskTrackingDto
                            {
                                task_id = t.task_id,
                                prod_id = t.prod_id,
                                name = t.name,
                                seq_num = t.seq_num,
                                status = t.status,
                                machine = t.machine,
                                start_time = t.start_time,
                                end_time = t.end_time,
                                process_id = t.process_id,
                                planned_start_time = t.planned_start_time,
                                planned_end_time = t.planned_end_time,
                                reason = t.reason,
                                is_taken_sub_product = t.is_taken_sub_product,
                                input_mode = t.input_mode,

                                task_logs = raw.TaskLogs
                                    .Where(l => l.task_id == t.task_id)
                                    .OrderByDescending(l => l.log_time)
                                    .ThenByDescending(l => l.log_id)
                                    .Select(l => new TaskLogTrackingDto
                                    {
                                        log_id = l.log_id,
                                        task_id = l.task_id,
                                        scanned_code = l.scanned_code,
                                        action_type = l.action_type,
                                        qty_good = l.qty_good,
                                        log_time = l.log_time,
                                        scanned_by_user_id = l.scanned_by_user_id,
                                        material_usage_json = l.material_usage_json,
                                        reason = l.reason,
                                        report_image_url = l.report_image_url,
                                        reference_input_json = l.reference_input_json,
                                        output_json = l.output_json
                                    })
                                    .ToList()
                            })
                            .ToList()
                    })
                    .ToList()
                };
            }).ToList();

            return new PagedResultLite<OrderProductionTrackingDto>
            {
                Page = raw.Page,
                PageSize = raw.PageSize,
                HasNext = raw.HasNext,
                Data = data
            };
        }

        public async Task<PagedResultLite<OrdersByProcessDto>> GetOrdersByCurrentProcessAsync(
    string processCode,
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(processCode))
                throw new ArgumentException("processCode không được để trống");

            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            var normalizedProcessCode = NormalizeProcessCode(processCode);

            var raw = await _orderRepo.GetOrdersByProcessCodeRawAsync(
                normalizedProcessCode,
                ct);

            var allData = raw.Orders.Select(o =>
            {
                var linkedProdIds = raw.ProdOrders
                    .Where(x => x.order_id == o.order_id)
                    .SelectMany(x =>
                        x.single_prod_id == null
                            ? new[] { x.prod_id }
                            : new[] { x.prod_id, x.single_prod_id.Value })
                    .ToHashSet();

                if (o.production_id != null)
                    linkedProdIds.Add(o.production_id.Value);

                var relatedProductions = raw.Productions
                    .Where(p =>
                        (p.order_id != null && p.order_id.Value == o.order_id) ||
                        linkedProdIds.Contains(p.prod_id))
                    .GroupBy(p => p.prod_id)
                    .Select(g => g.First())
                    .OrderBy(p => p.planned_start_date)
                    .ThenBy(p => p.prod_id)
                    .ToList();

                var allTasksOfOrder = relatedProductions
                    .SelectMany(p => raw.Tasks.Where(t => t.prod_id == p.prod_id))
                    .OrderBy(t => t.seq_num ?? int.MaxValue)
                    .ThenBy(t => t.task_id)
                    .ToList();

                var currentTask = allTasksOfOrder
                    .FirstOrDefault(t => IsDoingTaskForProcessList(t.status, t.start_time, t.end_time));

                currentTask ??= allTasksOfOrder
                    .FirstOrDefault(t => !IsDoneTaskForProcessList(t.status, t.end_time));

                if (currentTask == null)
                    return null;

                var currentTaskProcess = raw.TaskProcesses
                    .FirstOrDefault(x => x.task_id == currentTask.task_id);

                var currentProcessCode = ResolveTaskProcessCode(currentTask, currentTaskProcess);

                if (!IsSameProcessCode(currentProcessCode, normalizedProcessCode))
                    return null;

                var currentTaskId = currentTask.task_id;

                return new OrdersByProcessDto
                {
                    order_id = o.order_id,
                    code = o.code,
                    quote_id = o.quote_id,
                    order_date = o.order_date,
                    delivery_date = o.delivery_date,
                    total_amount = o.total_amount,
                    status = o.status,
                    payment_status = o.payment_status,
                    production_id = o.production_id,
                    is_enough = o.is_enough,
                    is_buy = o.is_buy,
                    layout_confirmed = o.layout_confirmed,
                    is_production_ready = o.is_production_ready,
                    confirmed_delivery_at = o.confirmed_delivery_at,

                    productions = relatedProductions.Select(p => new ProductionByProcessDto
                    {
                        prod_id = p.prod_id,
                        code = p.code,
                        order_id = p.order_id,
                        manager_id = p.manager_id,
                        end_date = p.end_date,
                        status = p.status,
                        product_type_id = p.product_type_id,
                        note = p.note,
                        created_at = p.created_at,
                        planned_start_date = p.planned_start_date,
                        actual_start_date = p.actual_start_date,
                        is_full_process = p.is_full_process,
                        sub_product_used_qty = p.sub_product_used_qty,
                        import_recieve_path = p.import_recieve_path,
                        sub_product_id = p.sub_product_id,
                        nvl_qty = p.nvl_qty,
                        prod_method = p.prod_method,
                        gm_note = p.gm_note,
                        mgr_note = p.mgr_note,
                        prod_kind = p.prod_kind,
                        group_process_codes = p.group_process_codes,
                        group_total_qty = p.group_total_qty,
                        gm_proposed_method = p.gm_proposed_method,

                        tasks = raw.Tasks
                            .Where(t => t.prod_id == p.prod_id)
                            .OrderBy(t => t.seq_num ?? int.MaxValue)
                            .ThenBy(t => t.task_id)
                            .Select(t => new TaskByProcessDto
                            {
                                task_id = t.task_id,
                                prod_id = t.prod_id,
                                name = t.name,
                                seq_num = t.seq_num,
                                status = t.status,
                                machine = t.machine,
                                start_time = t.start_time,
                                end_time = t.end_time,
                                process_id = t.process_id,
                                planned_start_time = t.planned_start_time,
                                planned_end_time = t.planned_end_time,
                                reason = t.reason,
                                is_taken_sub_product = t.is_taken_sub_product,
                                input_mode = t.input_mode,
                                is_current = t.task_id == currentTaskId
                            })
                            .ToList()
                    })
                    .ToList()
                };
            })
            .Where(x => x != null)
            .Cast<OrdersByProcessDto>()
            .OrderByDescending(x => x.order_date)
            .ThenByDescending(x => x.order_id)
            .ToList();

            var pageRows = allData
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .ToList();

            var hasNext = pageRows.Count > pageSize;

            if (hasNext)
                pageRows.RemoveAt(pageRows.Count - 1);

            return new PagedResultLite<OrdersByProcessDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = pageRows
            };
        }

        public async Task<PagedResultLite<OrderFullTrackingDto>> GetAllOrdersProductionTrackingAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;

            var raw = await _orderRepo.GetAllOrdersProductionTrackingRawAsync(
                page,
                pageSize,
                ct);

            var data = raw.Orders.Select(o =>
            {
                var requestId = raw.Requests
                    .Where(r => r.order_id == o.order_id)
                    .OrderByDescending(r => r.order_request_id)
                    .Select(r => (int?)r.order_request_id)
                    .FirstOrDefault();

                var linkedProdIds = raw.ProdOrders
                    .Where(x => x.order_id == o.order_id)
                    .SelectMany(x =>
                        x.single_prod_id == null
                            ? new[] { x.prod_id }
                            : new[] { x.prod_id, x.single_prod_id.Value })
                    .ToHashSet();

                if (o.production_id != null)
                    linkedProdIds.Add(o.production_id.Value);

                var relatedProductions = raw.Productions
                    .Where(p =>
                        (p.order_id != null && p.order_id.Value == o.order_id) ||
                        linkedProdIds.Contains(p.prod_id))
                    .GroupBy(p => p.prod_id)
                    .Select(g => g.First())
                    .OrderBy(p => p.planned_start_date)
                    .ThenBy(p => p.prod_id)
                    .ToList();

                return new OrderFullTrackingDto
                {
                    request_id = requestId,

                    order_id = o.order_id,
                    code = o.code,
                    quote_id = o.quote_id,
                    order_date = o.order_date,
                    delivery_date = o.delivery_date,
                    total_amount = o.total_amount,
                    status = o.status,
                    payment_status = o.payment_status,
                    production_id = o.production_id,
                    is_enough = o.is_enough,
                    is_buy = o.is_buy,
                    layout_confirmed = o.layout_confirmed,
                    is_production_ready = o.is_production_ready,
                    confirmed_delivery_at = o.confirmed_delivery_at,

                    productions = relatedProductions.Select(p => new OrderFullProductionDto
                    {
                        prod_id = p.prod_id,
                        code = p.code,
                        order_id = p.order_id,
                        manager_id = p.manager_id,
                        end_date = p.end_date,
                        status = p.status,
                        product_type_id = p.product_type_id,
                        note = p.note,
                        created_at = p.created_at,
                        planned_start_date = p.planned_start_date,
                        actual_start_date = p.actual_start_date,
                        is_full_process = p.is_full_process,
                        sub_product_used_qty = p.sub_product_used_qty,
                        import_recieve_path = p.import_recieve_path,
                        sub_product_id = p.sub_product_id,
                        nvl_qty = p.nvl_qty,
                        prod_method = p.prod_method,
                        gm_note = p.gm_note,
                        mgr_note = p.mgr_note,
                        prod_kind = p.prod_kind,
                        group_process_codes = p.group_process_codes,
                        group_total_qty = p.group_total_qty,
                        gm_proposed_method = p.gm_proposed_method,

                        tasks = raw.Tasks
                            .Where(t => t.prod_id == p.prod_id)
                            .OrderBy(t => t.seq_num ?? int.MaxValue)
                            .ThenBy(t => t.task_id)
                            .Select(t =>
                            {
                                var taskProcess = raw.TaskProcesses
                                    .FirstOrDefault(x => x.task_id == t.task_id);

                                return new OrderFullTaskDto
                                {
                                    task_id = t.task_id,
                                    prod_id = t.prod_id,
                                    name = t.name,
                                    seq_num = t.seq_num,
                                    status = t.status,
                                    machine = t.machine,
                                    start_time = t.start_time,
                                    end_time = t.end_time,
                                    process_id = t.process_id,
                                    planned_start_time = t.planned_start_time,
                                    planned_end_time = t.planned_end_time,
                                    reason = t.reason,
                                    is_taken_sub_product = t.is_taken_sub_product,
                                    input_mode = t.input_mode,

                                    process_code = taskProcess?.process_code,
                                    process_name = taskProcess?.process_name,

                                    task_logs = raw.TaskLogs
                                        .Where(l => l.task_id == t.task_id)
                                        .OrderByDescending(l => l.log_time)
                                        .ThenByDescending(l => l.log_id)
                                        .Select(l => new OrderFullTaskLogDto
                                        {
                                            log_id = l.log_id,
                                            task_id = l.task_id,
                                            scanned_code = l.scanned_code,
                                            action_type = l.action_type,
                                            qty_good = l.qty_good,
                                            log_time = l.log_time,
                                            scanned_by_user_id = l.scanned_by_user_id,
                                            material_usage_json = l.material_usage_json,
                                            reason = l.reason,
                                            report_image_url = l.report_image_url,
                                            reference_input_json = l.reference_input_json,
                                            output_json = l.output_json
                                        })
                                        .ToList()
                                };
                            })
                            .ToList()
                    })
                    .ToList()
                };
            }).ToList();

            return new PagedResultLite<OrderFullTrackingDto>
            {
                Page = raw.Page,
                PageSize = raw.PageSize,
                HasNext = raw.HasNext,
                Data = data
            };
        }

        private static string NormalizeProcessCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static bool IsSameProcessCode(string? actual, string expected)
        {
            actual = NormalizeProcessCode(actual);
            expected = NormalizeProcessCode(expected);

            if (actual == expected)
                return true;

            return expected switch
            {
                "IN" => actual == "IN",
                "RALO" => actual == "RALO" || actual == "RA_LO" || actual == "RA_LÔ",
                "CAT" => actual == "CAT" || actual == "CẮT",
                "PHU" => actual == "PHU" || actual == "PHỦ",
                "BOI" => actual == "BOI" || actual == "BỒI",
                "DAN" => actual == "DAN" || actual == "DÁN",
                "BE" => actual == "BE" || actual == "BẾ",
                "CAN" => actual == "CAN" || actual == "CÁN",
                "CAN_MANG" => actual == "CAN_MANG" || actual == "CÁN_MÀNG" || actual == "CAN",
                _ => actual == expected
            };
        }

        private static string? ResolveTaskProcessCode(task t, TaskProcessLiteRaw? taskProcess)
        {
            if (!string.IsNullOrWhiteSpace(taskProcess?.process_code))
                return NormalizeProcessCode(taskProcess.process_code);

            if (!string.IsNullOrWhiteSpace(t.machine))
                return NormalizeProcessCode(t.machine);

            if (!string.IsNullOrWhiteSpace(t.name))
                return NormalizeProcessCode(t.name);

            return null;
        }

        private static bool IsDoneTaskForProcessList(string? status, DateTime? endTime)
        {
            if (endTime != null)
                return true;

            if (string.IsNullOrWhiteSpace(status))
                return false;

            var s = status.Trim().ToUpperInvariant();

            return s == "DONE"
                || s == "FINISH"
                || s == "FINISHED"
                || s == "COMPLETED"
                || s == "COMPLETE";
        }

        private static bool IsDoingTaskForProcessList(string? status, DateTime? startTime, DateTime? endTime)
        {
            if (startTime != null && endTime == null)
                return true;

            if (string.IsNullOrWhiteSpace(status))
                return false;

            var s = status.Trim().ToUpperInvariant();

            return s == "DOING"
                || s == "PROCESSING"
                || s == "INPROCESS"
                || s == "IN_PROCESS"
                || s == "INPROGRESS"
                || s == "IN_PROGRESS"
                || s == "INPROCESSING"
                || s == "STARTED"
                || s == "RUNNING";
        }
    }
}
