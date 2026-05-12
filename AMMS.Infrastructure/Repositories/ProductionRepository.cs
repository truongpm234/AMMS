using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Exceptions;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System;
using System.Text.Json;

namespace AMMS.Infrastructure.Repositories
{
    public class ProductionRepository : IProductionRepository
    {
        private readonly AppDbContext _db;
        private readonly ITaskRepository _taskRepo;

        public ProductionRepository(AppDbContext db, ITaskRepository taskRepo)
        {
            _db = db;
            _taskRepo = taskRepo;
        }

        public async Task<DateTime?> GetNearestDeliveryDateAsync()
        {
            return await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id
                where pr.actual_start_date != null
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

        public Task<production?> GetByIdForUpdateAsync(int prodId, CancellationToken ct = default)
        {
            return _db.productions
                .AsTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);
        }

        public async Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(int page, int pageSize, int? roleId, CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            var baseRows = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking()
                    on q.order_request_id equals r.order_request_id into rj
                from r in rj.DefaultIfEmpty()

                where pr.created_at != null && pr.order_id != null
                orderby (pr.planned_start_date ?? pr.created_at) descending, pr.prod_id descending
                select new BaseRow
                {
                    prod_id = pr.prod_id,
                    order_id = o.order_id,
                    code = o.code,
                    delivery_date = o.delivery_date,
                    product_type_id = pr.product_type_id,
                    production_status = pr.status,
                    order_status = o.status,
                    customer_name = !string.IsNullOrWhiteSpace(r.customer_name) ? r.customer_name : "",
                    production_method = pr.prod_method,
                    is_full_process = pr.is_full_process,
                    sub_product_id = pr.sub_product_id,
                    sub_product_used_qty = pr.sub_product_used_qty,
                    nvl_qty = pr.nvl_qty,
                    gm_note = pr.gm_note,
                    mgr_note = pr.mgr_note,
                    first_item_product_name = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => i.product_name)
                        .FirstOrDefault(),

                    first_item_production_process = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => i.production_process)
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

            var taskRows = await _db.tasks.AsNoTracking().Where(t => t.prod_id != null && prodIds.Contains(t.prod_id.Value))
                .Select(t => new TaskRow
                {
                    TaskId = t.task_id,
                    ProdId = t.prod_id!.Value,
                    ProcessId = t.process_id,
                    SeqNum = t.seq_num,
                    Status = t.status,
                    StartTime = t.start_time,
                    EndTime = t.end_time,
                    PlannedStartTime = t.planned_start_time,
                    PlannedEndTime = t.planned_end_time
                })
    .ToListAsync(ct);

            var tasksByProd = taskRows
                .GroupBy(x => x.ProdId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.SeqNum ?? int.MaxValue).ToList()
                );

            var productTypeIds = baseRows
                .Select(x => x.product_type_id)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToList();

            var stepRows = await _db.product_type_processes.AsNoTracking().Where(p => productTypeIds.Contains(p.product_type_id) && (p.is_active ?? true))
    .Select(p => new StepRow
    {
        ProductTypeId = p.product_type_id,
        ProcessId = p.process_id,
        SeqNum = p.seq_num,
        ProcessName = p.process_name,
        ProcessCode = p.process_code
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

                steps = ResolveFixedRoute(
                    steps.OrderBy(s => s.SeqNum).ToList(),
                    x => x.ProcessCode,
                    r.first_item_production_process
                );

                var visibleSteps = ProductionSHelper.FilterStepsByRole(steps, roleId);

                if (visibleSteps.Count == 0)
                    continue;

                var stageStatuses = visibleSteps
                    .Select(step =>
                    {
                        var task = tasks.FirstOrDefault(t => t.ProcessId == step.ProcessId)
                                   ?? tasks.FirstOrDefault(t => t.SeqNum == step.SeqNum);

                        return new ProductionStageStatusDto
                        {
                            task_id = task?.TaskId,
                            process_id = step.ProcessId,
                            seq_num = step.SeqNum,
                            process_code = step.ProcessCode,
                            process_name = step.ProcessName,
                            status = ResolveTaskStageStatus(task),
                            start_time = task?.StartTime,
                            end_time = task?.EndTime,
                            planned_start_time = task?.PlannedStartTime,
                            planned_end_time = task?.PlannedEndTime,
                            is_current = false
                        };
                    })
                    .OrderBy(x => x.seq_num ?? int.MaxValue)
                    .ToList();

                var visibleTasks = tasks
                    .Where(t =>
                        visibleSteps.Any(s =>
                            (t.ProcessId.HasValue && t.ProcessId == s.ProcessId) ||
                            (t.SeqNum.HasValue && t.SeqNum == s.SeqNum)))
                    .OrderBy(t => t.SeqNum ?? int.MaxValue)
                    .ToList();

                var currentSeq = GetCurrentSeq(visibleTasks);

                string? currentStage = null;
                string? currentStageStatus = null;

                if (currentSeq.HasValue)
                {
                    var currentStageItem = stageStatuses
                        .FirstOrDefault(x => x.seq_num == currentSeq.Value);

                    if (currentStageItem != null)
                    {
                        currentStage = currentStageItem.process_name;
                        currentStageStatus = currentStageItem.status;
                        currentStageItem.is_current = true;
                    }
                }
                else if (visibleTasks.Count > 0 && visibleTasks.All(x => x.EndTime != null))
                {
                    var lastStage = stageStatuses
                        .OrderBy(x => x.seq_num ?? int.MaxValue)
                        .LastOrDefault();

                    if (lastStage != null)
                    {
                        currentStage = lastStage.process_name;
                        currentStageStatus = lastStage.status;
                        lastStage.is_current = true;
                    }
                }
                else
                {
                    var firstStage = stageStatuses
                        .OrderBy(x => x.seq_num ?? int.MaxValue)
                        .FirstOrDefault();

                    if (firstStage != null)
                    {
                        currentStage = firstStage.process_name;
                        currentStageStatus = firstStage.status;
                        firstStage.is_current = true;
                    }
                }

                var stages = visibleSteps
                    .Select(s => s.ProcessName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                var progress = ComputeProgressByStages(visibleSteps, currentSeq, visibleTasks);
                var ord = await _db.orders.SingleOrDefaultAsync(o => o.order_id == r.order_id);
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

                    status = r.order_status,
                    production_status = r.production_status,
                    is_production_ready = ord.is_production_ready,
                    production_method = r.production_method,
                    is_full_process = r.is_full_process,
                    sub_product_id = r.sub_product_id,
                    sub_product_used_qty = r.sub_product_used_qty,
                    nvl_qty = r.nvl_qty,
                    gm_note = r.gm_note,
                    mgr_note = r.mgr_note,

                    stage_status = currentStageStatus,
                    stages = stages,
                    stage_statuses = stageStatuses
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

        public async Task<ProductionProgressResponse> GetProgressAsync(int prodId)
        {
            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.status })
                .ToListAsync();

            var total = tasks.Count;
            if (total <= 0)
                return new ProductionProgressResponse
                {
                    prod_id = prodId,
                    total_steps = 0,
                    finished_steps = 0,
                    progress_percent = 0
                };

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var finishedTaskIds = await _db.task_logs
                .AsNoTracking()
                .Where(l => l.task_id != null
                    && taskIds.Contains(l.task_id.Value)
                    && l.action_type == "Finished")
                .Select(l => l.task_id!.Value)
                .Distinct()
                .ToListAsync();

            var finished = tasks.Count(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                && finishedTaskIds.Contains(t.task_id));

            var percent = Math.Round((finished * 100m) / total, 1);

            return new ProductionProgressResponse
            {
                prod_id = prodId,
                total_steps = total,
                finished_steps = finished,
                progress_percent = percent
            };
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var header = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id into oj
                from o in oj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking() on o.order_id equals r.order_id into rj
                from r in rj.DefaultIfEmpty()

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join pt in _db.product_types.AsNoTracking() on pr.product_type_id equals pt.product_type_id into ptj
                from pt in ptj.DefaultIfEmpty()

                join sp in _db.sub_products.AsNoTracking() on pr.sub_product_id equals sp.id into spj
                from sp in spj.DefaultIfEmpty()

                where pr.order_id == orderId
                orderby (pr.planned_start_date ?? pr.created_at ?? pr.end_date)
                select new
                {
                    pr,
                    sp,
                    o,
                    product_type_name = pt != null ? pt.name : null,
                    packaging_standard = pt != null ? pt.packaging_standard : null,
                    customer_name = !string.IsNullOrWhiteSpace(r.customer_name) ? r.customer_name : "Khách hàng",
                    first_item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.item_id,
                            i.product_name,
                            i.quantity,
                            i.production_process,
                            i_length = (int?)EF.Property<int?>(i, "length_mm"),
                            i_width = (int?)EF.Property<int?>(i, "width_mm"),
                            i_height = (int?)EF.Property<int?>(i, "height_mm"),
                            i_ink_weight_kg = (decimal?)i.est_ink_weight_kg
                        })
                        .FirstOrDefault()
                }).FirstOrDefaultAsync(ct);

            if (header == null)
                return null;

            var dto = new ProductionDetailDto
            {
                prod_id = header.pr.prod_id,
                import_recieve_path = header.pr.import_recieve_path,
                production_code = header.pr.code,
                production_status = header.pr.status,
                created_at = header.pr.created_at,
                planned_start_date = header.pr.planned_start_date,
                actual_start_date = header.pr.actual_start_date,
                end_date = header.pr.end_date,
                order_id = header.o?.order_id,
                order_code = header.o?.code,
                delivery_date = header.o?.delivery_date,
                customer_name = header.customer_name ?? "Khách ẩn tên",
                product_name = header.first_item?.product_name,
                quantity = header.first_item?.quantity ?? 0,
                product_type_id = header.pr.product_type_id,
                packaging_standard = header.packaging_standard,
                length_mm = header.first_item?.i_length,
                width_mm = header.first_item?.i_width,
                height_mm = header.first_item?.i_height,
                production_method = header.pr.prod_method,
                sub_product_id = header.pr.sub_product_id,
                sub_product_used_qty = header.pr.sub_product_used_qty,
                nvl_qty = header.pr.nvl_qty,
                sub_product_process = header.sp != null ? header.sp.product_process : null,
                is_full_process = header.pr.is_full_process,
            };

            order_request? orderReq = null;
            cost_estimate? estimate = null;

            int sheetsRequired = 0;
            int sheetsWaste = 0;
            int sheetsTotal = 0;
            int nUp = 1;

            if (dto.order_id.HasValue)
            {
                orderReq = await _db.order_requests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == dto.order_id.Value, ct);

                if (orderReq != null)
                {
                    if (orderReq.accepted_estimate_id.HasValue && orderReq.accepted_estimate_id.Value > 0)
                    {
                        estimate = await _db.cost_estimates
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                x => x.estimate_id == orderReq.accepted_estimate_id.Value
                                  && x.order_request_id == orderReq.order_request_id,
                                ct);
                    }

                    estimate ??= await _db.cost_estimates
                        .AsNoTracking()
                        .Where(x => x.order_request_id == orderReq.order_request_id)
                        .OrderByDescending(x => x.is_active)
                        .ThenByDescending(x => x.estimate_id)
                        .FirstOrDefaultAsync(ct);

                    if (estimate != null)
                    {
                        sheetsRequired = estimate.sheets_required;
                        sheetsWaste = estimate.sheets_waste;
                        sheetsTotal = estimate.sheets_total;
                        nUp = estimate.n_up > 0 ? estimate.n_up : 1;
                    }
                    dto.n_up = nUp;
                }
            }

            dto.ready_print_file = orderReq?.print_ready_file;
            dto.ink_type_names = estimate?.ink_type_names;
            dto.wave_type = estimate?.wave_type;
            dto.paper_name = estimate?.paper_name;
            dto.coating_type = estimate?.coating_type;
            dto.paper_alternative = estimate?.paper_alternative;
            dto.wave_alternative = estimate?.wave_alternative;
            dto.lamination_material_id = estimate?.lamination_material_id;
            dto.lamination_material_code = estimate?.lamination_material_code;
            dto.lamination_material_name = estimate?.lamination_material_name;
            decimal estInkWeightKg = 0m;
            if (header.first_item?.i_ink_weight_kg.HasValue == true)
                estInkWeightKg = header.first_item.i_ink_weight_kg.Value;

            int? numberOfPlates = orderReq?.number_of_plates;
            decimal estCoatingGlueWeightKg = estimate?.coating_glue_weight_kg ?? 0m;

            material? coatingMaterial = null;

            if (estimate != null &&
                estCoatingGlueWeightKg > 0m &&
                !IsNoCoatingType(estimate.coating_type))
            {
                coatingMaterial = await ResolveCoatingMaterialForDetailAsync(estimate, ct);
            }

            var coatingMaterialCode = coatingMaterial?.code
                ?? ResolveCoatingMaterialCodeForDetail(estimate?.coating_type);

            var coatingMaterialName = coatingMaterial?.name
                ?? (!IsNoCoatingType(estimate?.coating_type)
                    ? ProductionFlowHelper.ResolveCoatingDisplayName(estimate?.coating_type)
                    : null);

            var coatingMaterialUnit = coatingMaterial?.unit ?? "kg";
            var prodId = header.pr.prod_id;

            var tasks = await _db.tasks.AsNoTracking()
    .Where(t => t.prod_id == prodId)
    .Select(t => new
    {
        t.task_id,
        t.prod_id,
        t.seq_num,
        t.name,
        t.status,
        t.machine,
        t.start_time,
        t.end_time,
        t.planned_start_time,
        t.planned_end_time,
        t.process_id,
        t.is_taken_sub_product
    })
    .ToListAsync(ct);

            var lastTask = tasks
                .OrderByDescending(t => t.seq_num ?? 0)
                .ThenByDescending(t => t.task_id)
                .FirstOrDefault();

            if (lastTask != null
    && string.Equals(lastTask.status, "Finished", StringComparison.OrdinalIgnoreCase)
    && lastTask.end_time != null
    && !string.Equals(header.pr.status, "Importing", StringComparison.OrdinalIgnoreCase))
            {
                var prodToUpdate = new production { prod_id = prodId };
                _db.productions.Attach(prodToUpdate);

                prodToUpdate.status = "Importing";
                prodToUpdate.end_date = lastTask.end_time;

                if (header.pr.actual_start_date == null)
                    prodToUpdate.actual_start_date = lastTask.end_time;

                await _db.SaveChangesAsync(ct);

                dto.production_status = "Importing";
                dto.end_date = lastTask.end_time;
                dto.actual_start_date ??= lastTask.end_time;
            }

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                 .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                 .OrderBy(l => l.log_time)
                 .Select(l => new TaskLogDto
                 {
                     log_id = l.log_id,
                     task_id = l.task_id!.Value,
                     action_type = l.action_type,
                     qty_good = l.qty_good ?? 0,
                     log_time = l.log_time,
                     scanned_code = l.scanned_code,
                     scanned_by_user_id = l.scanned_by_user_id,
                     reason = l.reason,
                     report_image_url = l.report_image_url,

                     material_usage_json = l.material_usage_json
                 })
                 .ToListAsync(ct);

            foreach (var log in logs)
            {
                log.report_image_urls = SplitImageUrls(log.report_image_url);

                if (!string.IsNullOrWhiteSpace(log.material_usage_json))
                {
                    try
                    {
                        log.material_usages = JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(
                            log.material_usage_json) ?? new List<TaskMaterialUsageLogItemDto>();
                    }
                    catch
                    {
                        log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                    }
                }
                else
                {
                    log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                }
            }

            var ptId = header.pr.product_type_id;
            List<ProductTypeProcessStepDto> steps = new();

            if (ptId.HasValue)
            {
                steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .OrderBy(p => p.seq_num)
                    .Select(p => new ProductTypeProcessStepDto
                    {
                        process_id = p.process_id,
                        seq_num = p.seq_num,
                        process_name = p.process_name,
                        process_code = EF.Property<string?>(p, "process_code"),
                        machine = p.machine
                    })
                    .ToListAsync(ct);
            }

            steps = ResolveFixedRoute(
                steps.OrderBy(x => x.seq_num).ToList(),
                x => x.process_code,
                header.first_item?.production_process
            );

            var stages = new List<ProductionStageDto>();
            StageOutputRef? prevOutput = null;
            var routeCodes = steps.Select(x => x.process_code).ToList();

            for (var stageIndex = 0; stageIndex < steps.Count; stageIndex++)
            {
                var s = steps[stageIndex];
                var pcode = (s.process_code ?? "").Trim().ToUpperInvariant();

                var task = tasks.FirstOrDefault(t => t.process_id == s.process_id)
                           ?? tasks.FirstOrDefault(t => (t.seq_num ?? -1) == s.seq_num);

                var stageLogs = task == null
                    ? new List<TaskLogDto>()
                    : LogsByTaskId(logs, task.task_id);

                var qtyGood = stageLogs.Sum(x => x.qty_good);
                var qtyBad = 0;
                var denom = qtyGood + qtyBad;
                var wastePct = denom <= 0 ? 0m : Math.Round((qtyBad * 100m) / denom, 2);

                var io = BuildStageIO(
    processCode: pcode,
    processName: s.process_name ?? "",
    detail: dto,
    prevOutput: prevOutput,
    sheetsRequired: sheetsRequired,
    sheetsWaste: sheetsWaste,
    sheetsTotal: sheetsTotal,
    nUp: nUp,
    qtyGood: qtyGood,
    numberOfPlates: numberOfPlates,
    estInkWeightKg: estInkWeightKg,
    currentStageIndex: stageIndex,
    routeProcessCodes: routeCodes,
    paperCode: estimate?.paper_code,
    paperName: estimate?.paper_name,
    waveType: estimate?.wave_type,
    coatingType: estimate?.coating_type,
    coatingMaterialCode: coatingMaterialCode,
    coatingMaterialName: coatingMaterialName,
    coatingMaterialUnit: coatingMaterialUnit,
    estCoatingGlueWeightKg: estCoatingGlueWeightKg,
    inkTypeNames: estimate?.ink_type_names,
    laminationMaterialCode: estimate?.lamination_material_code,
    laminationMaterialName: estimate?.lamination_material_name,
    estLaminationWeightKg: estimate?.lamination_weight_kg ?? 0m
);

                var stage = new ProductionStageDto
                {
                    process_id = s.process_id,
                    seq_num = s.seq_num,
                    process_name = s.process_name ?? "",
                    process_code = s.process_code,
                    machine = task?.machine ?? s.machine,
                    task_id = task?.task_id,
                    task_name = task?.name,
                    status = task?.status,
                    start_time = task?.start_time,
                    end_time = task?.end_time,
                    qty_good = qtyGood,
                    waste_percent = wastePct,
                    last_scan_time = stageLogs.Count == 0 ? null : stageLogs.Max(x => x.log_time),
                    logs = stageLogs,
                    input_materials = io.inputs,
                    output_product = io.output,
                    planned_start_time = task?.planned_start_time,
                    planned_end_time = task?.planned_end_time,
                    estimated_output_quantity = io.output.estimated_quantity,
                    actual_output_quantity = io.output.actual_quantity,
                    n_up = nUp,
                    is_taken_sub_product = task?.is_taken_sub_product ?? false
                };

                stages.Add(stage);
                prevOutput = io.nextOutput;
            }

            dto.stages = stages;
            return dto;
        }

        private static List<string> SplitImageUrls(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default)
        {
            var prod = await _db.productions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null) return null;

            var tasks = await _db.tasks.AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.seq_num, t.process_id, t.name })
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .Select(l => new
                {
                    task_id = l.task_id!.Value,
                    qty_good = l.qty_good ?? 0,
                    log_time = l.log_time
                })
                .ToListAsync(ct);

            var ptId = prod.product_type_id;
            var stepMeta = new Dictionary<int, (string? code, string name, int seq)>();

            if (ptId.HasValue)
            {
                var steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .Select(p => new
                    {
                        p.process_id,
                        p.process_name,
                        p.seq_num,
                        process_code = (string?)EF.Property<string?>(p, "process_code")
                    })
                    .ToListAsync(ct);

                stepMeta = steps.ToDictionary(
                    x => x.process_id,
                    x => (x.process_code, x.process_name ?? "", x.seq_num)
                );
            }

            var stageRows = new List<StageWasteDto>();

            foreach (var t in tasks.OrderBy(x => x.seq_num ?? int.MaxValue))
            {
                var tlogs = logs.Where(x => x.task_id == t.task_id).ToList();
                var good = tlogs.Sum(x => x.qty_good);

                string pname = t.name;
                string? pcode = null;
                var seq = t.seq_num ?? 0;

                if (t.process_id.HasValue && stepMeta.TryGetValue(t.process_id.Value, out var meta))
                {
                    pname = meta.name;
                    pcode = meta.code;
                    seq = meta.seq;
                }

                stageRows.Add(new StageWasteDto
                {
                    task_id = t.task_id,
                    seq_num = seq,
                    process_name = pname,
                    process_code = pcode,
                    qty_good = good,
                    first_scan = tlogs.Count == 0 ? null : tlogs.Min(x => x.log_time),
                    last_scan = tlogs.Count == 0 ? null : tlogs.Max(x => x.log_time),
                });
            }

            var totalGood = stageRows.Sum(x => (decimal)x.qty_good);
            var totalDenom = totalGood;

            return new ProductionWasteReportDto
            {
                prod_id = prodId,
                total_good = totalGood,
                stages = stageRows.OrderBy(x => x.seq_num).ToList()
            };
        }

        public async Task<int?> StartProductionByOrderIdOnlyAsync(int orderId, DateTime now, CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var prod = await _db.productions
                    .FirstOrDefaultAsync(p => p.order_id == orderId, ct);

                if (prod == null)
                    return (int?)null;

                var bomIssues = await GetBomMissingMaterialMappingsAsync(orderId, ct);
                if (bomIssues.Count > 0)
                    throw new BomValidationException(bomIssues);

                await ConsumeMaterialsOnProductionStartAsync(prod, now, ct);

                prod.status = "InProcessing";
                prod.actual_start_date ??= now;

                if (prod.created_at == null)
                    prod.created_at = now;

                if (prod.planned_start_date == null)
                    prod.planned_start_date = now;

                if (prod.order_id.HasValue)
                {
                    var order = await _db.orders
                        .FirstOrDefaultAsync(o => o.order_id == prod.order_id.Value, ct);

                    if (order != null)
                        order.status = "InProcessing";
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return prod.prod_id;
            });
        }

        public async Task<bool> StartProductionByOrderIdAsync(int orderId, DateTime now, CancellationToken ct = default)
        {
            var prodId = await StartProductionByOrderIdOnlyAsync(orderId, now, ct);
            return prodId.HasValue;
        }

        public async Task<bool> TryCloseProductionIfCompletedAsync(int prodId, DateTime now, CancellationToken ct = default)
        {
            var prod = await _db.productions.FirstOrDefaultAsync(p => p.prod_id == prodId, ct);
            if (prod == null) return false;

            var tasks = await _db.tasks
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.status, t.end_time, t.seq_num })
                .ToListAsync(ct);

            if (tasks.Count == 0) return false;

            bool allFinished = tasks.All(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                || t.end_time != null);

            if (!allFinished) return false;

            var finishedAt = tasks.Where(t => t.end_time != null)
                .Select(t => t.end_time!.Value)
                .DefaultIfEmpty(now)
                .Max();

            prod.end_date = finishedAt;
            prod.status = "Importing";
            if (prod.actual_start_date == null)
                prod.actual_start_date = finishedAt;

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> SetProductionDeliveryByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(p => p.order_id == orderId, ct);

            if (prod == null)
                return false;

            var order = await _db.orders
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null)
                return false;

            var request = await _db.order_requests
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (request == null)
                return false;

            var now = AppTime.NowVnUnspecified();

            prod.status = "Delivery";
            order.status = "Delivery";
            order.confirmed_delivery_at = now;
            request.process_status = "Delivery";

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> SetCompletedByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(p => p.order_id == orderId, ct);

            if (prod == null)
                return false;

            var order = await _db.orders
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null)
                return false;

            var request = await _db.order_requests
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (request == null)
                return false;

            prod.status = "Completed";
            order.status = "Completed";
            request.process_status = "Completed";

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);

        private static string NormalizeProcessCode(string? code)
            => (code ?? "").Trim().ToUpperInvariant();

        private static HashSet<string> ParseSelectedProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeProcessCode(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<T> ResolveFixedRoute<T>(
    List<T> allSteps,
    Func<T, string?> processCodeSelector,
    string? selectedProcessesCsv)
        {
            if (allSteps == null || allSteps.Count == 0)
                return new List<T>();

            var selected = ParseSelectedProcessCodes(selectedProcessesCsv);
            if (selected.Count == 0)
                return allSteps;

            var filtered = allSteps
                .Where(x => selected.Contains(NormalizeProcessCode(processCodeSelector(x))))
                .ToList();

            return filtered.Count > 0 ? filtered : allSteps;
        }

        private static (List<StageMaterialDto> inputs, StageMaterialDto output, StageOutputRef nextOutput) BuildStageIO(
    string processCode,
    string processName,
    ProductionDetailDto detail,
    StageOutputRef? prevOutput,
    int sheetsRequired,
    int sheetsWaste,
    int sheetsTotal,
    int nUp,
    int qtyGood,
    int? numberOfPlates,
    decimal estInkWeightKg,
    int currentStageIndex,
    IReadOnlyList<string?> routeProcessCodes,
    string? paperCode,
    string? paperName,
    string? waveType,
    string? coatingType,
    string? coatingMaterialCode,
    string? coatingMaterialName,
    string? coatingMaterialUnit,
    decimal estCoatingGlueWeightKg,
    string? inkTypeNames,
    string? laminationMaterialCode,
    string? laminationMaterialName,
    decimal estLaminationWeightKg)
        {
            var inputs = new List<StageMaterialDto>();

            var code = (processCode ?? "").Trim().ToUpperInvariant();
            var productName = string.IsNullOrWhiteSpace(detail.product_name)
                ? "sản phẩm"
                : detail.product_name.Trim();

            sheetsRequired = Math.Max(0, sheetsRequired);
            sheetsWaste = Math.Max(0, sheetsWaste);
            sheetsTotal = Math.Max(sheetsTotal, Math.Max(sheetsRequired + sheetsWaste, 1));
            nUp = Math.Max(nUp, 1);

            var plateQty = Math.Max(1, numberOfPlates ?? 1);
            var sheetQty = Math.Max(1, sheetsTotal);
            var productionOutputQty = StageQuantityHelper.GetProductCap(sheetQty, nUp);

            var qtyContext = ResolveBothStageQuantityContext(
                detail,
                processCode,
                currentStageIndex,
                routeProcessCodes,
                sheetQty,
                productionOutputQty);

            sheetQty = qtyContext.stage_sheet_qty;
            productionOutputQty = qtyContext.stage_output_qty;

            var resolvedPaperCode = string.IsNullOrWhiteSpace(paperCode)
                ? "PAPER"
                : paperCode.Trim();

            var resolvedPaperName = string.IsNullOrWhiteSpace(paperName)
                ? "Giấy in"
                : paperName.Trim();

            static decimal? ActualFromQtyGood(int qtyGood, decimal cap)
            {
                if (qtyGood <= 0)
                    return null;

                return Math.Min(qtyGood, cap);
            }

            static decimal? ActualFromPrevious(StageOutputRef? prevOutput, decimal cap)
            {
                if (prevOutput?.ActualQuantity == null)
                    return null;

                return Math.Min(prevOutput.ActualQuantity.Value, cap);
            }

            void AddMainInputFromPrevious(
                string fallbackName,
                string fallbackCode,
                decimal estimatedQty,
                string unit)
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: !string.IsNullOrWhiteSpace(prevOutput?.Name)
                        ? prevOutput!.Name
                        : fallbackName,
                    code: !string.IsNullOrWhiteSpace(prevOutput?.Code)
                        ? prevOutput!.Code
                        : fallbackCode,
                    estimatedQty: estimatedQty,
                    actualQty: ActualFromPrevious(prevOutput, estimatedQty),
                    unit: unit));
            }

            // =========================
            // RALO
            // input = số bản kẽm
            // output = số bản kẽm
            // =========================
            if (code == "RALO" || code == "RA_LO")
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: "Bản kẽm cần ralo",
                    code: "PLATE",
                    estimatedQty: plateQty,
                    actualQty: null,
                    unit: "bản"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bản kẽm đã ralo",
                    code: "RALO",
                    estimatedQty: plateQty,
                    actualQty: ActualFromQtyGood(qtyGood, plateQty),
                    unit: "bản");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, plateQty, output.actual_quantity));
            }

            // =========================
            // CAT
            // input = sheets_total
            // output = sheets_total * n_up
            // unit = tờ
            // =========================
            if (code == "CAT" || code == "CUT")
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: resolvedPaperName,
                    code: resolvedPaperCode,
                    estimatedQty: sheetQty,
                    actualQty: null,
                    unit: "tờ"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm sau cắt",
                    code: "CAT",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // IN
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "IN")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm sau cắt",
                    fallbackCode: "CAT",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                if (!string.IsNullOrWhiteSpace(inkTypeNames))
                {
                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: $"Mực in ({inkTypeNames.Trim()})",
                        code: "INK_TYPES",
                        estimatedQty: estInkWeightKg > 0 ? estInkWeightKg : 0m,
                        actualQty: null,
                        unit: "kg"));
                }

                if ((numberOfPlates ?? 0) > 0)
                {
                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: "Bản kẽm in",
                        code: "PLATE",
                        estimatedQty: numberOfPlates.Value,
                        actualQty: null,
                        unit: "bản"));
                }

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm in",
                    code: "IN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // PHU
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "PHU")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm in",
                    fallbackCode: "IN",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                var hasRealCoating =
                    estCoatingGlueWeightKg > 0m &&
                    !IsNoCoatingType(coatingType);

                if (hasRealCoating)
                {
                    var resolvedCode = !string.IsNullOrWhiteSpace(coatingMaterialCode)
                        ? coatingMaterialCode.Trim()
                        : ResolveCoatingMaterialCodeForDetail(coatingType) ?? "COATING";

                    var resolvedName = !string.IsNullOrWhiteSpace(coatingMaterialName)
                        ? coatingMaterialName.Trim()
                        : ProductionFlowHelper.ResolveCoatingDisplayName(coatingType);

                    var resolvedUnit = !string.IsNullOrWhiteSpace(coatingMaterialUnit)
                        ? coatingMaterialUnit.Trim()
                        : "kg";

                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: resolvedName,
                        code: resolvedCode,
                        estimatedQty: estCoatingGlueWeightKg,
                        actualQty: null,
                        unit: resolvedUnit));
                }

                var output = ProductionSHelper.BuildStageMaterial(
                    name: hasRealCoating ? "Bán thành phẩm phủ" : "Bán thành phẩm qua công đoạn phủ",
                    code: "PHU",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // CAN
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "CAN" || code == "CAN_MANG")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm phủ",
                    fallbackCode: "PHU",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                var resolvedLaminationCode = string.IsNullOrWhiteSpace(laminationMaterialCode)
                    ? "LAMINATION"
                    : laminationMaterialCode.Trim();

                var resolvedLaminationName = string.IsNullOrWhiteSpace(laminationMaterialName)
                    ? "Màng cán"
                    : laminationMaterialName.Trim();

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: resolvedLaminationName,
                    code: resolvedLaminationCode,
                    estimatedQty: estLaminationWeightKg > 0 ? estLaminationWeightKg : 0m,
                    actualQty: null,
                    unit: "kg"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã cán",
                    code: "CAN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // BOI
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "BOI")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước bồi",
                    fallbackCode: "PREV",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: string.IsNullOrWhiteSpace(waveType) ? "Sóng carton" : waveType.Trim(),
                    code: string.IsNullOrWhiteSpace(waveType) ? "WAVE" : waveType.Trim(),
                    estimatedQty: 0m,
                    actualQty: null,
                    unit: "tờ"));

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: "Keo bồi",
                    code: "KEO_BOI",
                    estimatedQty: 0m,
                    actualQty: null,
                    unit: "kg"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã bồi",
                    code: "BOI",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // BE
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "BE")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước bế",
                    fallbackCode: "PREV",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã bế",
                    code: "BE",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // DUT
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "DUT")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước dứt",
                    fallbackCode: "BE",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: $"Bán thành phẩm đã dứt {productName}",
                    code: "DUT",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // DAN
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "DAN")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước dán",
                    fallbackCode: "DUT",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: $"Thành phẩm hoàn chỉnh {productName}",
                    code: "DAN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // Fallback
            AddMainInputFromPrevious(
                fallbackName: $"Bán thành phẩm trước {processName}",
                fallbackCode: "PREV",
                estimatedQty: productionOutputQty,
                unit: "tờ");

            var fallbackOutput = ProductionSHelper.BuildStageMaterial(
                name: $"Bán thành phẩm sau {processName}",
                code: processCode,
                estimatedQty: productionOutputQty,
                actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                unit: "tờ");

            return (
                inputs,
                fallbackOutput,
                BuildNextOutputRef(fallbackOutput, productionOutputQty, fallbackOutput.actual_quantity));
        }

        private static string ResolveStageOutputUnit(StageQtyMode mode)
        {
            return mode switch
            {
                StageQtyMode.Plate => "bản",
                StageQtyMode.Product => "sp",
                _ => "tờ"
            };
        }

        private static decimal ResolveStageEstimatedOutputQty(
            StageQtyMode mode,
            StageOutputRef? prevOutput,
            int sheetsTotal,
            int nUp,
            int? numberOfPlates)
        {
            decimal cap = mode switch
            {
                StageQtyMode.Plate => StageQuantityHelper.GetPlateCap(numberOfPlates ?? 1),
                StageQtyMode.Product => StageQuantityHelper.GetProductCap(sheetsTotal, nUp),
                _ => StageQuantityHelper.GetSheetCap(sheetsTotal)
            };

            if (prevOutput == null)
                return cap;

            if (mode == StageQtyMode.Plate && IsSameUnit(prevOutput.Unit, "bản"))
                return Math.Min(cap, prevOutput.EstimatedQuantity);

            if (mode == StageQtyMode.Sheet && IsSameUnit(prevOutput.Unit, "tờ"))
                return Math.Min(cap, prevOutput.EstimatedQuantity);

            if (mode == StageQtyMode.Product && IsSameUnit(prevOutput.Unit, "sp"))
                return Math.Min(cap, prevOutput.EstimatedQuantity);

            return cap;
        }

        private static decimal? ResolveStageActualOutputQty(
            StageQtyMode mode,
            StageOutputRef? prevOutput,
            int qtyGood,
            int sheetsTotal,
            int nUp,
            int? numberOfPlates)
        {
            var actual = ProductionSHelper.ToActualQty(qtyGood);
            if (!actual.HasValue)
                return null;

            decimal cap = mode switch
            {
                StageQtyMode.Plate => StageQuantityHelper.GetPlateCap(numberOfPlates ?? 1),
                StageQtyMode.Product => StageQuantityHelper.GetProductCap(sheetsTotal, nUp),
                _ => StageQuantityHelper.GetSheetCap(sheetsTotal)
            };

            var result = Math.Min(actual.Value, cap);

            if (prevOutput == null || !prevOutput.ActualQuantity.HasValue)
                return result;

            if (mode == StageQtyMode.Plate && IsSameUnit(prevOutput.Unit, "bản"))
                return Math.Min(result, prevOutput.ActualQuantity.Value);

            if (mode == StageQtyMode.Sheet && IsSameUnit(prevOutput.Unit, "tờ"))
                return Math.Min(result, prevOutput.ActualQuantity.Value);

            if (mode == StageQtyMode.Product && IsSameUnit(prevOutput.Unit, "sp"))
                return Math.Min(result, prevOutput.ActualQuantity.Value);

            return result;
        }

        private static bool IsSameUnit(string? source, string target)
        {
            return string.Equals(
                (source ?? "").Trim(),
                target,
                StringComparison.OrdinalIgnoreCase);
        }

        private static StageOutputRef BuildNextOutputRef(
            StageMaterialDto output,
            decimal estimatedQty,
            decimal? actualQty)
        {
            return new StageOutputRef
            {
                Name = output.name ?? "",
                Code = output.code,
                Unit = output.unit,
                EstimatedQuantity = estimatedQty,
                ActualQuantity = actualQty
            };
        }

        private static List<TaskLogDto> LogsByTaskId(List<TaskLogDto> all, int taskId)
        {
            return all
                .Where(x => x.task_id == taskId)
                .OrderBy(x => x.log_time)
                .ToList();
        }

        private static int? GetCurrentSeq(List<TaskRow> tasks)
        {
            var inProg = tasks.FirstOrDefault(x => x.StartTime != null && x.EndTime == null);
            if (inProg?.SeqNum != null) return inProg.SeqNum;

            var next = tasks.FirstOrDefault(x => x.EndTime == null);
            if (next?.SeqNum != null) return next.SeqNum;

            return null;
        }

        private static decimal ComputeProgressByStages(
            List<StepRow> steps,
            int? currentSeq,
            List<TaskRow> tasks)
        {
            var total = steps.Count;
            if (total <= 0) return 0m;

            if (tasks.Count > 0 && tasks.All(x => x.EndTime != null))
                return 100m;

            if (!currentSeq.HasValue) return 0m;

            var idx = steps.FindIndex(s => s.SeqNum == currentSeq.Value);
            if (idx < 0) idx = 0;

            var completedBefore = idx;
            var percent = completedBefore * 100m / total;
            return Math.Round(percent, 1);
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        private static string ResolveTaskStageStatus(TaskRow? task)
        {
            if (task == null)
                return "Unassigned";

            if (!string.IsNullOrWhiteSpace(task.Status))
                return task.Status!;

            if (task.EndTime != null)
                return "Finished";

            if (task.StartTime != null)
                return "InProcessing";

            return "Unassigned";
        }

        public async Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(
    DateTime from,
    DateTime to,
    CancellationToken ct = default)
        {
            if (to <= from)
                to = from.AddDays(1);

            var machines = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.process_code)
                .ThenBy(x => x.machine_code)
                .Select(x => new
                {
                    x.machine_code,
                    x.process_code,
                    x.process_name,
                    x.quantity,
                    busy_quantity = x.busy_quantity ?? 0,
                    free_quantity = x.free_quantity ?? (x.quantity - (x.busy_quantity ?? 0))
                })
                .ToListAsync(ct);

            var rawRows = await _db.tasks
                .AsNoTracking()
                .Where(t => t.machine != null && t.machine != "")
                .Select(t => new
                {
                    TaskId = t.task_id,
                    ProdId = t.prod_id,
                    ProcessId = t.process_id,
                    SeqNum = t.seq_num,
                    Status = t.status,
                    MachineCode = t.machine,
                    PlannedStart = t.planned_start_time,
                    PlannedEnd = t.planned_end_time,
                    ActualStart = t.start_time,
                    ActualEnd = t.end_time,

                    OrderId = _db.productions
                        .Where(pr => pr.prod_id == t.prod_id)
                        .Select(pr => pr.order_id)
                        .FirstOrDefault(),

                    OrderCode = (
                        from pr in _db.productions
                        join o in _db.orders on pr.order_id equals o.order_id
                        where pr.prod_id == t.prod_id
                        select o.code
                    ).FirstOrDefault(),

                    ProcessCode = _db.product_type_processes
                        .Where(p => p.process_id == t.process_id)
                        .Select(p => p.process_code)
                        .FirstOrDefault(),

                    ProcessName = _db.product_type_processes
                        .Where(p => p.process_id == t.process_id)
                        .Select(p => p.process_name)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            var rawSlots = rawRows
                .Select(x =>
                {
                    var start = x.PlannedStart ?? x.ActualStart;
                    var end = x.PlannedEnd
                              ?? x.ActualEnd
                              ?? (start.HasValue ? start.Value.AddHours(1) : (DateTime?)null);

                    return new
                    {
                        x.TaskId,
                        x.ProdId,
                        x.OrderId,
                        x.OrderCode,
                        x.ProcessId,
                        x.ProcessCode,
                        x.ProcessName,
                        x.SeqNum,
                        x.Status,
                        x.MachineCode,
                        Start = start,
                        End = end,
                        x.ActualStart,
                        x.ActualEnd
                    };
                })
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.MachineCode) &&
                    x.Start.HasValue &&
                    x.End.HasValue &&
                    x.Start.Value < to &&
                    x.End.Value > from)
                .Select(x => new MachineScheduleTaskDto
                {
                    task_id = x.TaskId,
                    prod_id = x.ProdId,
                    order_id = x.OrderId,
                    order_code = x.OrderCode,

                    process_id = x.ProcessId,
                    process_code = x.ProcessCode,
                    process_name = !string.IsNullOrWhiteSpace(x.ProcessName) ? x.ProcessName : null,

                    seq_num = x.SeqNum,
                    status = x.Status,

                    machine_code = x.MachineCode!,
                    lane_no = 0,

                    planned_start_time = x.Start,
                    planned_end_time = x.End,

                    actual_start_time = x.ActualStart,
                    actual_end_time = x.ActualEnd
                })
                .OrderBy(x => x.machine_code)
                .ThenBy(x => x.planned_start_time)
                .ThenBy(x => x.planned_end_time)
                .ThenBy(x => x.task_id)
                .ToList();

            var slotsByMachine = rawSlots
                .GroupBy(x => x.machine_code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new List<MachineScheduleBoardDto>();

            foreach (var m in machines)
            {
                slotsByMachine.TryGetValue(m.machine_code, out var slots);
                slots ??= new List<MachineScheduleTaskDto>();

                AssignLaneNumbers(slots, Math.Max(1, m.quantity), from);

                result.Add(new MachineScheduleBoardDto
                {
                    machine_code = m.machine_code,
                    process_code = m.process_code,
                    process_name = m.process_name,
                    quantity = m.quantity,
                    busy_quantity = m.busy_quantity,
                    free_quantity = m.free_quantity,
                    from_time = from,
                    to_time = to,
                    slots = slots
                });
            }

            return result;
        }

        private static void AssignLaneNumbers(
            List<MachineScheduleTaskDto> slots,
            int laneCount,
            DateTime anchor)
        {
            if (slots == null || slots.Count == 0)
                return;

            var laneAvailableAt = Enumerable.Repeat(anchor, laneCount).ToArray();

            foreach (var s in slots
                         .OrderBy(x => x.planned_start_time)
                         .ThenBy(x => x.planned_end_time)
                         .ThenBy(x => x.task_id))
            {
                var start = s.planned_start_time ?? anchor;
                var end = s.planned_end_time ?? start.AddHours(1);

                var bestLane = 0;
                var bestAvailable = laneAvailableAt[0];

                for (var i = 0; i < laneAvailableAt.Length; i++)
                {
                    if (laneAvailableAt[i] <= start)
                    {
                        bestLane = i;
                        bestAvailable = laneAvailableAt[i];
                        break;
                    }

                    if (laneAvailableAt[i] < bestAvailable)
                    {
                        bestLane = i;
                        bestAvailable = laneAvailableAt[i];
                    }
                }

                var actualStart = bestAvailable > start ? bestAvailable : start;
                var actualEnd = end > actualStart ? end : actualStart;

                s.lane_no = bestLane + 1;
                laneAvailableAt[bestLane] = actualEnd;
            }
        }

        private async Task ConsumeMaterialsOnProductionStartAsync(
    production prod,
    DateTime now,
    CancellationToken ct = default)
        {
            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                throw new InvalidOperationException("Production has no order_id");

            var orderId = prod.order_id.Value;
            var refDoc = $"PROD-START-{prod.prod_id}";

            // Chống trừ lặp nếu API start bị gọi lại
            var alreadyConsumed = await _db.stock_moves
                .AsNoTracking()
                .AnyAsync(x => x.type == "OUT" && x.ref_doc == refDoc, ct);

            if (alreadyConsumed)
                return;

            var bomLines = await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                where oi.order_id == orderId
                select new
                {
                    oi.item_id,
                    order_qty = oi.quantity,
                    b.material_id,
                    b.material_code,
                    b.material_name,
                    b.unit,
                    b.qty_total,
                    b.qty_per_product,
                    b.wastage_percent
                }
            ).ToListAsync(ct);

            if (bomLines.Count == 0)
                throw new InvalidOperationException("No BOM found for this order. Cannot consume materials.");

            if (bomLines.Any(x => !x.material_id.HasValue || x.material_id.Value <= 0))
                throw new InvalidOperationException("Some BOM lines do not map to a valid material_id.");

            var requiredByMaterial = bomLines
                .GroupBy(x => x.material_id!.Value)
                .Select(g =>
                {
                    decimal requiredQty = 0m;

                    foreach (var line in g)
                    {
                        decimal lineQty;

                        if (line.qty_total > 0m)
                        {
                            lineQty = (decimal)line.qty_total;
                        }
                        else
                        {
                            var orderQty = line.order_qty <= 0 ? 1 : line.order_qty;
                            var qtyPerProduct = line.qty_per_product ?? 0m;
                            var wastageFactor = 1m + ((line.wastage_percent ?? 0m) / 100m);

                            lineQty = orderQty * qtyPerProduct * wastageFactor;
                        }

                        if (lineQty < 0m) lineQty = 0m;
                        requiredQty += lineQty;
                    }

                    var first = g.First();

                    return new
                    {
                        MaterialId = g.Key,
                        MaterialCode = first.material_code,
                        MaterialName = first.material_name,
                        Unit = first.unit,
                        RequiredQty = Math.Round(requiredQty, 4)
                    };
                })
                .ToList();

            var materialIds = requiredByMaterial
                .Select(x => x.MaterialId)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            foreach (var req in requiredByMaterial)
            {
                if (!materials.TryGetValue(req.MaterialId, out var mat))
                    throw new InvalidOperationException(
                        $"Material not found. material_id={req.MaterialId}, code={req.MaterialCode}");

                var stockQty = mat.stock_qty ?? 0m;

                if (stockQty < req.RequiredQty)
                {
                    throw new InvalidOperationException(
                        $"Insufficient stock for material '{mat.name}' ({mat.code}). " +
                        $"Available={stockQty}, Required={req.RequiredQty}");
                }
            }

            foreach (var req in requiredByMaterial)
            {
                var mat = materials[req.MaterialId];
                mat.stock_qty = (mat.stock_qty ?? 0m) - req.RequiredQty;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = req.MaterialId,
                    type = "OUT",
                    qty = req.RequiredQty,
                    ref_doc = refDoc,
                    user_id = prod.manager_id,
                    move_date = now,
                    note = $"Consume material when production starts. prod_id={prod.prod_id}, order_id={orderId}",
                    purchase_id = null
                }, ct);
            }
        }

        private async Task<List<BomMissingMaterialItem>> GetBomMissingMaterialMappingsAsync(
    int orderId,
    CancellationToken ct = default)
        {
            return await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                where oi.order_id == orderId
                      && (!b.material_id.HasValue || b.material_id.Value <= 0)
                orderby oi.item_id, b.bom_id
                select new BomMissingMaterialItem
                {
                    bom_id = b.bom_id,
                    order_item_id = oi.item_id,
                    source_estimate_id = b.source_estimate_id,
                    material_code = b.material_code,
                    material_name = b.material_name,
                    unit = b.unit,
                    qty_total = b.qty_total ?? 0m
                }
            ).ToListAsync(ct);
        }

        public async Task<production?> GetLatestByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _db.productions
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<SetProductionMethodResponse?> SetProductionMethodAsync(
    SetProductionMethodRequest req,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            if (req.order_id <= 0)
                throw new InvalidOperationException("order_id không hợp lệ.");

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var order = await _db.orders
                    .FirstOrDefaultAsync(x => x.order_id == req.order_id, ct);

                if (order == null)
                    return null;

                var prod = await _db.productions
                    .Where(x => x.order_id == req.order_id)
                    .OrderByDescending(x => x.prod_id)
                    .FirstOrDefaultAsync(ct);

                if (prod == null)
                    throw new InvalidOperationException("Production not found for this order.");

                if (string.Equals(prod.status, "InProcessing", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Delivery", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Không thể thay đổi phương thức sản xuất vì đơn hàng đã bắt đầu hoặc đã hoàn tất sản xuất.");
                }

                var orderReq = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == req.order_id)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

                if (orderReq == null)
                    throw new InvalidOperationException("Order request not found for this order.");

                var orderQty = orderReq.quantity ?? 0;

                if (orderQty <= 0)
                {
                    orderQty = await _db.order_items
                        .AsNoTracking()
                        .Where(x => x.order_id == req.order_id)
                        .OrderBy(x => x.item_id)
                        .Select(x => x.quantity)
                        .FirstOrDefaultAsync(ct);
                }

                if (orderQty <= 0)
                    throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ.");

                var method = (req.production_method ?? "").Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(method))
                {
                    if (req.is_full_process == true)
                        method = "NVL";
                    else if (req.is_full_process == false)
                        method = "SUB";
                }

                if (method is not ("NVL" or "SUB" or "BOTH"))
                    throw new InvalidOperationException("production_method must be NVL | SUB | BOTH.");

                // Nếu production trước đó đang dùng bán thành phẩm, thì hoàn lại số lượng đã trừ trước khi đổi phương thức.
                if (prod.is_full_process == false
                    && prod.sub_product_id.HasValue
                    && prod.sub_product_used_qty > 0)
                {
                    var oldSubProduct = await _db.sub_products
                        .FirstOrDefaultAsync(x => x.id == prod.sub_product_id.Value, ct);

                    if (oldSubProduct != null)
                    {
                        oldSubProduct.quantity += prod.sub_product_used_qty;
                    }

                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                }

                if (method == "NVL")
                {
                    await RollbackSubProductFinishedTasksAsync(prod.prod_id, ct);

                    prod.prod_method = "NVL";
                    prod.is_full_process = true;
                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                    prod.nvl_qty = orderQty;
                    prod.mgr_note = string.IsNullOrWhiteSpace(req.mgr_note) ? null : req.mgr_note.Trim();

                    order.is_production_ready = true;
                    order.is_enough = true;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return new SetProductionMethodResponse
                    {
                        success = true,
                        order_id = order.order_id,
                        prod_id = prod.prod_id,
                        is_full_process = true,
                        production_method = "NVL",
                        sub_product_id = null,
                        sub_product_used_qty = 0,
                        nvl_qty = orderQty,
                        order_quantity = orderQty,
                        gm_note = prod.gm_note,
                        mgr_note = prod.mgr_note,
                        message = "Đã duyệt sản xuất bằng NVL."
                    };
                }

                if (method == "SUB")
                {
                    if (!req.sub_id.HasValue || req.sub_id.Value <= 0)
                        throw new InvalidOperationException("Vui lòng truyền sub_id khi chọn SUB.");

                    var selectedSubProduct = await ResolveValidSubProductAsync(
                        req.sub_id.Value,
                        prod,
                        orderReq,
                        orderQty,
                        requireEnoughQty: true,
                        ct);

                    selectedSubProduct.quantity -= orderQty;

                    prod.prod_method = "SUB";
                    prod.is_full_process = false;
                    prod.sub_product_id = selectedSubProduct.id;
                    prod.sub_product_used_qty = orderQty;
                    prod.nvl_qty = 0;
                    prod.mgr_note = string.IsNullOrWhiteSpace(req.mgr_note) ? null : req.mgr_note.Trim();

                    order.is_production_ready = true;
                    order.is_enough = true;

                    await ApplySubProductToExistingTasksAsync(
                        prod,
                        selectedSubProduct,
                        orderQty,
                        ct);

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return new SetProductionMethodResponse
                    {
                        success = true,
                        order_id = order.order_id,
                        prod_id = prod.prod_id,
                        is_full_process = false,
                        production_method = "SUB",
                        sub_product_id = selectedSubProduct.id,
                        sub_product_used_qty = orderQty,
                        nvl_qty = 0,
                        order_quantity = orderQty,
                        gm_note = prod.gm_note,
                        mgr_note = prod.mgr_note,
                        message = "Đã duyệt sản xuất bằng bán thành phẩm."
                    };
                }

            if (method == "BOTH")
            {
                if (!req.sub_id.HasValue || req.sub_id.Value <= 0)
                    throw new InvalidOperationException("Vui lòng truyền sub_id khi chọn BOTH.");

                var selectedSubProduct = await ResolveValidSubProductAsync(
                    req.sub_id.Value,
                    prod,
                    orderReq,
                    orderQty,
                    requireEnoughQty: false,
                    ct);

                if (selectedSubProduct.quantity <= 0)
                    throw new InvalidOperationException("Bán thành phẩm không có số lượng để kết hợp.");

                var subUseQty = Math.Min(selectedSubProduct.quantity, orderQty);
                var nvlQty = orderQty - subUseQty;

                if (nvlQty <= 0)
                    throw new InvalidOperationException("Số lượng bán thành phẩm đã đủ. Vui lòng chọn SUB thay vì BOTH.");

                selectedSubProduct.quantity -= subUseQty;

                prod.prod_method = "BOTH";
                prod.is_full_process = null;
                prod.sub_product_id = selectedSubProduct.id;
                prod.sub_product_used_qty = subUseQty;
                prod.nvl_qty = nvlQty;
                prod.mgr_note = string.IsNullOrWhiteSpace(req.mgr_note) ? null : req.mgr_note.Trim();

                order.is_production_ready = true;
                order.is_enough = true;

                // Task sản xuất phần thiếu nvl_qty.
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new SetProductionMethodResponse
                {
                    success = true,
                    order_id = order.order_id,
                    prod_id = prod.prod_id,
                    is_full_process = null,
                    production_method = "BOTH",
                    sub_product_id = selectedSubProduct.id,
                    sub_product_used_qty = subUseQty,
                    nvl_qty = nvlQty,
                    order_quantity = orderQty,
                    gm_note = prod.gm_note,
                    mgr_note = prod.mgr_note,
                    message = $"Đã duyệt sản xuất kết hợp. Dùng {subUseQty} bán thành phẩm, sản xuất thêm {nvlQty} bằng NVL."
                };
            }
                throw new InvalidOperationException("Unsupported production method.");
            });
        }

        private async Task<sub_product> ResolveValidSubProductAsync(
    int subId,
    production prod,
    order_request orderReq,
    int orderQty,
    bool requireEnoughQty,
    CancellationToken ct)
        {
            if (!prod.product_type_id.HasValue || prod.product_type_id.Value <= 0)
                throw new InvalidOperationException("Production chưa có product_type_id.");

            if (!orderReq.print_width_mm.HasValue || orderReq.print_width_mm.Value <= 0)
                throw new InvalidOperationException("Order request chưa có print_width_mm.");

            if (!orderReq.print_length_mm.HasValue || orderReq.print_length_mm.Value <= 0)
                throw new InvalidOperationException("Order request chưa có print_length_mm.");

            var selectedSubProduct = await _db.sub_products
                .Include(x => x.product_type)
                .FirstOrDefaultAsync(x => x.id == subId, ct);

            if (selectedSubProduct == null)
                throw new InvalidOperationException($"Không tìm thấy bán thành phẩm có id = {subId}.");

            if (!selectedSubProduct.is_active)
                throw new InvalidOperationException("Bán thành phẩm đã chọn đang không hoạt động.");

            if (selectedSubProduct.product_type_id != prod.product_type_id.Value)
                throw new InvalidOperationException("Bán thành phẩm đã chọn không cùng loại sản phẩm với production.");

            if (selectedSubProduct.width != orderReq.print_width_mm.Value)
                throw new InvalidOperationException(
                    $"Bán thành phẩm không đúng chiều rộng. Yêu cầu: {orderReq.print_width_mm.Value}, thực tế: {selectedSubProduct.width}.");

            if (selectedSubProduct.length != orderReq.print_length_mm.Value)
                throw new InvalidOperationException(
                    $"Bán thành phẩm không đúng chiều dài. Yêu cầu: {orderReq.print_length_mm.Value}, thực tế: {selectedSubProduct.length}.");

            if (requireEnoughQty && selectedSubProduct.quantity < orderQty)
                throw new InvalidOperationException(
                    $"Số lượng bán thành phẩm không đủ. Cần: {orderQty}, hiện có: {selectedSubProduct.quantity}.");

            if (!requireEnoughQty && selectedSubProduct.quantity <= 0)
                throw new InvalidOperationException("Bán thành phẩm không còn số lượng để kết hợp.");

            return selectedSubProduct;
        }

        private async Task RollbackSubProductFinishedTasksAsync(
    int prodId,
    CancellationToken ct)
        {
            var tasks = await _db.tasks
                .Where(x =>
                    x.prod_id == prodId &&
                    x.is_taken_sub_product == true)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return;

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs
                .Where(x =>
                    x.task_id.HasValue &&
                    taskIds.Contains(x.task_id.Value) &&
                    x.action_type == "Finished" &&
                    x.scanned_code != null &&
                    x.scanned_code.StartsWith("SUB_PRODUCT-"))
                .ToListAsync(ct);

            _db.task_logs.RemoveRange(logs);

            foreach (var t in tasks)
            {
                t.status = "Unassigned";
                t.start_time = null;
                t.end_time = null;
                t.reason = null;
                t.is_taken_sub_product = false;
            }
        }

        private static string NormProcessCodeForSubProduct(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static HashSet<string> ParseSubProductProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(
                    new[] { ',', ';', '|', '/', '\\' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCodeForSubProduct)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ProductionQtyContext
        {
            public int order_qty { get; init; } = 1;
            public int sheets_total { get; init; } = 1;
            public int sheets_required { get; init; } = 1;
            public int n_up { get; init; } = 1;
            public int number_of_plates { get; init; } = 1;
        }

        private async Task<ProductionQtyContext> GetProductionQtyContextAsync(
            int orderId,
            CancellationToken ct)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var orderQty = req?.quantity ?? 0;

            if (orderQty <= 0)
            {
                orderQty = await _db.order_items
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderBy(x => x.item_id)
                    .Select(x => x.quantity)
                    .FirstOrDefaultAsync(ct);
            }

            if (orderQty <= 0)
                orderQty = 1;

            cost_estimate? est = null;

            if (req != null)
            {
                if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
                {
                    est = await _db.cost_estimates
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x =>
                            x.estimate_id == req.accepted_estimate_id.Value &&
                            x.order_request_id == req.order_request_id,
                            ct);
                }

                est ??= await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == req.order_request_id)
                    .OrderByDescending(x => x.is_active)
                    .ThenByDescending(x => x.estimate_id)
                    .FirstOrDefaultAsync(ct);
            }

            var sheetsRequired = Math.Max(est?.sheets_required ?? 0, 0);
            var sheetsTotal = Math.Max(est?.sheets_total ?? 0, sheetsRequired);
            var nUp = est?.n_up > 0 ? est.n_up : 1;
            var numberOfPlates = req?.number_of_plates ?? 1;

            if (sheetsRequired <= 0)
                sheetsRequired = Math.Max(1, (int)Math.Ceiling(orderQty / (decimal)nUp));

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired;

            if (sheetsTotal <= 0)
                sheetsTotal = 1;

            if (numberOfPlates <= 0)
                numberOfPlates = 1;

            return new ProductionQtyContext
            {
                order_qty = orderQty,
                sheets_required = sheetsRequired,
                sheets_total = sheetsTotal,
                n_up = nUp,
                number_of_plates = numberOfPlates
            };
        }

        private static int ResolveQtyGoodForSubProductTask(
            string? processCode,
            int stageIndex,
            IReadOnlyList<string?> routeProcessCodes,
            ProductionQtyContext ctx)
        {
            return StageQuantityHelper.GetProductionOutputCap(
                currentCode: processCode,
                currentStageIndex: stageIndex,
                routeProcessCodes: routeProcessCodes,
                sheetsTotal: ctx.sheets_total,
                nUp: ctx.n_up,
                numberOfPlates: ctx.number_of_plates);
        }

        /// <summary>
        /// Khi production đã có task rồi, chọn sub_product sẽ tự Finished các task từ đầu route
        /// </summary>
        private async Task ApplySubProductToExistingTasksAsync(
            production prod,
            sub_product selectedSubProduct,
            int orderQty,
            CancellationToken ct)
        {
            if (prod.is_full_process != false)
                return;

            if (string.IsNullOrWhiteSpace(selectedSubProduct.product_process))
                return;

            if (!prod.order_id.HasValue)
                return;

            var selectedCodes = ParseSubProductProcessCodes(selectedSubProduct.product_process);
            if (selectedCodes.Count == 0)
                return;

            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == prod.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return;

            var maxCompletedSeq = tasks
                .Where(x => selectedCodes.Contains(NormProcessCodeForSubProduct(x.process?.process_code)))
                .Select(x => x.seq_num)
                .Where(x => x.HasValue)
                .Select(x => (int?)x!.Value)
                .Max();

            if (!maxCompletedSeq.HasValue)
                return;

            var now = AppTime.NowVnUnspecified();
            var reason = "Bán thành phẩm đã có sẵn trong kho";

            var routeCodes = tasks
                .Select(x => (string?)x.process?.process_code)
                .ToList();

            var qtyCtx = await GetProductionQtyContextAsync(prod.order_id.Value, ct);

            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];

                if (!t.seq_num.HasValue || t.seq_num.Value > maxCompletedSeq.Value)
                    continue;

                if (string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    continue;

                t.status = "Finished";
                t.start_time ??= now;
                t.end_time = now;
                t.reason = reason;
                t.is_taken_sub_product = true;

                var alreadyHasLog = await _db.task_logs
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.task_id == t.task_id &&
                        x.action_type == "Finished",
                        ct);

                if (!alreadyHasLog)
                {
                    var qtyGood = ResolveQtyGoodForSubProductTask(
                        t.process?.process_code,
                        i,
                        routeCodes,
                        qtyCtx);

                    await _db.task_logs.AddAsync(new task_log
                    {
                        task_id = t.task_id,
                        scanned_code = $"SUB_PRODUCT-{selectedSubProduct.id}",
                        action_type = "Finished",
                        qty_good = qtyGood,
                        log_time = now,
                        scanned_by_user_id = null,
                        material_usage_json = null
                    }, ct);
                }
            }
        }

        private static string RemoveDiacriticsForMaterial(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static string NormalizeMaterialCodeForDetail(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var s = RemoveDiacriticsForMaterial(raw)
                .Trim()
                .ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Z0-9]+", "_");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

            return s switch
            {
                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",

                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",

                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                _ => s
            };
        }

        private static bool IsNoCoatingType(string? coatingType)
        {
            var s = NormalizeMaterialCodeForDetail(coatingType);

            return string.IsNullOrWhiteSpace(s)
                   || s == "NONE"
                   || s == "NO"
                   || s == "NO_COATING"
                   || s == "KHONG"
                   || s == "KHONG_PHU"
                   || s == "KHONG_COATING";
        }

        private static string? ResolveCoatingMaterialCodeForDetail(string? coatingType)
        {
            if (IsNoCoatingType(coatingType))
                return null;

            var code = NormalizeMaterialCodeForDetail(coatingType);

            return string.IsNullOrWhiteSpace(code) ? null : code;
        }

        private async Task<material?> ResolveCoatingMaterialForDetailAsync(
            cost_estimate est,
            CancellationToken ct = default)
        {
            if (est.coating_glue_weight_kg <= 0m)
                return null;

            if (IsNoCoatingType(est.coating_type))
                return null;

            var code = ResolveCoatingMaterialCodeForDetail(est.coating_type);
            var displayName = ProductionFlowHelper.ResolveCoatingDisplayName(est.coating_type);

            var aliases = new List<string?> { code, est.coating_type, displayName }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeMaterialCodeForDetail)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            var allMaterials = await _db.materials
                .AsNoTracking()
                .ToListAsync(ct);

            return allMaterials.FirstOrDefault(m =>
                aliases.Contains(NormalizeMaterialCodeForDetail(m.code)) ||
                aliases.Contains(NormalizeMaterialCodeForDetail(m.name)));
        }

        public async Task<ImportReceiveSourceDto?> GetImportReceiveSourceByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            if (prod == null || !prod.order_id.HasValue)
                return null;

            var order = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (order == null)
                return null;

            var items = await (
                from oi in _db.order_items.AsNoTracking()
                join pt in _db.product_types.AsNoTracking()
                    on oi.product_type_id equals pt.product_type_id into ptJoin
                from pt in ptJoin.DefaultIfEmpty()
                where oi.order_id == orderId
                orderby oi.item_id
                select new ImportReceiveItemDto
                {
                    item_id = oi.item_id,
                    product_name = oi.product_name,
                    quantity = oi.quantity,
                    packaging_standard = pt != null ? pt.packaging_standard : null
                }
            ).ToListAsync(ct);

            return new ImportReceiveSourceDto
            {
                prod_id = prod.prod_id,
                order_id = order.order_id,
                order_code = order.code ?? string.Empty,
                items = items
            };
        }

        public async Task<bool> SaveImportReceivePathAsync(int prodId, string path, CancellationToken ct = default)
        {
            var prod = await _db.productions.FirstOrDefaultAsync(x => x.prod_id == prodId, ct);
            if (prod == null) return false;

            prod.import_recieve_path = path;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        private static BothStageQuantityContext ResolveBothStageQuantityContext(
    ProductionDetailDto detail,
    string? processCode,
    int currentStageIndex,
    IReadOnlyList<string?> routeProcessCodes,
    int fullSheetQty,
    int fullOutputQty)
        {
            var isBoth = string.Equals(
                detail.production_method,
                "BOTH",
                StringComparison.OrdinalIgnoreCase);

            if (!isBoth)
            {
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = false,
                    is_stage_covered_by_sub = false,
                    nvl_ratio = 1m
                };
            }

            var orderQty = detail.quantity <= 0 ? 1 : detail.quantity;
            var nvlQty = detail.nvl_qty > 0
                ? detail.nvl_qty
                : Math.Max(orderQty - detail.sub_product_used_qty, 0);

            if (nvlQty <= 0)
                nvlQty = orderQty;

            var nvlRatio = Math.Clamp((decimal)nvlQty / orderQty, 0m, 1m);

            var subCodes = ParseSelectedProcessCodes(detail.sub_product_process);

            var subLastIndex = -1;

            for (var i = 0; i < routeProcessCodes.Count; i++)
            {
                var code = NormalizeProcessCode(routeProcessCodes[i]);

                if (subCodes.Contains(code))
                    subLastIndex = i;
            }

            var currentCode = NormalizeProcessCode(processCode);

            var isRalo = currentCode == "RALO" || currentCode == "RA_LO";

            // Nếu sub_product đã đi tới công đoạn X, thì các công đoạn từ đầu tới X chỉ cần sản xuất phần thiếu bằng NVL.
            var isCoveredBySub =
                subLastIndex >= 0 &&
                currentStageIndex <= subLastIndex;

            if (!isCoveredBySub)
            {
                // quay về tổng số lượng full.
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = true,
                    is_stage_covered_by_sub = false,
                    nvl_ratio = nvlRatio
                };
            }

            if (isRalo)
            {
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = true,
                    is_stage_covered_by_sub = true,
                    nvl_ratio = nvlRatio
                };
            }

            var scaledSheetQty = Math.Max(
                1,
                (int)Math.Ceiling(fullSheetQty * nvlRatio));

            var scaledOutputQty = Math.Max(
                1,
                (int)Math.Ceiling(fullOutputQty * nvlRatio));

            return new BothStageQuantityContext
            {
                stage_sheet_qty = scaledSheetQty,
                stage_output_qty = scaledOutputQty,
                is_both = true,
                is_stage_covered_by_sub = true,
                nvl_ratio = nvlRatio
            };
        }
    }
}