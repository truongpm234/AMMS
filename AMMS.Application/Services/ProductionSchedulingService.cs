using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Planning;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace AMMS.Application.Services
{
    public class ProductionSchedulingService : IProductionSchedulingService
    {
        private readonly AppDbContext _db;
        private readonly IMachineRepository _machineRepo;
        private readonly ITaskRepository _taskRepo;
        private readonly WorkCalendar _cal;
        private readonly ILogger<ProductionSchedulingService> _logger;
        private readonly SchedulingOptions _opt;
        private readonly IProductionRepository _prodRepo;

        public ProductionSchedulingService(
            AppDbContext db,
            IProductionRepository prodRepo,
            IProductTypeProcessRepository ptpRepo,
            IMachineRepository machineRepo,
            ITaskRepository taskRepo,
            WorkCalendar cal,
            ILogger<ProductionSchedulingService> logger, IOptions<SchedulingOptions> opt)
        {
            _db = db;
            _machineRepo = machineRepo;
            _taskRepo = taskRepo;
            _cal = cal;
            _logger = logger;
            _opt = opt.Value ?? new SchedulingOptions();
            _prodRepo = prodRepo;
        }
        private async Task<int> GetMinStartWaitMinutesAsync(CancellationToken ct = default)
        {
            var hours = await _db.estimate_config
                .AsNoTracking()
                .Where(x => x.config_group == "planning" && x.config_key == "min_start_wait_hours")
                .OrderByDescending(x => x.updated_at)
                .Select(x => (decimal?)x.value_num)
                .FirstOrDefaultAsync(ct);

            var resolvedHours = (hours.HasValue && hours.Value > 0m) ? hours.Value : 6m;
            return (int)Math.Ceiling(resolvedHours * 60m);
        }

        public async Task<int> ScheduleOrderAsync(int orderId, int productTypeId, string? productionProcessCsv, int? managerId = 3)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                try
                {
                    var now = AppTime.NowVnUnspecified();

                    var ctx = await BuildPlanningContextByOrderIdAsync(
                        orderId,
                        productTypeId,
                        productionProcessCsv,
                        CancellationToken.None)
                        ?? throw new InvalidOperationException(
                            $"Cannot build planning context for order {orderId}. productTypeId={productTypeId}, csv={productionProcessCsv}");

                    _logger.LogInformation(
                        "ScheduleOrder start. OrderId={OrderId}, ProductTypeId={ProductTypeId}, RawCsv={RawCsv}, Qty={Qty}, SheetsTotal={SheetsTotal}, SheetsRequired={SheetsRequired}",
                        orderId, productTypeId, ctx.RawProductionProcessCsv, ctx.OrderQty, ctx.SheetsTotal, ctx.SheetsRequired);

                    var plan = await BuildStagePlansAsync(ctx, now, CancellationToken.None);

                    if (plan.Stages.Count == 0)
                        throw new InvalidOperationException(
                            $"No stage plan generated for order {orderId}. RawCsv={ctx.RawProductionProcessCsv}");

                    var prod = await GetOrCreateProductionAsync(orderId, productTypeId, managerId, now);

                    if (prod.created_at == null)
                        prod.created_at = now;

                    prod.planned_start_date = plan.PlanningAnchor;

                    await _db.SaveChangesAsync();

                    var order = await _db.orders
                        .AsTracking()
                        .FirstOrDefaultAsync(o => o.order_id == orderId);

                    if (order == null)
                        throw new InvalidOperationException($"Order {orderId} not found when updating production_id");

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

                    var orderItems = await _db.order_items
                        .Where(x => x.order_id == orderId)
                        .ToListAsync();

                    foreach (var item in orderItems)
                        item.production_process = plan.NormalizedProcessCsv;

                    await _db.SaveChangesAsync();

                    var subProductContext = await ResolveSubProductCompletedContextAsync(
    prod,
    plan.Stages,
    ctx,
    now,
    CancellationToken.None);

                    var tasks = plan.Stages
                        .OrderBy(x => x.SeqNum)
                        .Select(x =>
                        {
                            var isFinishedBySubProduct = subProductContext.finished_process_codes
                                .Contains(NormProcessCode(x.ProcessCode));

                            var finishedAt = isFinishedBySubProduct
                                ? now
                                : (DateTime?)null;

                            return new task
                            {
                                prod_id = prod.prod_id,
                                process_id = x.ProcessId,
                                seq_num = x.SeqNum,
                                name = x.ProcessName,

                                status = isFinishedBySubProduct ? "Finished" : "Unassigned",

                                machine = x.MachineCode,

                                start_time = finishedAt,
                                end_time = finishedAt,

                                planned_start_time = x.PlannedStart,
                                planned_end_time = x.PlannedEnd,

                                reason = isFinishedBySubProduct
                                    ? "Bán thành phẩm đã có sẵn trong kho"
                                    : null,

                                is_taken_sub_product = isFinishedBySubProduct
                            };
                        })
                        .ToList();

                    await _taskRepo.AddRangeAsync(tasks);
                    await _taskRepo.SaveChangesAsync();

                    await CreateSubProductFinishedLogsAsync(
                        tasks,
                        plan.Stages,
                        subProductContext,
                        ctx,
                        now,
                        CancellationToken.None);

                    _logger.LogInformation(
                        "ScheduleOrder success. OrderId={OrderId}, ProdId={ProdId}, TaskCount={TaskCount}, Csv={Csv}",
                        orderId, prod.prod_id, tasks.Count, plan.NormalizedProcessCsv);

                    await tx.CommitAsync();
                    return prod.prod_id;
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();

                    _logger.LogError(ex,
                        "ScheduleOrderAsync FAILED. OrderId={OrderId}, ProductTypeId={ProductTypeId}, ProductionProcessCsv={ProductionProcessCsv}",
                        orderId, productTypeId, productionProcessCsv);

                    throw;
                }
            });
        }

        public async Task<ProductionSchedulePreviewDto?> PreviewByOrderRequestAsync(int orderRequestId, CancellationToken ct = default)
        {
            var ctx = await BuildPlanningContextByOrderRequestAsync(orderRequestId, ct);
            if (ctx == null) return null;

            var now = AppTime.NowVnUnspecified();
            var plan = await BuildStagePlansAsync(ctx, now, ct);

            var estimatedFinish = plan.Stages.Count == 0
                ? now
                : plan.Stages.Max(x => x.PlannedEnd);

            return new ProductionSchedulePreviewDto
            {
                order_request_id = ctx.OrderRequestId,
                desired_delivery_date = ctx.DesiredDeliveryDate,
                estimated_finish_date = estimatedFinish,
                stages = plan.Stages
                    .OrderBy(x => x.SeqNum)
                    .Select(x => new ProductionStagePlanPreviewDto
                    {
                        process_id = x.ProcessId,
                        seq_num = x.SeqNum,
                        process_name = x.ProcessName,
                        process_code = x.ProcessCode,
                        machine_code = x.MachineCode,
                        unit = x.Unit,
                        required_units = x.RequiredUnits,
                        effective_capacity_per_hour = x.EffectiveCapacityPerHour,
                        setup_minutes = x.SetupMinutes,
                        handoff_minutes = x.HandoffMinutes,
                        planned_start_time = x.PlannedStart,
                        planned_end_time = x.PlannedEnd
                    })
                    .ToList()
            };
        }

        public async Task<int> DispatchDueTasksAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return 0;
        }

        private async Task<production> GetOrCreateProductionAsync(int orderId, int productTypeId, int? managerId, DateTime now)
        {
            var order = await _db.orders
                .FromSqlInterpolated($@"
            SELECT *
            FROM ""AMMS_DB"".""orders""
            WHERE order_id = {orderId}
            FOR UPDATE")
                .FirstAsync();

            production? prod = null;

            if (order.production_id.HasValue)
            {
                prod = await _db.productions
                    .AsTracking()
                    .FirstOrDefaultAsync(p => p.prod_id == order.production_id.Value && p.end_date == null);
            }

            prod ??= await _db.productions
                .AsTracking()
                .Where(p => p.order_id == orderId && p.end_date == null)
                .OrderByDescending(p => p.prod_id)
                .FirstOrDefaultAsync();

            if (prod != null)
                return prod;

            prod = new production
            {
                code = "TMP-PROD",
                order_id = orderId,
                manager_id = managerId,
                status = "Scheduled",
                product_type_id = productTypeId,
                created_at = now,
                planned_start_date = null,
                actual_start_date = null
            };

            await _db.productions.AddAsync(prod);
            await _db.SaveChangesAsync();

            prod.code = $"PROD-{prod.prod_id:00000}";
            await _db.SaveChangesAsync();

            order.production_id = prod.prod_id;
            await _db.SaveChangesAsync();

            return prod;
        }

        private async Task<PlanningContext?> BuildPlanningContextByOrderIdAsync(
    int orderId,
    int productTypeId,
    string? rawProductionProcessCsv,
    CancellationToken ct)
        {
            var row = await (
                from o in _db.orders.AsNoTracking()
                where o.order_id == orderId

                let firstItem = _db.order_items.AsNoTracking()
                    .Where(i => i.order_id == o.order_id)
                    .OrderBy(i => i.item_id)
                    .Select(i => new
                    {
                        i.production_process,
                        i.quantity,
                        i.length_mm,
                        i.width_mm,
                        i.height_mm
                    })
                    .FirstOrDefault()

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking() on q.order_request_id equals r.order_request_id into rj
                from r in rj.DefaultIfEmpty()

                select new
                {
                    order_id = o.order_id,
                    order_date = o.order_date,
                    order_delivery_date = o.delivery_date,
                    order_request_id = (int?)r.order_request_id,
                    accepted_estimate_id = (int?)r.accepted_estimate_id,
                    request_qty = (int?)r.quantity,
                    request_delivery_date = (DateTime?)r.delivery_date,
                    number_of_plates = (int?)r.number_of_plates,
                    is_one_side_box = (bool?)r.is_one_side_box,
                    product_length_mm = (int?)r.product_length_mm,
                    product_width_mm = (int?)r.product_width_mm,
                    product_height_mm = (int?)r.product_height_mm,
                    first_item_process = firstItem != null ? firstItem.production_process : null,
                    first_item_qty = firstItem != null ? (int?)firstItem.quantity : null,
                    first_item_length = firstItem != null ? firstItem.length_mm : null,
                    first_item_width = firstItem != null ? firstItem.width_mm : null,
                    first_item_height = firstItem != null ? firstItem.height_mm : null
                }
            ).FirstOrDefaultAsync(ct);

            if (row == null || !row.order_request_id.HasValue)
                return null;

            var est = await ResolveEstimateAsync(row.order_request_id.Value, row.accepted_estimate_id, ct);

            return new PlanningContext
            {
                OrderId = row.order_id,
                OrderRequestId = row.order_request_id.Value,
                ProductTypeId = productTypeId,
                OrderQty = SafeInt(row.first_item_qty ?? row.request_qty ?? 0, 1),
                SheetsTotal = est?.sheets_total ?? 0,
                SheetsRequired = est?.sheets_required ?? 0,
                NUp = SafeInt(est?.n_up ?? 0, 1),
                NumberOfPlates = row.number_of_plates ?? 0,
                IsOneSideBox = row.is_one_side_box ?? false,
                LengthMm = row.first_item_length ?? row.product_length_mm,
                WidthMm = row.first_item_width ?? row.product_width_mm,
                HeightMm = row.first_item_height ?? row.product_height_mm,
                TotalAreaM2 = est?.total_area_m2 ?? 0m,
                RawProductionProcessCsv =
                    !string.IsNullOrWhiteSpace(rawProductionProcessCsv)
                        ? rawProductionProcessCsv
                        : (!string.IsNullOrWhiteSpace(row.first_item_process)
                            ? row.first_item_process
                            : est?.production_processes),
                DesiredDeliveryDate = row.request_delivery_date ?? row.order_delivery_date ?? est?.desired_delivery_date,

                QueueDateTime = row.order_date ?? AppTime.NowVnUnspecified(),
                QueueOrderKey = row.order_id,
                WaveSheetsRequired = est?.wave_sheets_required ?? 0,
                WaveSheetsUsed = est?.wave_sheets_used ?? 0,
            };
        }

        private async Task<PlanningContext?> BuildPlanningContextByOrderRequestAsync(int orderRequestId, CancellationToken ct)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            if (req == null) return null;

            var ptCode = (req.product_type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ptCode))
                return null;

            var productTypeId = await _db.product_types
                .AsNoTracking()
                .Where(x => x.code == ptCode)
                .Select(x => (int?)x.product_type_id)
                .FirstOrDefaultAsync(ct);

            if (!productTypeId.HasValue)
                return null;

            var est = await ResolveEstimateAsync(orderRequestId, req.accepted_estimate_id, ct);

            return new PlanningContext
            {
                OrderId = null,
                OrderRequestId = orderRequestId,
                ProductTypeId = productTypeId.Value,
                OrderQty = SafeInt(req.quantity ?? 0, 1),
                SheetsTotal = est?.sheets_total ?? 0,
                SheetsRequired = est?.sheets_required ?? 0,
                NUp = SafeInt(est?.n_up ?? 0, 1),
                NumberOfPlates = req.number_of_plates ?? 0,
                IsOneSideBox = req.is_one_side_box ?? false,
                LengthMm = req.product_length_mm,
                WidthMm = req.product_width_mm,
                HeightMm = req.product_height_mm,
                TotalAreaM2 = est?.total_area_m2 ?? 0m,
                RawProductionProcessCsv = est?.production_processes,
                DesiredDeliveryDate = req.delivery_date ?? est?.desired_delivery_date,
                WaveSheetsRequired = est?.wave_sheets_required ?? 0,
                WaveSheetsUsed = est?.wave_sheets_used ?? 0,
                QueueDateTime = req.order_request_date ?? AppTime.NowVnUnspecified(),
                QueueOrderKey = req.order_request_id
            };
        }

        private async Task<cost_estimate?> ResolveEstimateAsync(int orderRequestId, int? acceptedEstimateId, CancellationToken ct)
        {
            var query = _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId);

            if (acceptedEstimateId.HasValue && acceptedEstimateId.Value > 0)
            {
                var accepted = await query
                    .FirstOrDefaultAsync(x => x.estimate_id == acceptedEstimateId.Value, ct);

                if (accepted != null)
                    return accepted;
            }

            return await query
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);
        }

        private async Task<PlanBuildResult> BuildStagePlansAsync(
    PlanningContext ctx,
    DateTime now,
    CancellationToken ct)
        {
            var allSteps = await _db.product_type_processes
                .AsNoTracking()
                .Where(x => x.product_type_id == ctx.ProductTypeId && (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .ToListAsync(ct);

            if (allSteps.Count == 0)
                throw new Exception($"No active routing for product_type_id={ctx.ProductTypeId}");

            var steps = ProductionProcessSelectionHelper.ResolveFixedRoute(
                allSteps,
                x => x.process_code,
                ctx.RawProductionProcessCsv);

            var normalizedCsv = ProductionProcessSelectionHelper.BuildCsv(steps, x => x.process_code);
            var normalizedNow = _cal.NormalizeStart(now);

            var minStartWaitMinutes = await GetMinStartWaitMinutesAsync(ct);

            var baseAnchor = EstimateInitialPlanningAnchor(
                ctx,
                normalizedNow,
                steps.Count,
                minStartWaitMinutes);

            var fifoAnchor = await ResolveQueueAnchorAsync(ctx, baseAnchor, ct);

            var plan = await BuildStagePlansFromAnchorAsync(
                ctx,
                fifoAnchor,
                steps,
                normalizedCsv,
                ct);

            if (ctx.DesiredDeliveryDate.HasValue && plan.Stages.Count > 0)
            {
                var finishDeadline = ResolveFinishDeadline(ctx.DesiredDeliveryDate.Value);
                var estimatedFinish = plan.Stages.Max(x => x.PlannedEnd);

                if (estimatedFinish > finishDeadline)
                {
                    _logger.LogWarning(
                        "FIFO scheduling may cause lateness. OrderId={OrderId}, RequestId={RequestId}, Anchor={Anchor}, EstimatedFinish={EstimatedFinish}, Deadline={Deadline}",
                        ctx.OrderId,
                        ctx.OrderRequestId,
                        fifoAnchor,
                        estimatedFinish,
                        finishDeadline);
                }
            }

            return plan;
        }

        private async Task<List<machine>> GetCandidateMachinesAsync(
            product_type_process step,
            Dictionary<string, List<machine>> cache,
            CancellationToken ct)
        {
            var cacheKey = $"STEP::{step.process_id}";
            if (cache.TryGetValue(cacheKey, out var cached))
                return cached;

            List<machine> result;

            if (!string.IsNullOrWhiteSpace(step.machine))
            {
                result = await _db.machines
                    .AsNoTracking()
                    .Where(x => x.is_active && x.machine_code == step.machine)
                    .OrderBy(x => x.machine_code)
                    .ToListAsync(ct);

                if (result.Count > 0)
                {
                    cache[cacheKey] = result;
                    return result;
                }
            }

            var pcode = ProductionProcessSelectionHelper.Norm(step.process_code);

            if (!string.IsNullOrWhiteSpace(pcode))
            {
                result = await _db.machines
                    .AsNoTracking()
                    .Where(x => x.is_active && x.process_code != null)
                    .Where(x => x.process_code!.Trim().ToUpper() == pcode)
                    .OrderByDescending(x => x.capacity_per_hour)
                    .ThenBy(x => x.machine_code)
                    .ToListAsync(ct);

                if (result.Count > 0)
                {
                    cache[cacheKey] = result;
                    return result;
                }
            }

            result = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active && x.process_name == step.process_name)
                .OrderByDescending(x => x.capacity_per_hour)
                .ThenBy(x => x.machine_code)
                .ToListAsync(ct);

            cache[cacheKey] = result;
            return result;
        }

        private async Task<MachinePoolState> GetOrBuildPoolStateAsync(
    machine m,
    DateTime planningAnchor,
    Dictionary<string, MachinePoolState> cache,
    CancellationToken ct)
        {
            var machineCode = (m.machine_code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(machineCode))
                throw new InvalidOperationException($"Machine code is empty. machine_id={m.machine_id}");

            if (cache.TryGetValue(machineCode, out var existing))
                return existing;

            var laneCount = Math.Max(1, m.quantity);
            var normalizedAnchor = _cal.NormalizeStart(planningAnchor);

            var laneStates = await _machineRepo.GetLaneAvailableTimesAsync(
                machineCode,
                normalizedAnchor,
                ignoreOverdueOrders: true,
                ct);

            var normalizedLaneStates = laneStates.Count > 0
                ? laneStates.OrderBy(x => x).ToList()
                : Enumerable.Repeat(normalizedAnchor, laneCount).ToList();

            if (normalizedLaneStates.Count < laneCount)
            {
                normalizedLaneStates.AddRange(
                    Enumerable.Repeat(normalizedAnchor, laneCount - normalizedLaneStates.Count));
            }

            var state = new MachinePoolState
            {
                Machine = m,
                LaneAvailableAt = normalizedLaneStates
            };

            cache[machineCode] = state;
            return state;
        }

        private static void AssignReservationToLane(List<DateTime> lanes, DateTime start, DateTime end)
        {
            var bestIndex = 0;
            var bestAvailable = lanes[0];

            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] <= start)
                {
                    bestIndex = i;
                    bestAvailable = lanes[i];
                    break;
                }

                if (lanes[i] < bestAvailable)
                {
                    bestIndex = i;
                    bestAvailable = lanes[i];
                }
            }

            var actualStart = bestAvailable > start ? bestAvailable : start;
            lanes[bestIndex] = end > actualStart ? end : actualStart;
        }

        private static (int laneIndex, DateTime freeAt) GetEarliestLane(List<DateTime> lanes)
        {
            var bestIndex = 0;
            var bestTime = lanes[0];

            for (var i = 1; i < lanes.Count; i++)
            {
                if (lanes[i] < bestTime)
                {
                    bestIndex = i;
                    bestTime = lanes[i];
                }
            }

            return (bestIndex, bestTime);
        }

        private static int SafeInt(int value, int fallback = 1)
            => value > 0 ? value : fallback;

        private static string GetStageUnit(string processCode)
        {
            return ProductionProcessSelectionHelper.Norm(processCode) switch
            {
                "DAN" => "sp",
                _ => "tờ"
            };
        }

        private static decimal GetStageRequiredUnits(string processCode, PlanningContext ctx)
        {
            var pcode = ProductionProcessSelectionHelper.Norm(processCode);

            if (pcode == "DAN")
                return SafeInt(ctx.OrderQty, 1);

            if (pcode == "BOI" && ctx.WaveSheetsUsed > 0)
                return ctx.WaveSheetsUsed;

            if (ctx.SheetsTotal > 0) return ctx.SheetsTotal;
            if (ctx.SheetsRequired > 0) return ctx.SheetsRequired;
            return SafeInt(ctx.OrderQty, 1);
        }

        private static int GetSetupMinutes(string processCode, PlanningContext ctx)
        {
            var pcode = ProductionProcessSelectionHelper.Norm(processCode);

            return pcode switch
            {
                "RALO" => 10,
                "CAT" => 8,
                "IN" => 15 + (ctx.NumberOfPlates * 3),
                "PHU" => 10,
                "CAN" => 12,
                "BOI" => 10,
                "BE" => 12,
                "DUT" => 8,
                "DAN" => 10,
                _ => 10
            };
        }

        private static int GetHandoffMinutes(string? prevProcessCode, string currentProcessCode, PlanningContext ctx)
        {
            var pcode = ProductionProcessSelectionHelper.Norm(currentProcessCode);

            return pcode switch
            {
                "CAT" => 5,
                "IN" => 8,
                "PHU" => 8,
                "CAN" => 10,
                "BOI" => 8,
                "BE" => 10,
                "DUT" => 8,
                "DAN" => 5,
                _ => 5
            };
        }

        private static int GetMachineTurnaroundMinutes(string processCode)
        {
            var pcode = ProductionProcessSelectionHelper.Norm(processCode);

            return pcode switch
            {
                "IN" => 5,
                "PHU" => 4,
                "CAN" => 4,
                "BE" => 4,
                "DAN" => 3,
                _ => 3
            };
        }

        private static decimal GetComplexityFactor(string processCode, PlanningContext ctx)
        {
            var pcode = ProductionProcessSelectionHelper.Norm(processCode);
            decimal factor = 1.0m;

            if (pcode == "IN" && ctx.NumberOfPlates >= 4)
                factor += 0.12m;

            if ((pcode == "BE" || pcode == "DAN") &&
                (ctx.LengthMm ?? 0) * (ctx.WidthMm ?? 0) * (ctx.HeightMm ?? 0) > 2_000_000)
            {
                factor += 0.10m;
            }

            if (!ctx.IsOneSideBox && (pcode == "BOI" || pcode == "DAN"))
                factor += 0.05m;

            if (ctx.OrderQty >= 10000)
                factor += 0.03m;

            return factor;
        }

        private static decimal GetEffectiveCapacityPerHour(machine m)
        {
            var eff = m.efficiency_percent <= 0 ? 100m : m.efficiency_percent;
            var raw = (decimal)m.capacity_per_hour * (eff / 100m);
            return raw > 0m ? raw : 1m;
        }

        private static double EstimateStageDurationHours(
            string processCode,
            decimal requiredUnits,
            machine m,
            PlanningContext ctx)
        {
            var effectiveCap = GetEffectiveCapacityPerHour(m);
            var setupMinutes = GetSetupMinutes(processCode, ctx);
            var complexity = GetComplexityFactor(processCode, ctx);

            var runHours = requiredUnits <= 0
                ? 0.05m
                : (requiredUnits / effectiveCap) * complexity;

            var totalHours = (setupMinutes / 60m) + runHours;

            if (totalHours <= 0.05m)
                totalHours = 0.05m;

            return (double)Math.Round(totalHours, 4);
        }

        private DateTime EstimateInitialPlanningAnchor(
    PlanningContext ctx,
    DateTime scheduledAt,
    int stageCount,
    int minStartWaitMinutes)
        {
            var leadMinutes = Math.Max(_opt.MinimumPlanningLeadMinutes, _opt.InitialLeadMinutes);

            if (stageCount >= _opt.LongRouteStageThreshold)
                leadMinutes += _opt.LongRouteExtraLeadMinutes;

            if (ctx.OrderQty >= _opt.LargeOrderQtyThreshold)
                leadMinutes += _opt.LargeOrderExtraLeadMinutes;

            if (ctx.SheetsTotal >= _opt.LargeSheetsThreshold)
                leadMinutes += Math.Max(15, _opt.LargeOrderExtraLeadMinutes / 2);

            if (ctx.NumberOfPlates >= _opt.HighPlateThreshold)
                leadMinutes += _opt.HighPlateExtraLeadMinutes;

            var selected = ProductionProcessSelectionHelper.ParseCsv(ctx.RawProductionProcessCsv);

            if (selected.Contains("IN"))
                leadMinutes += 10;

            if (selected.Contains("CAN") || selected.Contains("BOI"))
                leadMinutes += 10;

            leadMinutes = Math.Max(leadMinutes, minStartWaitMinutes);

            return _cal.NormalizeStart(scheduledAt.AddMinutes(leadMinutes));
        }

        private async Task<PlanBuildResult> BuildStagePlansFromAnchorAsync(
            PlanningContext ctx,
            DateTime planningAnchor,
            List<product_type_process> steps,
            string normalizedCsv,
            CancellationToken ct)
        {
            var machinePoolCache = new Dictionary<string, MachinePoolState>(StringComparer.OrdinalIgnoreCase);
            var candidateCache = new Dictionary<string, List<machine>>(StringComparer.OrdinalIgnoreCase);

            var result = new List<StagePlanDraft>();
            DateTime? prevEnd = null;
            string? prevCode = null;
            DateTime? raloEnd = null;

            foreach (var step in steps.OrderBy(x => x.seq_num))
            {
                var pcode = ProductionProcessSelectionHelper.Norm(step.process_code);
                if (string.IsNullOrWhiteSpace(pcode))
                    throw new Exception($"process_code missing for process_id={step.process_id}");

                var unit = GetStageUnit(pcode);
                var requiredUnits = GetStageRequiredUnits(pcode, ctx);

                var candidates = await GetCandidateMachinesAsync(step, candidateCache, ct);
                if (candidates.Count == 0)
                    throw new Exception($"No active machine found for process_code={pcode}");

                StagePlanDraft? best = null;

                foreach (var candidate in candidates)
                {
                    var pool = await GetOrBuildPoolStateAsync(candidate, planningAnchor, machinePoolCache, ct);
                    var (laneIndex, laneFreeAt) = GetEarliestLane(pool.LaneAvailableAt);

                    var independent = ProductionFlowHelper.IsInitialParallel(pcode);

                    var handoffMinutes = (!independent && prevEnd.HasValue)
                        ? GetHandoffMinutes(prevCode, pcode, ctx)
                        : 0;

                    var earliestByFlow = independent
                        ? planningAnchor
                        : (prevEnd.HasValue
                            ? prevEnd.Value.AddMinutes(handoffMinutes)
                            : planningAnchor);

                    if (!independent && raloEnd.HasValue && earliestByFlow < raloEnd.Value)
                        earliestByFlow = raloEnd.Value;

                    var startCandidate = laneFreeAt > earliestByFlow
                        ? laneFreeAt
                        : earliestByFlow;

                    var plannedStart = _cal.NormalizeStart(startCandidate);

                    var setupMinutes = GetSetupMinutes(pcode, ctx);
                    var effectiveCap = GetEffectiveCapacityPerHour(candidate);
                    var durationHours = EstimateStageDurationHours(pcode, requiredUnits, candidate, ctx);

                    var plannedEnd = _cal.AddWorkingHours(plannedStart, durationHours);

                    var draft = new StagePlanDraft
                    {
                        ProcessId = step.process_id,
                        SeqNum = step.seq_num,
                        ProcessName = step.process_name,
                        ProcessCode = pcode,
                        MachineCode = candidate.machine_code,
                        MachineEntity = candidate,
                        LaneIndex = laneIndex,
                        Unit = unit,
                        RequiredUnits = requiredUnits,
                        EffectiveCapacityPerHour = effectiveCap,
                        SetupMinutes = setupMinutes,
                        HandoffMinutes = handoffMinutes,
                        PlannedStart = plannedStart,
                        PlannedEnd = plannedEnd
                    };

                    if (best == null ||
                        draft.PlannedEnd < best.PlannedEnd ||
                        (draft.PlannedEnd == best.PlannedEnd && draft.PlannedStart < best.PlannedStart))
                    {
                        best = draft;
                    }
                }

                if (best == null)
                    throw new Exception($"Cannot build plan for process_code={pcode}");

                var selectedPool = await GetOrBuildPoolStateAsync(best.MachineEntity, planningAnchor, machinePoolCache, ct);
                selectedPool.LaneAvailableAt[best.LaneIndex] =
                    best.PlannedEnd.AddMinutes(GetMachineTurnaroundMinutes(best.ProcessCode));

                result.Add(best);

                if (ProductionFlowHelper.IsRalo(best.ProcessCode))
                    raloEnd = best.PlannedEnd;

                prevEnd = best.PlannedEnd;
                prevCode = best.ProcessCode;
            }

            return new PlanBuildResult
            {
                PlanningAnchor = planningAnchor,
                NormalizedProcessCsv = normalizedCsv,
                Stages = result
            };
        }

        private DateTime ResolveFinishDeadline(DateTime deliveryDate)
        {
            var due = DateTime.SpecifyKind(deliveryDate, DateTimeKind.Unspecified);

            // delivery_date thường là date-only -> map thành cuối ngày giao hàng
            if (due.TimeOfDay == TimeSpan.Zero)
            {
                var cutoffHour = _opt.DeliveryCutoffHour <= 0 ? 17 : _opt.DeliveryCutoffHour;
                due = due.Date.AddHours(cutoffHour);
            }

            if (_opt.DueDateSafetyHours > 0)
                due = due.AddHours(-_opt.DueDateSafetyHours);

            return due;
        }

        private async Task<PlanBuildResult?> FindLatestOnTimePlanAsync(
            PlanningContext ctx,
            List<product_type_process> steps,
            string normalizedCsv,
            DateTime earliestAnchor,
            DateTime finishDeadline,
            PlanBuildResult seedPlan,
            CancellationToken ct)
        {
            if (seedPlan.Stages.Count == 0)
                return seedPlan;

            var seedStart = seedPlan.Stages.Min(x => x.PlannedStart);
            var seedEnd = seedPlan.Stages.Max(x => x.PlannedEnd);
            var totalSpan = seedEnd - seedStart;

            var candidateAnchor = _cal.NormalizeStart(finishDeadline - totalSpan);
            if (candidateAnchor < earliestAnchor)
                candidateAnchor = earliestAnchor;

            var stepMinutes = Math.Max(5, _opt.AnchorSearchStepMinutes);

            var totalMinutes = Math.Max(0, (candidateAnchor - earliestAnchor).TotalMinutes);
            var maxIterations = Math.Max(1, (int)Math.Ceiling(totalMinutes / stepMinutes) + 1);

            var anchor = candidateAnchor;

            for (var i = 0; i < maxIterations; i++)
            {
                var plan = await BuildStagePlansFromAnchorAsync(
                    ctx,
                    anchor,
                    steps,
                    normalizedCsv,
                    ct);

                var finish = plan.Stages.Count == 0
                    ? anchor
                    : plan.Stages.Max(x => x.PlannedEnd);

                if (finish <= finishDeadline)
                    return plan;

                anchor = anchor.AddMinutes(-stepMinutes);
                if (anchor < earliestAnchor)
                    break;
            }

            return null;
        }

        private static bool IsFinished(task t)
        {
            return string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                   || t.end_time != null;
        }
        private sealed class MachinePoolState
        {
            public machine Machine { get; init; } = null!;
            public List<DateTime> LaneAvailableAt { get; init; } = new();
        }
        private async Task<DateTime> ResolveQueueAnchorAsync(
    PlanningContext ctx,
    DateTime proposedAnchor,
    CancellationToken ct)
        {
            if (!_opt.enforce_fifo_by_order_date)
                return proposedAnchor;

            if (!ctx.OrderId.HasValue || ctx.OrderId.Value <= 0)
                return proposedAnchor;

            var currentOrderId = ctx.OrderId.Value;
            var currentOrderDate = DateTime.SpecifyKind(ctx.QueueDateTime, DateTimeKind.Unspecified);

            var olderQueueTails = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id
                where pr.order_id != null
                      && o.order_id != currentOrderId
                      && (
                            o.order_date < currentOrderDate ||
                            (o.order_date == currentOrderDate && o.order_id < currentOrderId)
                         )
                let lastTaskEnd = _db.tasks.AsNoTracking()
                    .Where(t => t.prod_id == pr.prod_id)
                    .Select(t => (DateTime?)t.planned_end_time ?? t.end_time)
                    .OrderByDescending(x => x)
                    .FirstOrDefault()
                select new
                {
                    queue_tail = lastTaskEnd
                                 ?? pr.planned_start_date
                                 ?? pr.actual_start_date
                                 ?? pr.created_at
                }
            ).ToListAsync(ct);

            var maxOlderTail = olderQueueTails
                .Where(x => x.queue_tail.HasValue)
                .Select(x => x.queue_tail!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            if (maxOlderTail == DateTime.MinValue)
                return proposedAnchor;

            var gapMinutes = Math.Max(0, _opt.order_gap_minutes);
            var fifoAnchor = _cal.NormalizeStart(maxOlderTail.AddMinutes(gapMinutes));

            return fifoAnchor > proposedAnchor ? fifoAnchor : proposedAnchor;
        }

        private sealed class SubProductCompletedContext
        {
            public bool is_sub_product_mode { get; init; }
            public int? sub_product_id { get; init; }
            public string? raw_product_process { get; init; }
            public HashSet<string> finished_process_codes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormProcessCode(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static HashSet<string> ParseProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(
                    new[] { ',', ';', '|', '/', '\\' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<SubProductCompletedContext> ResolveSubProductCompletedContextAsync(
            production prod,
            IReadOnlyList<StagePlanDraft> stages,
            PlanningContext ctx,
            DateTime now,
            CancellationToken ct)
        {
            var empty = new SubProductCompletedContext();

            if ((bool)prod.is_full_process)
                return empty;

            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                return empty;

            var sub = await _db.sub_products
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.id == prod.sub_product_id.Value, ct);

            if (sub == null || string.IsNullOrWhiteSpace(sub.product_process))
                return empty;

            var selectedCodes = ParseProcessCodes(sub.product_process);

            if (selectedCodes.Count == 0)
                return empty;

            var orderedStages = stages
                .OrderBy(x => x.SeqNum)
                .ToList();

            var maxCompletedSeq = orderedStages
                .Where(x => selectedCodes.Contains(NormProcessCode(x.ProcessCode)))
                .Select(x => (int?)x.SeqNum)
                .Max();

            if (!maxCompletedSeq.HasValue)
                return empty;

            var finishedCodes = orderedStages
                .Where(x => x.SeqNum <= maxCompletedSeq.Value)
                .Select(x => NormProcessCode(x.ProcessCode))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new SubProductCompletedContext
            {
                is_sub_product_mode = true,
                sub_product_id = sub.id,
                raw_product_process = sub.product_process,
                finished_process_codes = finishedCodes
            };
        }

        private static int ResolveQtyGoodForAutoFinishedTask(
            string? processCode,
            int stageIndex,
            IReadOnlyList<string?> routeProcessCodes,
            PlanningContext ctx)
        {
            var pcode = NormProcessCode(processCode);

            var sheetsTotal = Math.Max(ctx.SheetsTotal, ctx.SheetsRequired);
            if (sheetsTotal <= 0)
                sheetsTotal = Math.Max(1, ctx.OrderQty);

            var nUp = ctx.NUp <= 0 ? 1 : ctx.NUp;
            var numberOfPlates = ctx.NumberOfPlates <= 0 ? 1 : ctx.NumberOfPlates;

            return StageQuantityHelper.GetProductionOutputCap(
                currentCode: pcode,
                currentStageIndex: stageIndex,
                routeProcessCodes: routeProcessCodes,
                sheetsTotal: sheetsTotal,
                nUp: nUp,
                numberOfPlates: numberOfPlates);
        }

        private async Task CreateSubProductFinishedLogsAsync(
            List<task> tasks,
            IReadOnlyList<StagePlanDraft> stages,
            SubProductCompletedContext subProductContext,
            PlanningContext ctx,
            DateTime now,
            CancellationToken ct)
        {
            if (!subProductContext.is_sub_product_mode)
                return;

            var orderedStages = stages
                .OrderBy(x => x.SeqNum)
                .ToList();

            var routeCodes = orderedStages
                .Select(x => (string?)x.ProcessCode)
                .ToList();

            var taskByProcessId = tasks
                .Where(x => x.process_id.HasValue)
                .ToDictionary(x => x.process_id!.Value, x => x);

            for (var i = 0; i < orderedStages.Count; i++)
            {
                var stage = orderedStages[i];
                var pcode = NormProcessCode(stage.ProcessCode);

                if (!subProductContext.finished_process_codes.Contains(pcode))
                    continue;

                if (!taskByProcessId.TryGetValue(stage.ProcessId, out var t))
                    continue;

                var alreadyHasLog = await _db.task_logs
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.task_id == t.task_id &&
                        x.action_type == "Finished",
                        ct);

                if (alreadyHasLog)
                    continue;

                var qtyGood = ResolveQtyGoodForAutoFinishedTask(
                    stage.ProcessCode,
                    i,
                    routeCodes,
                    ctx);

                await _db.task_logs.AddAsync(new task_log
                {
                    task_id = t.task_id,
                    scanned_code = $"SUB_PRODUCT-{subProductContext.sub_product_id}",
                    action_type = "Finished",
                    qty_good = qtyGood,
                    log_time = now,
                    scanned_by_user_id = null,
                    material_usage_json = null
                }, ct);
            }

            await _db.SaveChangesAsync(ct);
        }

        private sealed class StagePlanDraft
        {
            public int ProcessId { get; init; }
            public int SeqNum { get; init; }
            public string ProcessName { get; init; } = "";
            public string ProcessCode { get; init; } = "";
            public string MachineCode { get; init; } = "";
            public machine MachineEntity { get; init; } = null!;
            public int LaneIndex { get; init; }
            public string Unit { get; init; } = "";
            public decimal RequiredUnits { get; init; }
            public decimal EffectiveCapacityPerHour { get; init; }
            public int SetupMinutes { get; init; }
            public int HandoffMinutes { get; init; }
            public DateTime PlannedStart { get; init; }
            public DateTime PlannedEnd { get; init; }
        }

        private sealed class PlanBuildResult
        {
            public DateTime PlanningAnchor { get; init; }
            public string NormalizedProcessCsv { get; init; } = "";
            public List<StagePlanDraft> Stages { get; init; } = new();
        }
    }
}