using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AMMS.Application.Services
{
    public class GroupProductionService : IGroupProductionService
    {
        private readonly AppDbContext _db;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GroupProductionService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<GroupProductionCandidateDto>> GetCandidatesAsync(
    int? productTypeId,
    string? processCodes,
    CancellationToken ct = default)
        {
            var selectedCodes = GroupProductionHelper.ParseCodes(processCodes);

            if (selectedCodes.Count > 0)
                GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            var today = AppTime.NowVnUnspecified().Date;
            var minDeliveryDate = today.AddDays(7);

            var rows = await (
                from o in _db.orders.AsNoTracking()
                join pr in _db.productions.AsNoTracking()
                    on o.order_id equals pr.order_id
                where pr.prod_kind == "SINGLE"
                      && o.layout_confirmed
                      && o.is_production_ready

                      // Business rule:Chỉ order có delivery_date cách ngày hiện tại ít nhất 7 ngày
                      && o.delivery_date != null
                      && o.delivery_date >= minDeliveryDate

                      && o.status != "Delivery"
                      && o.status != "Completed"
                      && o.status != "Importing"
                      && o.status != "InProcessing"
                      && o.status != "Finished"

                      // Không hiện order đã nằm trong production GROUP active.
                      && !_db.prod_orders.Any(po =>
                            po.order_id == o.order_id &&
                            po.status == "Active" &&
                            _db.productions.Any(g =>
                                g.prod_id == po.prod_id &&
                                g.prod_kind == "GROUP" &&
                                g.status != "Cancelled" &&
                                g.status != "Completed"))
                select new
                {
                    o.order_id,
                    order_code = o.code,
                    single_prod_id = pr.prod_id,
                    o.delivery_date,
                    item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.product_type_id,
                            i.product_name,
                            i.quantity,
                            i.production_process
                        })
                        .FirstOrDefault()
                }
            ).ToListAsync(ct);

            var result = new List<GroupProductionCandidateDto>();

            foreach (var row in rows)
            {
                if (row.item == null)
                    continue;

                if (productTypeId.HasValue && row.item.product_type_id != productTypeId.Value)
                    continue;

                var orderCodes = GroupProductionHelper.ParseCodes(row.item.production_process);
                var processKey = string.Join(",", orderCodes);

                var hasAllSelectedCodes = selectedCodes.Count == 0 ||
                                          selectedCodes.All(x =>
                                              orderCodes.Contains(x, StringComparer.OrdinalIgnoreCase));

                var productTypeName = row.item.product_type_id.HasValue
                    ? await _db.product_types.AsNoTracking()
                        .Where(x => x.product_type_id == row.item.product_type_id.Value)
                        .Select(x => x.name)
                        .FirstOrDefaultAsync(ct)
                    : null;

                result.Add(new GroupProductionCandidateDto
                {
                    order_id = row.order_id,
                    order_code = row.order_code,
                    single_prod_id = row.single_prod_id,
                    product_type_id = row.item.product_type_id,
                    product_type_name = productTypeName,
                    product_name = row.item.product_name,
                    quantity = row.item.quantity,
                    production_process = row.item.production_process,
                    process_key = processKey,
                    delivery_date = row.delivery_date,

                    can_group = hasAllSelectedCodes,

                    reason = hasAllSelectedCodes
                        ? null
                        : "Order không có đủ các công đoạn được chọn để ghép."
                });
            }

            return result
                .OrderBy(x => x.product_type_id)
                .ThenBy(x => x.delivery_date)
                .ThenBy(x => x.order_id)
                .ToList();
        }

        public async Task<CreateGroupProductionResponse> CreateAsync(
            CreateGroupProductionRequest req,
            int? managerUserId,
            CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            var orderIds = req.order_ids
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để sản xuất ghép.");

            var selectedCodes = req.process_codes
                .Select(GroupProductionHelper.Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn sản xuất chung.");

            GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var rows = await (
                    from o in _db.orders
                    join pr in _db.productions on o.order_id equals pr.order_id
                    where orderIds.Contains(o.order_id)
                          && pr.prod_kind == "SINGLE"
                    select new
                    {
                        order = o,
                        singleProd = pr,
                        item = _db.order_items
                            .Where(i => i.order_id == o.order_id)
                            .OrderBy(i => i.item_id)
                            .FirstOrDefault()
                    }
                ).ToListAsync(ct);

                if (rows.Count != orderIds.Count)
                    throw new InvalidOperationException("Một số order không tồn tại hoặc chưa có production riêng.");

                var today = AppTime.NowVnUnspecified().Date;
                var minDeliveryDate = today.AddDays(7);

                var invalidDeliveryOrders = rows
                    .Where(x => x.order.delivery_date == null || x.order.delivery_date < minDeliveryDate)
                    .Select(x => new
                    {
                        x.order.order_id,
                        x.order.delivery_date
                    })
                    .ToList();

                if (invalidDeliveryOrders.Count > 0)
                {
                    var invalidText = string.Join(", ", invalidDeliveryOrders.Select(x =>
                        $"{x.order_id}({(x.delivery_date.HasValue ? x.delivery_date.Value.ToString("yyyy-MM-dd") : "null")})"));

                    throw new InvalidOperationException(
                        $"Chỉ được ghép các order có delivery_date cách ngày hiện tại ít nhất 7 ngày. Order không hợp lệ: {invalidText}");
                }
                if (rows.Any(x => x.item == null))
                    throw new InvalidOperationException("Một số order chưa có order_item.");

                if (rows.Any(x => !x.order.layout_confirmed || !x.order.is_production_ready))
                    throw new InvalidOperationException("Tất cả order phải xác nhận layout và sẵn sàng sản xuất.");

                var productTypeIds = rows
                    .Select(x => x.item!.product_type_id)
                    .Distinct()
                    .ToList();

                if (productTypeIds.Count != 1 || productTypeIds[0] == null)
                    throw new InvalidOperationException("Các order phải cùng product_type.");

                var productTypeId = productTypeIds[0]!.Value;

                foreach (var row in rows)
                {
                    var orderCodes = GroupProductionHelper.ParseCodes(row.item!.production_process);

                    var missing = selectedCodes
                        .Where(x => !orderCodes.Contains(x, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Order {row.order.order_id} thiếu công đoạn: {string.Join(",", missing)}");
                    }
                }

                var alreadyGroupedOrderIds = await _db.prod_orders
                    .Where(x => orderIds.Contains(x.order_id) && x.status == "Active")
                    .Where(x => _db.productions.Any(p =>
                        p.prod_id == x.prod_id &&
                        p.prod_kind == "GROUP" &&
                        p.status != "Cancelled" &&
                        p.status != "Completed"))
                    .Select(x => x.order_id)
                    .Distinct()
                    .ToListAsync(ct);

                if (alreadyGroupedOrderIds.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Một số order đã nằm trong lệnh sản xuất ghép khác: {string.Join(",", alreadyGroupedOrderIds)}");
                }

                var now = AppTime.NowVnUnspecified();

                var groupProd = new production
                {
                    code = $"GRP-{now:yyyyMMddHHmmss}",
                    order_id = null,
                    manager_id = managerUserId,
                    created_at = now,
                    planned_start_date = req.planned_start_date ?? now,
                    status = "Unassigned",
                    product_type_id = productTypeId,
                    note = req.note,
                    prod_kind = "GROUP",
                    prod_method = "GROUP",
                    group_process_codes = string.Join(",", selectedCodes),
                    group_total_qty = rows.Sum(x => x.item!.quantity)
                };

                await _db.productions.AddAsync(groupProd, ct);
                await _db.SaveChangesAsync(ct);

                foreach (var row in rows)
                {
                    await _db.prod_orders.AddAsync(new prod_order
                    {
                        prod_id = groupProd.prod_id,
                        order_id = row.order.order_id,
                        single_prod_id = row.singleProd.prod_id,
                        qty = row.item!.quantity,
                        product_type_id = productTypeId,
                        product_process = row.item.production_process,
                        status = "Active",
                        created_at = now
                    }, ct);
                }

                var allSteps = await _db.product_type_processes
    .Where(x => x.product_type_id == productTypeId && (x.is_active ?? true))
    .OrderBy(x => x.seq_num)
    .ToListAsync(ct);

                var stepRows = allSteps
                    .Where(x => selectedCodes.Contains(
                        GroupProductionHelper.Norm(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => x.seq_num)
                    .ToList();

                if (stepRows.Count != selectedCodes.Count)
                {
                    var foundCodes = stepRows
                        .Select(x => GroupProductionHelper.Norm(x.process_code))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var missing = selectedCodes
                        .Where(x => !foundCodes.Contains(x))
                        .ToList();

                    throw new InvalidOperationException(
                        $"Không tìm thấy đủ process trong product_type_processes. Thiếu: {string.Join(",", missing)}");
                }

                var groupTasks = new List<task>();
                var seq = 1;

                foreach (var step in stepRows)
                {
                    var groupTask = new task
                    {
                        prod_id = groupProd.prod_id,
                        name = $"GROUP-{step.process_name ?? step.process_code}",
                        seq_num = seq++,
                        status = "Unassigned",
                        machine = step.machine,
                        process_id = step.process_id,
                        input_mode = "MANUAL",
                        reason = "Task thuộc production ghép, nhập tay input/output khi báo cáo."
                    };

                    await _db.tasks.AddAsync(groupTask, ct);
                    groupTasks.Add(groupTask);
                }

                await _db.SaveChangesAsync(ct);

                await LinkSingleTasksAsync(groupProd, groupTasks, rows.Select(x => new SingleRow
                {
                    OrderId = x.order.order_id,
                    SingleProdId = x.singleProd.prod_id,
                    Qty = x.item!.quantity
                }).ToList(), ct);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new CreateGroupProductionResponse
                {
                    group_prod_id = groupProd.prod_id,
                    group_code = groupProd.code,
                    product_type_id = productTypeId,
                    total_qty = groupProd.group_total_qty,
                    order_ids = orderIds,
                    process_codes = selectedCodes,
                    group_task_ids = groupTasks.Select(x => x.task_id).ToList(),
                    message = "Đã tạo production ghép. RALO/CAT/IN vẫn giữ ở production riêng của từng order."
                };
            });
        }

        public async Task StartAsync(int groupProdId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == groupProdId, ct);

            if (prod == null)
                throw new KeyNotFoundException("Group production not found.");

            if (!string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production này không phải production ghép.");

            var orderCount = await _db.prod_orders
                .CountAsync(x => x.prod_id == groupProdId && x.status == "Active", ct);

            if (orderCount < 2)
                throw new InvalidOperationException("Production ghép cần ít nhất 2 order.");

            var now = AppTime.NowVnUnspecified();

            prod.status = "InProcessing";
            prod.actual_start_date ??= now;

            var orderIds = await _db.prod_orders
                .Where(x => x.prod_id == groupProdId && x.status == "Active")
                .Select(x => x.order_id)
                .ToListAsync(ct);

            var orders = await _db.orders
                .Where(x => orderIds.Contains(x.order_id))
                .ToListAsync(ct);

            foreach (var order in orders)
            {
                if (!string.Equals(order.status, "InProcessing", StringComparison.OrdinalIgnoreCase))
                    order.status = "InProcessing";
            }

            await _db.SaveChangesAsync(ct);
        }

        public async Task<GroupProductionDetailDto?> GetDetailAsync(
            int groupProdId,
            CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == groupProdId, ct);

            if (prod == null)
                return null;

            if (!string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production này không phải production ghép.");

            var productTypeName = prod.product_type_id.HasValue
                ? await _db.product_types.AsNoTracking()
                    .Where(x => x.product_type_id == prod.product_type_id.Value)
                    .Select(x => x.name)
                    .FirstOrDefaultAsync(ct)
                : null;

            var orderRows = await (
                from po in _db.prod_orders.AsNoTracking()
                join o in _db.orders.AsNoTracking() on po.order_id equals o.order_id
                where po.prod_id == groupProdId && po.status == "Active"
                orderby po.id
                select new GroupProductionOrderDto
                {
                    order_id = po.order_id,
                    order_code = o.code,
                    single_prod_id = po.single_prod_id ?? 0,
                    qty = po.qty,
                    status = o.status
                }
            ).ToListAsync(ct);

            var tasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == groupProdId)
                .OrderBy(x => x.seq_num)
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs
                .AsNoTracking()
                .Where(x => x.task_id.HasValue && taskIds.Contains(x.task_id.Value))
                .OrderBy(x => x.log_time)
                .Select(x => new GroupTaskLogDto
                {
                    log_id = x.log_id,
                    task_id = x.task_id!.Value,
                    action_type = x.action_type,
                    qty_good = x.qty_good ?? 0,
                    log_time = x.log_time,
                    reason = x.reason,
                    reference_input_json = x.reference_input_json,
                    material_usage_json = x.material_usage_json,
                    output_json = x.output_json
                })
                .ToListAsync(ct);

            var allocations = await (
                from tq in _db.task_qtys.AsNoTracking()
                join o in _db.orders.AsNoTracking() on tq.order_id equals o.order_id
                where taskIds.Contains(tq.group_task_id)
                select new
                {
                    tq.group_task_id,
                    tq.order_id,
                    order_code = o.code,
                    tq.single_task_id,
                    tq.qty_good,
                    tq.output_json
                }
            ).ToListAsync(ct);

            var stages = new List<GroupProductionStageDto>();
            var previousOutputQty = (decimal)prod.group_total_qty;
            var previousOutputName = "Bán thành phẩm từ các order ghép";

            foreach (var task in tasks)
            {
                var taskLogs = logs
                    .Where(x => x.task_id == task.task_id)
                    .ToList();

                var stageAllocations = allocations
                    .Where(x => x.group_task_id == task.task_id)
                    .Select(x => new GroupTaskAllocationDto
                    {
                        order_id = x.order_id,
                        order_code = x.order_code,
                        single_task_id = x.single_task_id,
                        qty_good = x.qty_good,
                        output_json = x.output_json
                    })
                    .ToList();

                var io = BuildGroupStageIO(
                    task.process?.process_code,
                    task.process?.process_name,
                    prod.group_total_qty,
                    previousOutputQty,
                    previousOutputName,
                    taskLogs);

                var actualOutput = taskLogs.Sum(x => x.qty_good);

                stages.Add(new GroupProductionStageDto
                {
                    task_id = task.task_id,
                    seq_num = task.seq_num,
                    process_code = task.process?.process_code,
                    process_name = task.process?.process_name,
                    status = task.status,
                    start_time = task.start_time,
                    end_time = task.end_time,
                    estimated_output_qty = io.estimatedOutputQty,
                    actual_output_qty = actualOutput,
                    input_materials = io.inputs,
                    outputs = io.outputs,
                    logs = taskLogs,
                    allocations = stageAllocations
                });

                previousOutputQty = actualOutput > 0 ? actualOutput : io.estimatedOutputQty;
                previousOutputName = io.outputs.FirstOrDefault()?.name ?? $"BTP sau {task.process?.process_code}";
            }

            return new GroupProductionDetailDto
            {
                prod_id = prod.prod_id,
                code = prod.code,
                status = prod.status,
                product_type_id = prod.product_type_id,
                product_type_name = productTypeName,
                total_qty = prod.group_total_qty,
                process_codes = prod.group_process_codes,
                orders = orderRows,
                stages = stages
            };
        }

        private async Task LinkSingleTasksAsync(
    production groupProd,
    List<task> groupTasks,
    List<SingleRow> rows,
    CancellationToken ct)
        {
            var groupTaskCodes = new Dictionary<string, task>(StringComparer.OrdinalIgnoreCase);

            foreach (var groupTask in groupTasks)
            {
                var code = await _db.product_type_processes
                    .Where(x => x.process_id == groupTask.process_id)
                    .Select(x => x.process_code)
                    .FirstAsync(ct);

                groupTaskCodes[GroupProductionHelper.Norm(code)] = groupTask;
            }

            var singleProdIds = rows.Select(x => x.SingleProdId).Distinct().ToList();

            var singleTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && singleProdIds.Contains(x.prod_id.Value))
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                foreach (var kv in groupTaskCodes)
                {
                    var code = kv.Key;
                    var groupTask = kv.Value;

                    var singleTask = singleTasks.FirstOrDefault(x =>
                        x.prod_id == row.SingleProdId &&
                        string.Equals(
                            GroupProductionHelper.Norm(x.process?.process_code),
                            code,
                            StringComparison.OrdinalIgnoreCase));

                    if (singleTask == null)
                    {
                        throw new InvalidOperationException(
                            $"Production riêng {row.SingleProdId} không có task công đoạn {code}.");
                    }

                    if (!string.Equals(singleTask.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    {
                        singleTask.status = "GroupedWaiting";
                        singleTask.reason = $"Đã ghép vào production chung prod_id={groupProd.prod_id}.";
                    }

                    await _db.task_links.AddAsync(new task_link
                    {
                        group_prod_id = groupProd.prod_id,
                        group_task_id = groupTask.task_id,
                        single_prod_id = row.SingleProdId,
                        single_task_id = singleTask.task_id,
                        order_id = row.OrderId,
                        process_code = code,
                        qty_plan = row.Qty,
                        status = "Waiting",
                        created_at = AppTime.NowVnUnspecified()
                    }, ct);
                }
            }
        }

        private static (
            List<GroupStageMaterialDto> inputs,
            List<GroupStageMaterialDto> outputs,
            decimal estimatedOutputQty)
            BuildGroupStageIO(
                string? processCode,
                string? processName,
                int groupTotalQty,
                decimal previousOutputQty,
                string previousOutputName,
                List<GroupTaskLogDto> logs)
        {
            var code = GroupProductionHelper.Norm(processCode);
            var estimatedQty = groupTotalQty > 0 ? groupTotalQty : previousOutputQty;

            var inputs = new List<GroupStageMaterialDto>
        {
            new()
            {
                code = "PREV",
                name = previousOutputName,
                unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ",
                estimated_qty = previousOutputQty,
                actual_qty = ResolveActualReferenceInput(logs, previousOutputQty)
            }
        };

            if (code == "PHU")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "COATING",
                    name = "Keo/phủ nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "COATING")
                });
            }
            else if (code is "CAN" or "CAN_MANG")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "LAMINATION",
                    name = "Màng cán nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "LAMINATION")
                });
            }
            else if (code == "BOI")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "WAVE",
                    name = "Sóng carton nhập tay",
                    unit = "tờ",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "WAVE")
                });

                inputs.Add(new GroupStageMaterialDto
                {
                    code = "KEO_BOI",
                    name = "Keo bồi nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "KEO_BOI")
                });
            }

            var outputName = code switch
            {
                "PHU" => "BTP sau phủ",
                "CAN" => "BTP sau cán",
                "CAN_MANG" => "BTP sau cán màng",
                "BOI" => "BTP sau bồi",
                "BE" => "BTP sau bế",
                "DUT" => "BTP sau dứt",
                "DAN" => "Thành phẩm sau dán",
                _ => $"BTP sau {processName ?? processCode}"
            };

            var actualOutput = logs.Sum(x => x.qty_good);

            var outputs = new List<GroupStageMaterialDto>
        {
            new()
            {
                code = code,
                name = outputName,
                unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ",
                estimated_qty = estimatedQty,
                actual_qty = actualOutput
            }
        };

            return (inputs, outputs, estimatedQty);
        }

        private static decimal ResolveActualReferenceInput(
            List<GroupTaskLogDto> logs,
            decimal fallback)
        {
            var json = logs
                .OrderByDescending(x => x.log_time)
                .Select(x => x.reference_input_json)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(json))
                return 0;

            try
            {
                var refs = JsonSerializer.Deserialize<List<TaskReferenceUsageInputDto>>(json, JsonOptions)
                           ?? new List<TaskReferenceUsageInputDto>();

                return refs.Sum(x => x.quantity_used);
            }
            catch
            {
                return 0;
            }
        }

        private static decimal ResolveActualMaterial(
            List<GroupTaskLogDto> logs,
            string code)
        {
            var json = logs
                .OrderByDescending(x => x.log_time)
                .Select(x => x.material_usage_json)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(json))
                return 0;

            try
            {
                var mats = JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(json, JsonOptions)
                           ?? new List<TaskMaterialUsageLogItemDto>();

                return mats
                    .Where(x => string.Equals(x.material_code, code, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.quantity_used);
            }
            catch
            {
                return 0;
            }
        }

        private sealed class SingleRow
        {
            public int OrderId { get; set; }
            public int SingleProdId { get; set; }
            public int Qty { get; set; }
        }
    }
}
