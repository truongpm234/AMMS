using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AMMS.Application.Services
{
    public class GroupProductionService : IGroupProductionService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _noti;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly ITaskScanService _scanSvc;
        private static readonly string[] Dept1Codes = { "RALO", "CAT", "IN" };
        private static readonly string[] Dept2Codes = { "PHU", "CAN", "BOI" };
        private static readonly string[] Dept3Codes = { "BE", "DUT", "DAN" };
        private static readonly string[] GroupableDept2Codes = { "PHU", "CAN", "BOI" };
        private static readonly string[] FullRouteOrder = { "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN" };
        private const int MinProductionDays = 7;
        private const int Dept1Days = 3;
        private const int Dept2Days = 2;
        private const int Dept3Days = 2;
        private const string NotePrivateBeforeGroup =
    "Các công đoạn đầu được tách thành lệnh sản xuất riêng cho từng đơn hàng.";

        private const string NoteGroupDept2 =
            "Các công đoạn phòng ban 2 đủ điều kiện sẽ được ghép chung vào một lệnh sản xuất.";

        private const string NoteSplitAfterGroup =
            "Các công đoạn sau ghép nhóm được tách thành lệnh sản xuất riêng cho từng đơn hàng.";

        private const string NoteSinglePreview =
            "Đơn hàng không đủ điều kiện ghép nhóm nên được đề xuất sản xuất riêng.";

        private const string NoteGroupSuggestion =
            "Các đơn hàng đủ điều kiện ghép nhóm ở công đoạn phòng ban 2. Các công đoạn còn lại sẽ được tách riêng theo từng đơn hàng.";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GroupProductionService(AppDbContext db, NotificationService noti, IHubContext<RealtimeHub> hub, ITaskScanService scanSvc)
        {
            _db = db;
            _noti = noti;
            _hub = hub;
            _scanSvc = scanSvc;
        }

        public async Task<List<GroupProductionCandidateDto>> GetCandidatesAsync(
    int? productTypeId,
    string? processCodes,
    CancellationToken ct = default)
        {
            /*
             * FE mới không cần API này nữa.
             * Giữ lại để backward compatibility.
             * Candidate được flatten từ SuggestAsync để không lệch logic.
             */
            var suggestions = await SuggestAsync(
                productTypeId,
                processCodes,
                orderIds: null,
                ct);

            var displaySuggestions = suggestions
    .Where(x => x.orders != null)
    .ToList();

            var result = new List<GroupProductionCandidateDto>();

            foreach (var suggestion in displaySuggestions)
            {
                foreach (var order in suggestion.orders)
                {
                    result.Add(new GroupProductionCandidateDto
                    {
                        order_id = order.order_id,
                        order_code = order.order_code,

                        single_prod_id = order.single_prod_id,

                        product_type_id = order.product_type_id,
                        product_type_name = order.product_type_name,

                        product_name = order.product_name,
                        quantity = order.quantity,

                        production_process = order.production_process,
                        process_key = string.Join(
                            ",",
                            (suggestion.suggest_process ?? new List<string>())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(FullRouteIndex)),

                        delivery_date = order.delivery_date,

                        can_group = suggestion.can_group,
                        reason = suggestion.can_group
                            ? suggestion.reason
                            : suggestion.note ?? suggestion.reason,

                        production_method = order.production_method,
                        has_started_task = false
                    });
                }
            }

            return result
                .GroupBy(x => new
                {
                    x.order_id,
                    x.single_prod_id,
                    x.process_key
                })
                .Select(g => g.First())
                .OrderBy(x => x.product_type_id)
                .ThenBy(x => x.delivery_date)
                .ThenBy(x => x.order_id)
                .ToList();
        }

        private async Task<List<GroupOrderRow>> LoadCleanRowsForSuggestionAsync(
    int? productTypeId,
    List<string> selectedCodes,
    CancellationToken ct)
        {
            var today = AppTime.NowVnUnspecified().Date;

            /*
             * Nếu muốn chỉ hiện đơn giao >= 7 ngày thì giữ minDeliveryDate.
             * Nếu muốn single preview hiện cả đơn gấp thì chỉ check delivery_date != null.
             */
            var minDeliveryDate = today.AddDays(MinProductionDays);

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            var orderIds = await (
                from o in _db.orders.AsNoTracking()
                join pr in _db.productions.AsNoTracking()
                    on o.order_id equals pr.order_id

                where pr.prod_kind == "SINGLE"
                      && pr.status == "Pending"
                      && o.status == "Pending"

                      && o.layout_confirmed
                      && o.is_production_ready

                      && o.delivery_date != null

                      /*
                       * không hiện single preview thì bỏ comment dòng dưới.
                       */
                      //&& o.delivery_date >= minDeliveryDate

                      && !_db.tasks.Any(t => t.prod_id == pr.prod_id)

                      && pr.planned_start_date == null
                      && pr.planned_end_date == null

                      && !_db.prod_orders.Any(po =>
                            po.order_id == o.order_id &&
                            po.status == "Active" &&
                            _db.productions.Any(g =>
                                g.prod_id == po.prod_id &&
                                g.prod_kind == "GROUP" &&
                                g.status != "Cancelled" &&
                                g.status != "Completed"))

                select o.order_id
            )
            .Distinct()
            .ToListAsync(ct);

            if (orderIds.Count == 0)
                return new List<GroupOrderRow>();

            var rows = await LoadGroupOrderRowsAsync(
                orderIds,
                selectedCodes,
                ct);

            return await ApplySuggestionEligibilityFilterAsync(
                rows,
                productTypeId,
                selectedCodes,
                ct);
        }

        private async Task<List<GroupOrderRow>> ApplySuggestionEligibilityFilterAsync(
    List<GroupOrderRow> rows,
    int? productTypeId,
    List<string> selectedCodes,
    CancellationToken ct)
        {
            if (rows == null || rows.Count == 0)
                return new List<GroupOrderRow>();

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            /*
             * Nếu FE truyền processCodes thì process đó bắt buộc phải là PHU/CAN/BOI.
             */
            if (selectedCodes.Count > 0)
                EnsureOnlyGroupableDept2Codes(selectedCodes);

            var orderIds = rows
                .Select(x => x.Order.order_id)
                .Distinct()
                .ToList();

            var singleProdIds = rows
                .Select(x => x.SingleProd.prod_id)
                .Distinct()
                .ToList();

            var activeGroupedOrderIds = await LoadActiveGroupedOrderIdsAsync(
                orderIds,
                ct);

            var startedSingleProdIds = await LoadSingleProdIdsHavingStartedTaskAsync(
                singleProdIds,
                selectedCodes,
                ct);

            return rows
                .Where(x => x.Order != null)
                .Where(x => x.SingleProd != null)
                .Where(x => x.Item != null)

                /*
                 * chỉ Pending mới được suggestion/group.
                 */
                .Where(x => string.Equals(
                    x.Order.status,
                    "Pending",
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(
                    x.SingleProd.status,
                    "Pending",
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => x.SingleProd.planned_start_date == null)
                .Where(x => x.SingleProd.planned_end_date == null)

                .Where(x => !activeGroupedOrderIds.Contains(x.Order.order_id))
                .Where(x => !startedSingleProdIds.Contains(x.SingleProd.prod_id))
                .Where(x => !x.HasAnyStartedTask)

                .Where(x =>
                    !productTypeId.HasValue ||
                    x.Item.product_type_id == productTypeId.Value)

                /*
                 * - NVL/SUB được phép ghép chung.
                 * - BOTH không được ghép.
                 */
                .Where(x =>
                {
                    var method = ResolveRowProductionMethodOrNull(x);
                    return method == "NVL" || method == "SUB";
                })

                .Where(x =>
                    selectedCodes.Count == 0 ||
                    selectedCodes.All(code =>
                        x.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))

                .ToList();
        }

        private async Task<HashSet<int>> LoadActiveGroupedOrderIdsAsync(
    List<int> orderIds,
    CancellationToken ct)
        {
            orderIds = orderIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            if (orderIds.Count == 0)
                return new HashSet<int>();

            var ids = await _db.prod_orders
                .AsNoTracking()
                .Where(po =>
                    orderIds.Contains(po.order_id) &&
                    po.status == "Active" &&
                    _db.productions.Any(g =>
                        g.prod_id == po.prod_id &&
                        g.prod_kind == "GROUP" &&
                        g.status != "Cancelled" &&
                        g.status != "Completed"))
                .Select(po => po.order_id)
                .Distinct()
                .ToListAsync(ct);

            return ids.ToHashSet();
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
                .OrderBy(x => x)
                .ToList();

            if (orderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để sản xuất ghép.");

            var selectedCodes = req.process_codes
                .SelectMany(x => GroupProductionHelper.ParseCodes(x))
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn để tạo lệnh sản xuất ghép/tách.");

            /*
             * Đồng bộ với PreviewCoreAsync:
             * chỉ PHU/CAN/BOI được ghép.
             */
            EnsureOnlyGroupableDept2Codes(selectedCodes);

            await ValidateCreateSelectionBelongsToGroupSuggestionAsync(
                orderIds,
                selectedCodes,
                ct);

            /*
             * Preview dùng chính req mà FE gửi.
             * Create sẽ dùng preview này để lấy timeline thật.
             */
            var preview = await PreviewAsync(req, ct);

            if (preview.suggestion_type == "SINGLE_PREVIEW" || orderIds.Count < 2)
            {
                throw new InvalidOperationException(
                    "API tạo lệnh ghép không dùng cho sản xuất đơn. Cần chọn ít nhất 2 order đủ điều kiện ghép.");
            }

            if (preview.days_late_if_any > 0)
            {
                throw new InvalidOperationException(
                    $"Lịch ghép dự kiến trễ {preview.days_late_if_any} ngày so với mốc giao chung {preview.common_delivery_deadline:yyyy-MM-dd}.");
            }

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var rows = await LoadGroupOrderRowsAsync(
                    orderIds,
                    selectedCodes,
                    ct);

                if (rows.Count != orderIds.Count)
                    throw new InvalidOperationException("Một số order không tồn tại hoặc chưa có production riêng.");

                if (rows.Any(x => x.Item == null))
                    throw new InvalidOperationException("Một số order chưa có order_item.");

                var invalidStatusOrders = rows
                    .Where(x =>
                        !string.Equals(x.Order.status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(x.SingleProd.status, "Pending", StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                        $"{x.Order.order_id}(order={x.Order.status}, production={x.SingleProd.status})")
                    .ToList();

                if (invalidStatusOrders.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Chỉ order/production Pending mới được tạo lệnh ghép. " +
                        $"Không hợp lệ: {string.Join(", ", invalidStatusOrders)}");
                }

                if (rows.Any(x => !x.Order.layout_confirmed || !x.Order.is_production_ready))
                    throw new InvalidOperationException("Tất cả order phải xác nhận layout và sẵn sàng sản xuất.");

                var productTypeIds = rows
                    .Select(x => x.Item.product_type_id)
                    .Distinct()
                    .ToList();

                if (productTypeIds.Count != 1 || productTypeIds[0] == null)
                    throw new InvalidOperationException("Các order phải cùng product_type.");

                var productTypeId = productTypeIds[0]!.Value;

                ValidateRowsHaveGroupableProductionMethodOrThrow(
                    rows,
                    "tạo lệnh ghép");

                ValidateRowsHaveNoStartedTaskOrThrow(
                    rows,
                    "tạo lệnh ghép");

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
                        $"Một số order đã nằm trong production ghép active: {string.Join(",", alreadyGroupedOrderIds)}");
                }

                var plan = BuildDepartmentProductionPlan(
                    rows,
                    selectedCodes,
                    out var warnings);

                if (plan.Count == 0 || !plan.Any(x => x.IsGroup))
                    throw new InvalidOperationException("Không có công đoạn PHU/CAN/BOI hợp lệ để tạo lệnh sản xuất ghép.");

                var allSteps = await _db.product_type_processes
                    .Where(x => x.product_type_id == productTypeId && (x.is_active ?? true))
                    .OrderBy(x => x.seq_num)
                    .ToListAsync(ct);

                /*
                 * Tạo task riêng trước group theo preview.private_stages.
                 * Không gọi SyncSingleDept1TaskTimelineAsync nữa.
                 */
                await EnsureSinglePrivateTasksBeforeGroupAsync(
                    rows,
                    plan,
                    allSteps,
                    preview,
                    ct);

                var createdGroupProdIds = new List<int>();
                var createdSplitProdIds = new List<int>();

                foreach (var segment in plan)
                {
                    var segmentStart = ResolveStageStart(preview, segment);
                    var segmentEnd = ResolveStageEnd(preview, segment);

                    if (segment.IsGroup)
                    {
                        var groupProd = await CreateDepartmentGroupProductionAsync(
                            segment,
                            productTypeId,
                            managerUserId,
                            segmentStart,
                            segmentEnd,
                            req.note,
                            allSteps,
                            ct);

                        createdGroupProdIds.Add(groupProd.prod_id);
                    }
                    else
                    {
                        var splitProd = await CreateSplitProductionAsync(
                            segment,
                            productTypeId,
                            managerUserId,
                            segmentStart,
                            segmentEnd,
                            req.note,
                            allSteps,
                            ct);

                        createdSplitProdIds.Add(splitProd.prod_id);
                    }
                }

                await MarkGroupedOrdersAndSingleProductionsScheduledAsync(
                    rows,
                    preview,
                    ct);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var firstGroupId = createdGroupProdIds.FirstOrDefault();

                var firstGroupCode = firstGroupId > 0
                    ? await _db.productions
                        .Where(x => x.prod_id == firstGroupId)
                        .Select(x => x.code)
                        .FirstOrDefaultAsync(ct)
                    : null;

                return new CreateGroupProductionResponse
                {
                    group_prod_id = firstGroupId,
                    code = firstGroupCode,

                    group_prod_ids = createdGroupProdIds,
                    split_prod_ids = createdSplitProdIds,
                    all_created_prod_ids = createdGroupProdIds
                        .Concat(createdSplitProdIds)
                        .Distinct()
                        .ToList(),

                    order_ids = orderIds,
                    warnings = warnings,
                    message = "Đã tạo lệnh sản xuất ghép theo đúng kế hoạch preview."
                };
            });
        }

        private async Task MarkGroupedOrdersAndSingleProductionsScheduledAsync(
    List<GroupOrderRow> rows,
    GroupProductionConfirmPreviewResponse preview,
    CancellationToken ct)
        {
            var orderIds = rows
                .Select(x => x.Order.order_id)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var singleProdIds = rows
                .Select(x => x.SingleProd.prod_id)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count == 0 || singleProdIds.Count == 0)
                return;

            var orders = await _db.orders
                .Where(x => orderIds.Contains(x.order_id))
                .ToListAsync(ct);

            foreach (var ord in orders)
            {
                /*
                 * Create group chỉ là lập lịch, chưa start.
                 */
                ord.status = "Scheduled";
            }

            var singleProds = await _db.productions
                .Where(x => singleProdIds.Contains(x.prod_id))
                .ToListAsync(ct);

            foreach (var prod in singleProds)
            {
                /*
                 * Lệnh RALO,CAT,IN riêng từng order chỉ được Scheduled sau khi tạo group.
                 * Không được tự chuyển InProcessing ở bước create group.
                 */
                prod.status = "Scheduled";
                prod.actual_start_date = null;

                var privateStage = preview.private_stages
                    .FirstOrDefault(x =>
                        prod.order_id.HasValue &&
                        x.order_ids.Contains(prod.order_id.Value));

                if (privateStage != null)
                {
                    prod.planned_start_date = privateStage.planned_start_date;
                    prod.planned_end_date = preview.estimated_finish_date;
                }
                else
                {
                    prod.planned_start_date ??= preview.suggested_planned_start_date;
                    prod.planned_end_date = preview.estimated_finish_date;
                }
            }
        }

        private async Task EnsureSinglePrivateTasksBeforeGroupAsync(
    List<GroupOrderRow> rows,
    List<ProductionPlanSegment> plan,
    List<product_type_process> allSteps,
    GroupProductionConfirmPreviewResponse preview,
    CancellationToken ct)
        {
            var groupSegments = plan
                .Where(x => x.IsGroup)
                .ToList();

            if (groupSegments.Count == 0)
                return;

            var groupedCodeByOrderId = groupSegments
                .SelectMany(segment => segment.Members.Select(member => new
                {
                    order_id = member.Order.order_id,
                    codes = segment.ProcessCodes
                }))
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(x => x.codes)
                        .Select(NormProcessCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            var now = AppTime.NowVnUnspecified();

            foreach (var row in rows)
            {
                if (!groupedCodeByOrderId.TryGetValue(row.Order.order_id, out var groupedCodes) ||
                    groupedCodes.Count == 0)
                {
                    continue;
                }

                var privateCodesBeforeGroup = BuildPrivateCodesBeforeGroupForOrder(
                    row,
                    groupedCodes);

                if (privateCodesBeforeGroup.Count == 0)
                    continue;

                var stage = preview.private_stages.FirstOrDefault(x =>
                    x.order_ids.Contains(row.Order.order_id) &&
                    SameProcessCodes(x.process_codes, privateCodesBeforeGroup));

                if (stage == null)
                {
                    stage = BuildStageDto(
                        deptCode: "PRIVATE_BEFORE_GROUP",
                        deptName: "Công đoạn riêng trước ghép nhóm",
                        stageType: "SINGLE_PRIVATE",
                        processCodes: privateCodesBeforeGroup,
                        orderIds: new List<int> { row.Order.order_id },
                        start: preview.suggested_planned_start_date,
                        durationDays: Dept1Days,
                        note: NotePrivateBeforeGroup);
                }

                var existingCodes = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x => x.prod_id == row.SingleProd.prod_id)
                    .Select(x => x.process != null ? x.process.process_code : null)
                    .ToListAsync(ct);

                var existingSet = existingCodes
                    .Select(NormProcessCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var method = ResolveRowProductionMethodOrNull(row);

                var subCoveredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (method == "SUB" && row.SingleProd.sub_product_id.HasValue)
                {
                    var subPath = await _db.sub_products
                        .AsNoTracking()
                        .Where(x => x.id == row.SingleProd.sub_product_id.Value)
                        .Select(x => x.product_process)
                        .FirstOrDefaultAsync(ct);

                    subCoveredCodes = ParseProcessCodes(subPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                var steps = allSteps
                    .Where(x => privateCodesBeforeGroup.Contains(
                        NormProcessCode(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => x.seq_num)
                    .ToList();

                var totalMinutes = Math.Max(1, (int)(stage.planned_end_date - stage.planned_start_date).TotalMinutes);
                var minutesPerTask = Math.Max(1, totalMinutes / Math.Max(1, steps.Count));

                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    var code = NormProcessCode(step.process_code);

                    if (existingSet.Contains(code))
                        continue;

                    var isFinishedBySub =
                        method == "SUB" &&
                        subCoveredCodes.Contains(code);

                    var taskStart = stage.planned_start_date.AddMinutes(minutesPerTask * i);
                    var taskEnd = i == steps.Count - 1
                        ? stage.planned_end_date
                        : stage.planned_start_date.AddMinutes(minutesPerTask * (i + 1));

                    await _db.tasks.AddAsync(new task
                    {
                        prod_id = row.SingleProd.prod_id,
                        process_id = step.process_id,
                        seq_num = step.seq_num,
                        name = step.process_name ?? step.process_code ?? code,

                        status = isFinishedBySub ? "Finished" : "Unassigned",

                        machine = ResolveTaskMachineFromProcess(step),
                        input_mode = "MANUAL",

                        start_time = isFinishedBySub ? now : null,
                        end_time = isFinishedBySub ? now : null,

                        planned_start_time = taskStart,
                        planned_end_time = taskEnd,

                        reason = isFinishedBySub
                            ? "Bán thành phẩm đã đáp ứng công đoạn riêng trước ghép nhóm."
                            : "Công đoạn riêng trước ghép nhóm.",

                        is_taken_sub_product = isFinishedBySub
                    }, ct);
                }

                row.SingleProd.status = "Scheduled";
                row.SingleProd.planned_start_date ??= stage.planned_start_date;
                row.SingleProd.planned_end_date = preview.estimated_finish_date;
            }

            await _db.SaveChangesAsync(ct);
        }

        private async Task ValidateCreateSelectionBelongsToGroupSuggestionAsync(
    List<int> selectedOrderIds,
    List<string> selectedCodes,
    CancellationToken ct)
        {
            selectedOrderIds = selectedOrderIds
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            if (selectedOrderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để tạo lệnh ghép.");

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn process_codes để tạo lệnh ghép.");

            var suggestions = await SuggestAsync(
                productTypeId: null,
                processCodes: string.Join(",", selectedCodes),
                orderIds: null,
                ct: ct);

            var groupSuggestions = suggestions
                .Where(x => x.can_group)
                .Where(x => x.create_group_allowed)
                .Where(x => !string.Equals(
                    x.suggestion_type,
                    "SINGLE_PREVIEW",
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => x.suggest_order != null && x.suggest_order.Count >= 2)
                .Where(x => x.suggest_process != null && x.suggest_process.Count > 0)
                .ToList();

            if (groupSuggestions.Count == 0)
            {
                throw new InvalidOperationException(
                    "Không có suggestion ghép hợp lệ nào hiện tại. Các order này chỉ có thể lập lịch đơn hoặc chưa đủ điều kiện ghép.");
            }

            var matched = groupSuggestions.FirstOrDefault(s =>
                IsSelectedOrderSubsetOfSuggestion(
                    s.suggest_order,
                    selectedOrderIds)
                &&
                IsSelectedProcessSubsetOfSuggestion(
                    s.suggest_process,
                    selectedCodes));

            if (matched != null)
                return;

            var validSuggestionText = string.Join(" | ",
                groupSuggestions.Select(FormatSuggestionForManualSelectionError));

            throw new InvalidOperationException(
                "Không đủ điều kiện ghép. Tổ hợp order/process bạn chọn không thuộc suggestion ghép hợp lệ hiện tại. " +
                "Bạn có thể chọn một phần order trong cùng một suggestion, nhưng không được chọn order từ các suggestion khác nhau hoặc order single preview. " +
                $"Order chọn=[{string.Join(",", selectedOrderIds)}], " +
                $"Process chọn=[{string.Join(",", selectedCodes)}]. " +
                $"Suggestion hợp lệ: {validSuggestionText}");
        }

        private async Task EnsureSingleHeadTasksBeforeGroupAsync(
    List<GroupOrderRow> rows,
    List<string> selectedGroupCodes,
    List<product_type_process> allSteps,
    GroupProductionConfirmPreviewResponse preview,
    CancellationToken ct)
        {
            var selectedSet = selectedGroupCodes
                .Select(NormProcessCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var now = AppTime.NowVnUnspecified();

            foreach (var row in rows)
            {
                var route = row.RouteCodes
                    .Select(NormProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                var firstGroupIndex = route.FindIndex(x => selectedSet.Contains(x));

                if (firstGroupIndex <= 0)
                    continue;

                var headCodes = route
                    .Take(firstGroupIndex)
                    .ToList();

                var existingCodes = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x => x.prod_id == row.SingleProd.prod_id)
                    .Select(x => x.process != null ? x.process.process_code : null)
                    .ToListAsync(ct);

                var existingSet = existingCodes
                    .Select(NormProcessCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var method = ResolveRowProductionMethodOrThrow(row);

                var subCoveredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if ((method == "SUB" || method == "BOTH") &&
                    row.SingleProd.sub_product_id.HasValue)
                {
                    var subPath = await _db.sub_products
                        .AsNoTracking()
                        .Where(x => x.id == row.SingleProd.sub_product_id.Value)
                        .Select(x => x.product_process)
                        .FirstOrDefaultAsync(ct);

                    subCoveredCodes = ParseProcessCodes(subPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                var headStart = preview.suggested_planned_start_date;
                var headEnd = preview.dept1_private_stage.planned_end_date;

                var headSteps = allSteps
                    .Where(x => headCodes.Contains(
                        NormProcessCode(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => x.seq_num)
                    .ToList();

                var totalMinutes = Math.Max(1, (int)(headEnd - headStart).TotalMinutes);
                var minutesPerTask = Math.Max(1, totalMinutes / Math.Max(1, headSteps.Count));

                for (var i = 0; i < headSteps.Count; i++)
                {
                    var step = headSteps[i];
                    var code = NormProcessCode(step.process_code);

                    if (existingSet.Contains(code))
                        continue;

                    var isFinishedBySub =
                        method == "SUB" &&
                        subCoveredCodes.Contains(code);

                    var start = headStart.AddMinutes(minutesPerTask * i);
                    var end = i == headSteps.Count - 1
                        ? headEnd
                        : headStart.AddMinutes(minutesPerTask * (i + 1));

                    var task = new task
                    {
                        prod_id = row.SingleProd.prod_id,
                        process_id = step.process_id,
                        seq_num = step.seq_num,
                        name = step.process_name ?? step.process_code ?? code,

                        status = isFinishedBySub ? "Finished" : "Unassigned",

                        machine = ResolveTaskMachineFromProcess(step),
                        input_mode = "MANUAL",

                        start_time = isFinishedBySub ? now : null,
                        end_time = isFinishedBySub ? now : null,

                        planned_start_time = start,
                        planned_end_time = end,

                        reason = isFinishedBySub
                            ? "Bán thành phẩm đã cover công đoạn này khi lập lịch group."
                            : "Task SINGLE trước công đoạn ghép.",

                        is_taken_sub_product = isFinishedBySub
                    };

                    await _db.tasks.AddAsync(task, ct);
                }

                row.SingleProd.planned_start_date ??= headStart;
                row.SingleProd.planned_end_date = headEnd;
                row.SingleProd.status = "Scheduled";
            }

            await _db.SaveChangesAsync(ct);
        }

        private async Task<production> CreateDepartmentGroupProductionAsync(
    ProductionPlanSegment segment,
    int productTypeId,
    int? managerUserId,
    DateTime plannedStart,
    DateTime plannedEnd,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            if (segment == null)
                throw new InvalidOperationException("segment is required.");

            if (segment.Members == null || segment.Members.Count < 2)
                throw new InvalidOperationException("Production ghép cần ít nhất 2 order.");

            var now = AppTime.NowVnUnspecified();

            var selectedCodes = segment.ProcessCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Không có process_code hợp lệ để tạo production ghép.");

            var codesCsv = string.Join(",", selectedCodes);

            var groupCode = await GenerateShortProductionCodeAsync(
                "GRP",
                segment.DepartmentCode,
                ct);

            var inheritedProdMethod = ResolveGroupProductionMethodLabel(segment.Members);
            var methodSummary = ResolveMethodSummary(segment.Members);

            var groupProd = new production
            {
                code = groupCode,
                order_id = null,
                manager_id = managerUserId,
                created_at = now,
                planned_start_date = plannedStart,
                status = "Scheduled",
                product_type_id = productTypeId,
                planned_end_date = plannedEnd,
                prod_kind = "GROUP",
                prod_method = inheritedProdMethod,
                is_full_process = inheritedProdMethod == "NVL"
                    ? true
                    : inheritedProdMethod == "SUB"
                        ? false
                        : null,
                note = NoteGroupDept2,
                group_process_codes = codesCsv,
                group_total_qty = segment.Members.Sum(x => x.Item.quantity)
            };

            await _db.productions.AddAsync(groupProd, ct);
            await _db.SaveChangesAsync(ct);

            var prod_id = await _db.productions.FirstOrDefaultAsync(o => o.code == groupProd.code);
            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager")).SendAsync("group-production", new { meassage = $"Lệnh sản xuất {prod_id.prod_id} đã được lên lịch" });
            await _noti.CreateNotfi(6, $"Lệnh sản xuất {prod_id} đã được lên lịch", null, prod_id.prod_id, "Inprocessing");
            foreach (var member in segment.Members)
            {
                await _db.prod_orders.AddAsync(new prod_order
                {
                    prod_id = groupProd.prod_id,
                    order_id = member.Order.order_id,
                    single_prod_id = member.SingleProd.prod_id,
                    qty = member.Item.quantity,
                    product_type_id = productTypeId,
                    product_process = member.Item.production_process,
                    status = "Active",
                    created_at = now
                }, ct);
            }

            await _db.SaveChangesAsync(ct);

            var stepRows = allSteps
                .Where(x => selectedCodes.Contains(
                    NormProcessCode(x.process_code),
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => FullRouteIndex(x.process_code))
                .ToList();

            if (stepRows.Count != selectedCodes.Count)
            {
                var foundCodes = stepRows
                    .Select(x => NormProcessCode(x.process_code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = selectedCodes
                    .Where(x => !foundCodes.Contains(x))
                    .ToList();

                throw new InvalidOperationException(
                    $"Không tìm thấy đủ process trong product_type_processes. Thiếu: {string.Join(",", missing)}");
            }

            var memberBySingleProdId = segment.Members
                .GroupBy(x => x.SingleProd.prod_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.First());

            var singleProdIds = memberBySingleProdId.Keys.ToList();

            var allSingleTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x =>
                    x.prod_id.HasValue &&
                    singleProdIds.Contains(x.prod_id.Value))
                .OrderBy(x => x.prod_id)
                .ThenBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var tasksToDelete = allSingleTasks
                .Where(x =>
                    selectedCodes.Contains(
                        NormProcessCode(x.process?.process_code),
                        StringComparer.OrdinalIgnoreCase))
                .ToList();

            /*
             * - Nếu single production đã có task cũ thì validate và xóa task được group.
             * - Nếu chưa có task thì vẫn tạo group task
             */
            var missingSingleTaskLinks = new List<(int singleProdId, int orderId, string code)>();

            foreach (var member in segment.Members)
            {
                foreach (var code in selectedCodes)
                {
                    var hasTask = tasksToDelete.Any(x =>
                        x.prod_id == member.SingleProd.prod_id &&
                        string.Equals(
                            NormProcessCode(x.process?.process_code),
                            code,
                            StringComparison.OrdinalIgnoreCase));

                    if (!hasTask)
                    {
                        missingSingleTaskLinks.Add((
                            member.SingleProd.prod_id,
                            member.Order.order_id,
                            code));
                    }
                }
            }

            await ValidateSingleTasksCanBeDeletedForGroupAsync(
                tasksToDelete,
                allSingleTasks,
                ct);

            var groupTaskByCode = new Dictionary<string, task>(StringComparer.OrdinalIgnoreCase);

            var taskCount = Math.Max(stepRows.Count, 1);
            var totalMinutes = Math.Max(1, (int)(plannedEnd - plannedStart).TotalMinutes);
            var minutesPerTask = Math.Max(1, totalMinutes / taskCount);

            for (var i = 0; i < stepRows.Count; i++)
            {
                var step = stepRows[i];
                var code = NormProcessCode(step.process_code);

                var taskStart = plannedStart.AddMinutes(minutesPerTask * i);
                var taskEnd = i == stepRows.Count - 1
                    ? plannedEnd
                    : plannedStart.AddMinutes(minutesPerTask * (i + 1));

                var groupTask = new task
                {
                    prod_id = groupProd.prod_id,
                    name = $"GROUP-{segment.DepartmentCode}-{step.process_name ?? step.process_code}",

                    seq_num = step.seq_num,

                    status = "Unassigned",
                    machine = ResolveTaskMachineFromProcess(step),
                    process_id = step.process_id,
                    input_mode = "MANUAL",

                    planned_start_time = taskStart,
                    planned_end_time = taskEnd,

                    reason = NoteGroupDept2
                };

                await _db.tasks.AddAsync(groupTask, ct);
                groupTaskByCode[code] = groupTask;
            }

            await _db.SaveChangesAsync(ct);

            foreach (var member in segment.Members)
            {
                foreach (var processCodeRaw in selectedCodes)
                {
                    var processCode = NormProcessCode(processCodeRaw);

                    if (!groupTaskByCode.TryGetValue(processCode, out var groupTask))
                        throw new InvalidOperationException($"Không tìm thấy group task cho process_code={processCode}.");

                    var originalSingleTask = allSingleTasks.FirstOrDefault(x =>
                        x.prod_id == member.SingleProd.prod_id &&
                        string.Equals(
                            NormProcessCode(x.process?.process_code),
                            processCode,
                            StringComparison.OrdinalIgnoreCase));

                    await _db.task_links.AddAsync(new task_link
                    {
                        group_prod_id = groupProd.prod_id,
                        group_task_id = groupTask.task_id,

                        single_prod_id = member.SingleProd.prod_id,
                        single_task_id = null,

                        original_single_task_id = originalSingleTask?.task_id,

                        order_id = member.Order.order_id,
                        process_code = processCode,
                        qty_plan = member.Item.quantity,

                        status = "Active",
                        created_at = now
                    }, ct);
                }
            }

            if (tasksToDelete.Count > 0)
            {
                _db.tasks.RemoveRange(tasksToDelete);
            }

            await _db.SaveChangesAsync(ct);

            return groupProd;
        }

        private async Task ValidateSingleTasksCanBeDeletedForGroupAsync(
    List<task> tasksToDelete,
    List<task> allSingleTasks,
    CancellationToken ct)
        {
            if (tasksToDelete == null || tasksToDelete.Count == 0)
                return;

            var taskIdsToDelete = tasksToDelete
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdDeleteSet = taskIdsToDelete.ToHashSet();

            var allTaskIds = allSingleTasks
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdsWithLogs = allTaskIds.Count == 0
                ? new HashSet<int>()
                : (await _db.task_logs
                    .AsNoTracking()
                    .Where(x =>
                        x.task_id.HasValue &&
                        allTaskIds.Contains(x.task_id.Value))
                    .Select(x => x.task_id!.Value)
                    .Distinct()
                    .ToListAsync(ct))
                    .ToHashSet();

            var alreadyLinkedTaskIds = await _db.task_links
    .AsNoTracking()
    .Where(x =>
        (
            x.single_task_id.HasValue &&
            taskIdsToDelete.Contains(x.single_task_id.Value)
        )
        ||
        (
            x.original_single_task_id.HasValue &&
            taskIdsToDelete.Contains(x.original_single_task_id.Value)
        ))
    .Where(x =>
        x.status == null ||
        x.status.ToUpper() != "CANCELLED")
    .Select(x => x.original_single_task_id ?? x.single_task_id)
    .ToListAsync(ct);

            if (alreadyLinkedTaskIds.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Có task đã được ghép trước đó: {string.Join(",", alreadyLinkedTaskIds.Where(x => x.HasValue).Select(x => x!.Value))}");
            }

            foreach (var t in tasksToDelete)
            {
                var code = NormProcessCode(t.process?.process_code);

                if (taskIdsWithLogs.Contains(t.task_id) ||
                    string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.status, "InProcessing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    t.start_time != null ||
                    t.end_time != null)
                {
                    throw new InvalidOperationException(
                        $"Không thể ghép/xóa công đoạn {code} của production {t.prod_id} vì task {t.task_id} đã bắt đầu, đã Finished hoặc đã có log.");
                }

                if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                    throw new InvalidOperationException($"Task {t.task_id} thiếu prod_id hoặc seq_num.");

                var laterStarted = allSingleTasks.Any(x =>
                    x.prod_id == t.prod_id &&
                    x.task_id != t.task_id &&
                    !taskIdDeleteSet.Contains(x.task_id) &&
                    x.seq_num.HasValue &&
                    x.seq_num.Value > t.seq_num.Value &&
                    (
                        taskIdsWithLogs.Contains(x.task_id) ||
                        string.Equals(x.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.status, "InProcessing", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                        x.start_time != null ||
                        x.end_time != null
                    ));

                if (laterStarted)
                {
                    throw new InvalidOperationException(
                        $"Không thể ghép/xóa task {t.task_id} của production {t.prod_id} vì công đoạn phía sau đã bắt đầu hoặc đã có log.");
                }
            }
        }

        private static int ResolveDepartmentDurationDays(string? departmentCode)
        {
            return NormProcessCode(departmentCode) switch
            {
                "DEPT_1" => 3,
                "DEPT_2" => 2,
                "DEPT_3" => 2,
                _ => 1
            };
        }

        private static string ResolveTaskMachineFromProcess(product_type_process step)
        {
            if (!string.IsNullOrWhiteSpace(step.machine))
                return step.machine.Trim();

            if (!string.IsNullOrWhiteSpace(step.process_code))
                return step.process_code.Trim().ToUpperInvariant();

            return "";
        }

        private async Task<production> CreateSplitProductionAsync(
    ProductionPlanSegment segment,
    int productTypeId,
    int? managerUserId,
    DateTime plannedStart,
    DateTime plannedEnd,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            var member = segment.Members.First();
            var inheritedProdMethod = ResolveSegmentProductionMethodOrThrow(segment);
            var now = AppTime.NowVnUnspecified();
            var codesCsv = string.Join(",", segment.ProcessCodes);

            var splitCode = await GenerateShortProductionCodeAsync(
                "SPL",
                segment.DepartmentCode,
                ct);

            var splitProd = new production
            {
                code = splitCode,
                order_id = member.Order.order_id,
                manager_id = managerUserId,
                created_at = now,
                planned_start_date = plannedStart,
                status = "Scheduled",
                product_type_id = productTypeId,
                planned_end_date = plannedEnd,
                note = NoteSplitAfterGroup,

                prod_kind = "SPLIT",
                prod_method = inheritedProdMethod,
                group_process_codes = codesCsv,
                group_total_qty = member.Item.quantity,
                end_date = null
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager")).SendAsync("group-production", new { meassage = $"Lệnh sản xuất {splitProd.prod_id} đã được lên lịch" });
            await _noti.CreateNotfi(6, $"Lệnh sản xuất {splitProd.prod_id} đã được lên lịch", null, splitProd.prod_id, "Inprocessing");

            await _db.productions.AddAsync(splitProd, ct);
            await _db.SaveChangesAsync(ct);

            var processSeqMap = segment.ProcessCodes
                .Select((code, index) => new
                {
                    code = NormProcessCode(code),
                    seq = index + 1
                })
                .ToDictionary(x => x.code, x => x.seq, StringComparer.OrdinalIgnoreCase);

            var existingTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == member.SingleProd.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var candidateTasks = existingTasks
                .Where(x =>
                {
                    var code = NormProcessCode(x.process?.process_code);
                    return processSeqMap.ContainsKey(code);
                })
                .ToList();

            var candidateTaskIds = candidateTasks
                .Select(x => x.task_id)
                .ToList();

            var taskIdsWithLogs = candidateTaskIds.Count == 0
                ? new HashSet<int>()
                : (await _db.task_logs
                    .AsNoTracking()
                    .Where(x => x.task_id.HasValue &&
                                candidateTaskIds.Contains(x.task_id.Value))
                    .Select(x => x.task_id!.Value)
                    .Distinct()
                    .ToListAsync(ct))
                    .ToHashSet();

            foreach (var task in candidateTasks)
            {
                var code = NormProcessCode(task.process?.process_code);

                if (taskIdsWithLogs.Contains(task.task_id) ||
                    string.Equals(task.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(task.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    task.start_time != null ||
                    task.end_time != null)
                {
                    throw new InvalidOperationException(
                        $"Không thể tách công đoạn {code} của production {member.SingleProd.prod_id} sang SPLIT vì task đã bắt đầu hoặc đã có log.");
                }
            }

            var splitTasks = new List<task>();

            /*
             * Case 1:
             * SINGLE đã có task tương ứng thì move sang SPLIT.
             */
            foreach (var task in candidateTasks)
            {
                var taskCode = NormProcessCode(task.process?.process_code);

                task.prod_id = splitProd.prod_id;
                task.seq_num = processSeqMap[taskCode];
                task.status = "Unassigned";
                task.start_time = null;
                task.end_time = null;
                task.reason = NoteSplitAfterGroup;

                splitTasks.Add(task);
            }

            /*
             * Case 2:
             * Nếu không có task để move thì tạo task mới.
             * Lưu ý: set timeline trực tiếp trên splitTasks, không query DB trước SaveChanges.
             */
            if (splitTasks.Count == 0)
            {
                var stepRows = allSteps
                    .Where(x => segment.ProcessCodes.Contains(
                        NormProcessCode(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => FullRouteIndex(x.process_code))
                    .ToList();

                foreach (var step in stepRows)
                {
                    var code = NormProcessCode(step.process_code);

                    var newTask = new task
                    {
                        prod_id = splitProd.prod_id,
                        name = $"SPLIT-{step.process_name ?? step.process_code}",
                        seq_num = processSeqMap.TryGetValue(code, out var seq) ? seq : 999,
                        status = "Unassigned",
                        machine = ResolveTaskMachineFromProcess(step),
                        process_id = step.process_id,
                        input_mode = "MANUAL",
                        reason = $"Task SPLIT theo phòng ban {segment.DepartmentName}."
                    };

                    await _db.tasks.AddAsync(newTask, ct);
                    splitTasks.Add(newTask);
                }
            }

            if (splitTasks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Không tạo được task SPLIT cho order {member.Order.order_id}, process={codesCsv}.");
            }

            var orderedSplitTasks = splitTasks
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .ToList();

            var taskCount = Math.Max(orderedSplitTasks.Count, 1);
            var totalMinutes = Math.Max(1, (int)(plannedEnd - plannedStart).TotalMinutes);
            var minutesPerTask = Math.Max(1, totalMinutes / taskCount);

            for (var i = 0; i < orderedSplitTasks.Count; i++)
            {
                orderedSplitTasks[i].planned_start_time =
                    plannedStart.AddMinutes(minutesPerTask * i);

                orderedSplitTasks[i].planned_end_time =
                    i == orderedSplitTasks.Count - 1
                        ? plannedEnd
                        : plannedStart.AddMinutes(minutesPerTask * (i + 1));
            }

            await _db.SaveChangesAsync(ct);

            return splitProd;
        }

        public async Task<List<SuggestedGroupProductionDto>> SuggestAsync(
    int? productTypeId,
    string? processCodes,
    string? orderIds,
    CancellationToken ct = default)
        {
            var selectedCodes = NormalizeSelectedCodesForGroup(
                GroupProductionHelper.ParseCodes(processCodes));

            var selectedOrderIds = ParseOrderIdsCsv(orderIds);

            if (selectedCodes.Count > 0)
                EnsureOnlyGroupableDept2Codes(selectedCodes);

            List<GroupOrderRow> allRows;

            if (selectedOrderIds.Count > 0)
            {
                var manualRows = await LoadGroupOrderRowsAsync(
                    selectedOrderIds,
                    selectedCodes,
                    ct);

                allRows = await ApplySuggestionEligibilityFilterAsync(
                    manualRows,
                    productTypeId,
                    selectedCodes,
                    ct);
            }
            else
            {
                allRows = await LoadCleanRowsForSuggestionAsync(
                    productTypeId,
                    selectedCodes,
                    ct);
            }

            if (allRows.Count == 0)
                return new List<SuggestedGroupProductionDto>();

            var productTypeIds = allRows
                .Where(x => x.Item != null && x.Item.product_type_id.HasValue)
                .Select(x => x.Item.product_type_id!.Value)
                .Distinct()
                .ToList();

            var productTypeNameMap = productTypeIds.Count == 0
                ? new Dictionary<int, string?>()
                : await _db.product_types
                    .AsNoTracking()
                    .Where(x => productTypeIds.Contains(x.product_type_id))
                    .ToDictionaryAsync(
                        x => x.product_type_id,
                        x => x.name,
                        ct);

            var finalSuggestions = new List<SuggestedGroupProductionDto>();

            /*
             * Rule mới:
             * Group theo product_type_id.
             * Không group theo prod_method nữa.
             */
            foreach (var productTypeGroup in allRows
                .Where(x => x.Item != null)
                .Where(x => x.Item.product_type_id.HasValue)
                .GroupBy(x => x.Item.product_type_id!.Value)
                .OrderBy(g => g.Key))
            {
                var rowsOfOneProductType = productTypeGroup
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                var currentProductTypeId = productTypeGroup.Key;

                productTypeNameMap.TryGetValue(
                    currentProductTypeId,
                    out var currentProductTypeName);

                var groupedOrderIds = new HashSet<int>();

                if (rowsOfOneProductType.Count >= 2)
                {
                    var groupSuggestions = selectedCodes.Count > 0
                        ? BuildSuggestionPreviewFromSelectedCodes(
                            rowsOfOneProductType,
                            selectedCodes)
                        : BuildAutoDept2Suggestions(
                            rowsOfOneProductType);

                    groupSuggestions = groupSuggestions
                        .Where(x => x.suggest_order != null && x.suggest_order.Count >= 2)
                        .Where(x => x.suggest_process != null && x.suggest_process.Count > 0)
                        .Where(x => !string.Equals(
                            x.suggestion_type,
                            "SPLIT_ONLY",
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var suggestion in groupSuggestions)
                    {
                        var memberRows = rowsOfOneProductType
                            .Where(x => suggestion.suggest_order.Contains(x.Order.order_id))
                            .ToList();

                        suggestion.product_type_id = currentProductTypeId;
                        suggestion.product_type_name = currentProductTypeName;
                        suggestion.production_method = ResolveGroupProductionMethodLabel(memberRows);

                        suggestion.can_group = true;
                        suggestion.create_group_allowed = true;
                        suggestion.order_count = suggestion.suggest_order.Count;
                        suggestion.suggestion_key = BuildSuggestionKey(suggestion);

                        foreach (var id in suggestion.suggest_order)
                            groupedOrderIds.Add(id);

                        await EnrichSuggestionWithPreviewAsync(
                            suggestion,
                            currentProductTypeId,
                            currentProductTypeName,
                            rowsOfOneProductType,
                            ct);

                        finalSuggestions.Add(suggestion);
                    }
                }

                /*
                 * Order không nằm trong group hợp lệ vẫn hiện SINGLE_PREVIEW.
                 */
                var singleRows = rowsOfOneProductType
                    .Where(x => !groupedOrderIds.Contains(x.Order.order_id))
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                foreach (var row in singleRows)
                {
                    var method = ResolveRowProductionMethodOrNull(row);

                    /*
                     * BOTH không đưa vào suggestion group.
                     * BOTH sẽ đi flow single riêng.
                     */
                    if (method == "BOTH")
                        continue;

                    var singleSuggestion = await BuildSinglePreviewSuggestionAsync(
                        row,
                        currentProductTypeId,
                        currentProductTypeName,
                        method ?? "",
                        ct);

                    finalSuggestions.Add(singleSuggestion);
                }
            }

            return finalSuggestions
                .OrderByDescending(x => x.can_group)
                .ThenByDescending(x =>
                    string.Equals(x.suggestion_type, "GROUP_WITH_AUTO_SPLIT", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x =>
                    string.Equals(x.suggestion_type, "GROUP", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.product_type_id)
                .ThenBy(x => x.common_delivery_deadline)
                .ThenBy(x =>
                    x.suggest_order == null || x.suggest_order.Count == 0
                        ? int.MaxValue
                        : x.suggest_order.Min())
                .ToList();
        }

        private async Task EnrichSuggestionWithPreviewAsync(
    SuggestedGroupProductionDto suggestion,
    int productTypeId,
    string? productTypeName,
    List<GroupOrderRow> sourceRows,
    CancellationToken ct)
        {
            suggestion.suggest_order ??= new List<int>();
            suggestion.suggest_process ??= new List<string>();
            suggestion.order_codes ??= new List<string?>();
            suggestion.orders ??= new List<SuggestionOrderPreviewDto>();
            suggestion.batches ??= new List<SuggestionBatchPreviewDto>();
            suggestion.auto_split_productions ??= new List<SuggestedSplitProductionDto>();
            suggestion.warnings ??= new List<GroupProductionPlanWarningDto>();

            suggestion.orders = BuildSuggestionOrders(
                sourceRows
                    .Where(x => suggestion.suggest_order.Contains(x.Order.order_id))
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList(),
                productTypeName);

            suggestion.order_codes = suggestion.orders
                .Select(x => x.order_code)
                .ToList();

            suggestion.order_count = suggestion.orders.Count;

            if (suggestion.suggest_order.Count < 2)
            {
                suggestion.can_group = false;
                suggestion.create_group_allowed = false;
                suggestion.preview = null;
                suggestion.batches = new List<SuggestionBatchPreviewDto>();
                suggestion.preview_error = "Không đủ 2 order để tạo gợi ý ghép.";
                suggestion.reason = NoteSinglePreview;
                suggestion.note = NoteSinglePreview;
                return;
            }

            if (suggestion.suggest_process.Count == 0)
            {
                suggestion.can_group = false;
                suggestion.create_group_allowed = false;
                suggestion.preview = null;
                suggestion.batches = new List<SuggestionBatchPreviewDto>();
                suggestion.preview_error = "Không có công đoạn hợp lệ để tạo gợi ý ghép.";
                suggestion.reason = NoteSinglePreview;
                suggestion.note = NoteSinglePreview;
                return;
            }

            try
            {
                /*
                 * QUAN TRỌNG:
                 * Đây là preview nội bộ khi SuggestAsync đang build suggestion.
                 * Không gọi validate-against-current-suggestion nữa, vì chính suggestion này đang được build.
                 */
                var preview = await PreviewCoreAsync(
                    new CreateGroupProductionRequest
                    {
                        order_ids = suggestion.suggest_order,
                        process_codes = suggestion.suggest_process,
                        planned_start_date = null,
                        note = null
                    },
                    validateAgainstSuggestion: false,
                    ct);

                suggestion.suggested_planned_start_date = preview.suggested_planned_start_date;
                suggestion.schedule_planned_start_date = preview.suggested_planned_start_date;

                suggestion.common_delivery_deadline = preview.common_delivery_deadline;
                suggestion.estimated_finish_date = preview.estimated_finish_date;
                suggestion.schedule_planned_end_date = preview.estimated_finish_date;

                suggestion.estimated_total_days = preview.total_duration_days;
                suggestion.preview = preview;
                suggestion.preview_error = null;

                suggestion.batches = await BuildBatchesFromGroupPreviewAsync(
                    preview,
                    suggestion.orders,
                    productTypeId,
                    ct);

                suggestion.can_group = true;
                suggestion.create_group_allowed = true;
                suggestion.suggestion_key = BuildSuggestionKey(suggestion);

                suggestion.reason = NoteGroupSuggestion;
                suggestion.note = NoteGroupSuggestion;

                if (preview.warnings != null && preview.warnings.Count > 0)
                {
                    suggestion.warnings = preview.warnings;
                }
            }
            catch (Exception ex)
            {
                /*
                 * Không để null list.
                 * Nếu còn lỗi, FE vẫn nhận được order/suggest_process, nhưng biết rõ lỗi ở preview_error.
                 */
                suggestion.can_group = false;
                suggestion.create_group_allowed = false;
                suggestion.preview = null;
                suggestion.batches = new List<SuggestionBatchPreviewDto>();
                suggestion.preview_error = ex.Message;
                suggestion.reason = NoteGroupSuggestion;
                suggestion.note = "Gợi ý ghép nhóm chưa thể tạo kế hoạch sản xuất.";
            }
        }

        private static List<int> ParseOrderIdsCsv(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<int>();

            return raw
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => int.TryParse(x, out _))
                .Select(int.Parse)
                .Where(x => x > 0)
                .Distinct()
                .ToList();
        }

        private async Task<SuggestedGroupProductionDto> BuildSinglePreviewSuggestionAsync(
    GroupOrderRow row,
    int productTypeId,
    string? productTypeName,
    string productionMethod,
    CancellationToken ct)
        {
            var routeCodes = row.RouteCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (routeCodes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Order {row.Order.order_id} chưa có production_process hợp lệ.");
            }

            var commonDeadline = ResolveCommonDeadline(new List<GroupOrderRow> { row });
            var suggestedStart = ResolveSuggestedStart(commonDeadline);
            var plannedEnd = suggestedStart.AddDays(MinProductionDays);

            var orderDto = new SuggestionOrderPreviewDto
            {
                order_id = row.Order.order_id,
                order_code = row.Order.code,
                single_prod_id = row.SingleProd.prod_id,
                product_type_id = row.Item.product_type_id,
                product_type_name = productTypeName,
                product_name = row.Item.product_name,
                quantity = row.Item.quantity,
                production_process = row.Item.production_process,
                production_method = productionMethod,
                delivery_date = row.Order.delivery_date
            };

            var tasks = await BuildTaskPreviewDtosAsync(
                productTypeId,
                routeCodes,
                suggestedStart,
                plannedEnd,
                ct);

            var batch = new SuggestionBatchPreviewDto
            {
                batch_type = "SINGLE",
                prod_kind = "SINGLE",
                department_code = "FULL_PATH",
                department_name = "Full production path",

                order_ids = new List<int> { row.Order.order_id },
                order_codes = new List<string?> { row.Order.code },

                process_codes = routeCodes,

                planned_start_date = suggestedStart,
                planned_end_date = plannedEnd,
                duration_days = MinProductionDays,

                tasks = tasks,
                note = NoteSinglePreview
            };

            var timelineStage = BuildStageDto(
                deptCode: "FULL_PATH",
                deptName: "Full production path",
                stageType: "SINGLE",
                processCodes: routeCodes,
                orderIds: new List<int> { row.Order.order_id },
                start: suggestedStart,
                durationDays: MinProductionDays,
                note: NoteSinglePreview);

            var daysLate = Math.Max(
                0,
                (plannedEnd.Date - commonDeadline.Date).Days);

            /*
             * FIX CHÍNH:
             * Trước đây SINGLE_PREVIEW không set preview nên response trả preview = null.
             * Bây giờ tạo preview đầy đủ giống cấu trúc API preview.
             */
            var preview = new GroupProductionConfirmPreviewResponse
            {
                suggestion_type = "SINGLE_PREVIEW",
                can_group = false,
                create_group_allowed = false,

                product_type_id = productTypeId,
                product_type_name = productTypeName,
                production_method = productionMethod,

                order_count = 1,
                order_codes = new List<string?> { row.Order.code },
                orders = new List<SuggestionOrderPreviewDto> { orderDto },
                batches = new List<SuggestionBatchPreviewDto> { batch },

                order_ids = new List<int> { row.Order.order_id },
                process_codes = routeCodes,
                selected_process_codes = routeCodes,

                common_delivery_deadline = commonDeadline,
                suggested_planned_start_date = suggestedStart,
                estimated_finish_date = plannedEnd,
                total_duration_days = MinProductionDays,

                dept1_private_stage = null,
                private_stages = new List<GroupProductionScheduleStageDto>(),
                group_stages = new List<GroupProductionScheduleStageDto>(),
                split_stages = new List<GroupProductionScheduleStageDto>(),
                timeline = new List<GroupProductionScheduleStageDto> { timelineStage },

                can_meet_common_deadline = daysLate == 0,
                days_late_if_any = daysLate,

                warnings = new List<GroupProductionPlanWarningDto>(),
                notes = new List<string>(),

                reason = NoteSinglePreview,
                note = NoteSinglePreview
            };

            return new SuggestedGroupProductionDto
            {
                suggestion_key = $"SINGLE:{row.Order.order_id}:{productionMethod}",
                suggestion_type = "SINGLE_PREVIEW",

                can_group = false,
                create_group_allowed = false,

                suggest_order = new List<int> { row.Order.order_id },
                suggest_process = routeCodes,

                product_type_id = productTypeId,
                product_type_name = productTypeName,
                production_method = productionMethod,

                department_code = "FULL_PATH",
                department_name = "Full production path",
                material_key = null,

                order_count = 1,
                order_codes = new List<string?> { row.Order.code },
                orders = new List<SuggestionOrderPreviewDto> { orderDto },

                batches = new List<SuggestionBatchPreviewDto> { batch },

                suggested_planned_start_date = suggestedStart,
                schedule_planned_start_date = suggestedStart,
                common_delivery_deadline = commonDeadline,
                estimated_finish_date = plannedEnd,
                schedule_planned_end_date = plannedEnd,
                estimated_total_days = MinProductionDays,
                preview = preview,
                preview_error = null,

                auto_split_productions = new List<SuggestedSplitProductionDto>(),
                warnings = new List<GroupProductionPlanWarningDto>(),

                reason = NoteSinglePreview,
                note = NoteSinglePreview
            };
        }

        private static List<SuggestionOrderPreviewDto> BuildSuggestionOrders(
    List<GroupOrderRow> rows,
    string? productTypeName)
        {
            return rows
                .OrderBy(x => x.Order.delivery_date)
                .ThenBy(x => x.Order.order_id)
                .Select(x => new SuggestionOrderPreviewDto
                {
                    order_id = x.Order.order_id,
                    order_code = x.Order.code,
                    single_prod_id = x.SingleProd.prod_id,
                    product_type_id = x.Item.product_type_id,
                    product_type_name = productTypeName,
                    product_name = x.Item.product_name,
                    quantity = x.Item.quantity,
                    production_process = x.Item.production_process,
                    production_method = ResolveRowProductionMethodOrNull(x),
                    delivery_date = x.Order.delivery_date
                })
                .ToList();
        }

        private async Task<List<SuggestionBatchPreviewDto>> BuildBatchesFromGroupPreviewAsync(
    GroupProductionConfirmPreviewResponse preview,
    List<SuggestionOrderPreviewDto> orders,
    int productTypeId,
    CancellationToken ct)
        {
            var result = new List<SuggestionBatchPreviewDto>();

            var timeline = preview.timeline ?? new List<GroupProductionScheduleStageDto>();

            foreach (var stage in timeline
                .OrderBy(x => x.planned_start_date)
                .ThenBy(x => StageOrder(x.stage_type))
                .ThenBy(x => x.order_ids == null || x.order_ids.Count == 0
                    ? int.MaxValue
                    : x.order_ids.Min()))
            {
                stage.order_ids ??= new List<int>();
                stage.process_codes ??= new List<string>();

                var stageOrderCodes = orders
                    .Where(x => stage.order_ids.Contains(x.order_id))
                    .OrderBy(x => stage.order_ids.IndexOf(x.order_id))
                    .Select(x => x.order_code)
                    .ToList();

                var prodKind = stage.stage_type switch
                {
                    "GROUP" => "GROUP",
                    "SPLIT" => "SPLIT",
                    _ => "SINGLE"
                };

                var tasks = await BuildTaskPreviewDtosAsync(
                    productTypeId,
                    stage.process_codes,
                    stage.planned_start_date,
                    stage.planned_end_date,
                    ct);

                result.Add(new SuggestionBatchPreviewDto
                {
                    batch_type = stage.stage_type,
                    prod_kind = prodKind,
                    department_code = stage.dept_code,
                    department_name = stage.dept_name,

                    order_ids = stage.order_ids,
                    order_codes = stageOrderCodes,

                    process_codes = stage.process_codes
                        .Select(NormProcessCode)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),

                    planned_start_date = stage.planned_start_date,
                    planned_end_date = stage.planned_end_date,
                    duration_days = stage.duration_days,

                    tasks = tasks,
                    note = ResolveFixedBatchNote(stage.stage_type)
                });
            }

            return result;
        }

        private static string ResolveFixedBatchNote(string? stageType)
        {
            return NormProcessCode(stageType) switch
            {
                "SINGLE_PRIVATE" => NotePrivateBeforeGroup,
                "GROUP" => NoteGroupDept2,
                "SPLIT" => NoteSplitAfterGroup,
                _ => "Lệnh sản xuất được tạo theo kế hoạch hệ thống."
            };
        }

        private async Task<List<SuggestionTaskPreviewDto>> BuildTaskPreviewDtosAsync(
    int productTypeId,
    List<string> processCodes,
    DateTime plannedStart,
    DateTime plannedEnd,
    CancellationToken ct)
        {
            processCodes = processCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (processCodes.Count == 0)
                return new List<SuggestionTaskPreviewDto>();

            var steps = await _db.product_type_processes
                .AsNoTracking()
                .Where(x =>
                    x.product_type_id == productTypeId &&
                    (x.is_active ?? true))
                .ToListAsync(ct);

            var selectedSteps = steps
                .Where(x => processCodes.Contains(
                    NormProcessCode(x.process_code),
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => FullRouteIndex(x.process_code))
                .ToList();

            var taskCount = Math.Max(selectedSteps.Count, 1);
            var totalMinutes = Math.Max(1, (int)(plannedEnd - plannedStart).TotalMinutes);
            var minutesPerTask = Math.Max(1, totalMinutes / taskCount);

            var result = new List<SuggestionTaskPreviewDto>();

            for (var i = 0; i < selectedSteps.Count; i++)
            {
                var step = selectedSteps[i];
                var code = NormProcessCode(step.process_code);
                var departmentCode = ResolveDepartmentCode(code);

                var start = plannedStart.AddMinutes(minutesPerTask * i);
                var end = i == selectedSteps.Count - 1
                    ? plannedEnd
                    : plannedStart.AddMinutes(minutesPerTask * (i + 1));

                result.Add(new SuggestionTaskPreviewDto
                {
                    process_code = code,
                    process_name = step.process_name ?? step.process_code ?? code,
                    department_code = departmentCode,
                    department_name = ResolveDepartmentName(departmentCode),
                    machine = ResolveTaskMachineFromProcess(step),
                    seq_num = step.seq_num,
                    planned_start_time = start,
                    planned_end_time = end
                });
            }

            return result;
        }

        private static string BuildSuggestionKey(SuggestedGroupProductionDto suggestion)
        {
            var type = suggestion.suggestion_type ?? "";
            var method = suggestion.production_method ?? "";
            var orders = string.Join("-", (suggestion.suggest_order ?? new List<int>())
                .Distinct()
                .OrderBy(x => x));

            var processes = string.Join("-", (suggestion.suggest_process ?? new List<string>())
                .Select(NormProcessCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex));

            return $"{type}:{method}:ORDERS={orders}:PROCESSES={processes}";
        }

        private List<SuggestedGroupProductionDto> BuildSuggestionPreviewFromSelectedCodes(
    List<GroupOrderRow> rows,
    List<string> selectedCodes)
        {
            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            rows = rows
                .Where(x => IsGroupableProductionMethod(x.SingleProd.prod_method))
                .ToList();

            if (selectedCodes.Count == 0 || rows.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            /*
             * Chỉ lấy order có đủ toàn bộ selectedCodes.
             */
            var rowsHavingAllCodes = rows
                .Where(row => selectedCodes.All(code =>
                    row.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (rowsHavingAllCodes.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var result = new List<SuggestedGroupProductionDto>();

            /*
             * Nhóm theo material key tổng hợp của toàn bộ selected codes.
             * Không nhóm theo prod_method.
             */
            var materialGroups = rowsHavingAllCodes
                .GroupBy(row => BuildCompositeGroupMaterialKey(selectedCodes, row))
                .Where(g => g.Count() >= 2)
                .ToList();

            foreach (var mg in materialGroups)
            {
                var memberRows = mg
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                result.Add(new SuggestedGroupProductionDto
                {
                    suggestion_type = "GROUP_WITH_AUTO_SPLIT",

                    suggest_order = memberRows
                        .Select(x => x.Order.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList(),

                    suggest_process = selectedCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),

                    department_code = "DEPT_2",
                    department_name = "Phủ - Cán - Bồi",

                    material_key = mg.Key,
                    production_method = ResolveGroupProductionMethodLabel(memberRows),

                    can_group = true,
                    create_group_allowed = true,

                    reason = NoteGroupSuggestion,
                    note = NoteGroupSuggestion
                });
            }

            return result;
        }

        private List<SuggestedGroupProductionDto> BuildAutoDept2Suggestions(
    List<GroupOrderRow> rows)
        {
            rows = rows
                .Where(x => IsGroupableProductionMethod(x.SingleProd.prod_method))
                .ToList();

            if (rows.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var possibleGroupCodes = GroupableDept2Codes
                .Where(code =>
                    rows.Count(row =>
                        row.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)) >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (possibleGroupCodes.Count == 0)
                return new List<SuggestedGroupProductionDto>();

            return BuildSuggestionPreviewFromSelectedCodes(
                rows,
                possibleGroupCodes);
        }
       
        private static string? CombineMaterialKeys(
            string? a,
            string? b)
        {
            var keys = new List<string>();

            if (!string.IsNullOrWhiteSpace(a))
                keys.AddRange(a.Split(" | ", StringSplitOptions.RemoveEmptyEntries));

            if (!string.IsNullOrWhiteSpace(b))
                keys.AddRange(b.Split(" | ", StringSplitOptions.RemoveEmptyEntries));

            keys = keys
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return keys.Count == 0
                ? null
                : string.Join(" | ", keys);
        }
        public async Task<GroupProductionTaskContextDto?> GetTaskContextAsync(
    int taskId,
    CancellationToken ct = default)
        {
            var current = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Include(x => x.prod)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (current == null)
                return null;

            task? previous = null;

            if (current.prod_id.HasValue)
            {
                var currentSeq = current.seq_num ?? int.MaxValue;

                previous = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x =>
                        x.prod_id == current.prod_id.Value &&
                        x.task_id != current.task_id &&
                        (x.seq_num ?? int.MaxValue) < currentSeq)
                    .OrderByDescending(x => x.seq_num)
                    .ThenByDescending(x => x.task_id)
                    .FirstOrDefaultAsync(ct);
            }

            return new GroupProductionTaskContextDto
            {
                task_id = current.task_id,
                prod_id = current.prod_id,
                prod_kind = current.prod?.prod_kind,
                process_code = current.process?.process_code,
                process_name = current.process?.process_name,
                status = current.status,

                previous_task = previous == null
                    ? null
                    : new TaskPreviousInfoDto
                    {
                        task_id = previous.task_id,
                        prod_id = previous.prod_id,
                        seq_num = previous.seq_num,
                        process_code = previous.process?.process_code,
                        process_name = previous.process?.process_name,
                        status = previous.status,
                        start_time = previous.start_time,
                        end_time = previous.end_time
                    }
            };
        }

        public async Task StartAsync(int groupProdId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == groupProdId, ct);

            if (prod == null)
                throw new KeyNotFoundException("Production not found.");

            if (!string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("API này chỉ dùng cho production GROUP/SPLIT.");
            }

            if (!string.Equals(prod.status, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prod.status, "Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chỉ production Scheduled mới được bắt đầu. Trạng thái hiện tại: {prod.status}");
            }

            var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                _db,
                groupProdId,
                ct);

            if (!dep.can_start)
            {
                throw new InvalidOperationException(
                    "Chưa thể bắt đầu production vì công đoạn trước đó chưa hoàn thành. " +
                    dep.message);
            }

            var now = AppTime.NowVnUnspecified();

            prod.status = "InProcessing";
            prod.actual_start_date ??= now;

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
                throw new InvalidOperationException("Lệnh sản xuất này không phải lệnh sản xuất ghép.");

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

                    report_image_url = x.report_image_url,

                    reference_input_json = x.reference_input_json,
                    material_usage_json = x.material_usage_json,
                    output_json = x.output_json
                })
                .ToListAsync(ct);

            foreach (var log in logs)
            {
                log.report_image_urls = SplitImageUrls(log.report_image_url);
            }

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

            var previousOutputQty = prod.group_total_qty > 0
                ? (decimal)prod.group_total_qty
                : orderRows.Sum(x => x.qty);

            var previousOutputName = "Bán thành phẩm từ các order ghép";

            foreach (var task in tasks)
            {
                var taskLogs = logs
                    .Where(x => x.task_id == task.task_id)
                    .OrderBy(x => x.log_time)
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

                var baseGroupQty = prod.group_total_qty > 0
    ? prod.group_total_qty
    : orderRows.Sum(x => x.qty);

                var io = BuildGroupStageIO(
                    task.process?.process_code,
                    task.process?.process_name,
                    baseGroupQty,
                    previousOutputQty,
                    previousOutputName,
                    taskLogs);

                /*
                 * FIX CHÍNH:
                 * Luôn lấy estimate từ qr-prepare bundle trước.
                 * Nhờ vậy task chưa finish/chưa có log vẫn hiển thị estimated input material.
                 *
                 * Ví dụ task CAN chưa finish:
                 * - BuildGroupStageIO cũ tạo LAMINATION estimated_qty = 0
                 * - qrBundle.consumable_materials có MANG_12MIC estimated_input_qty
                 * - Sau merge, detail sẽ hiển thị Màng cán 12 mic estimated_qty đúng.
                 */
                var qrBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                    task.task_id,
                    ct);

                ApplyQrPrepareEstimateToGroupStageIo(
                    io.inputs,
                    io.outputs,
                    qrBundle,
                    task.process?.process_code,
                    task.process?.process_name,
                    previousOutputQty,
                    previousOutputName);

                /*
                 * Log thực tế vẫn apply sau cùng để override actual_qty.
                 * Estimate giữ theo qr-prepare nếu log chưa có.
                 */
                ApplyTaskLogJsonToGroupStageIo(
                    io.inputs,
                    io.outputs,
                    taskLogs);

                /*
                 * actualOutput ưu tiên output_json.
                 * Nếu output_json trống thì fallback qty_good.
                 */
                var actualOutput = ResolveGroupActualOutputQty(taskLogs);

                var estimatedOutputQty = ResolveEstimatedGroupOutputQty(
                    io.outputs,
                    io.estimatedOutputQty,
                    previousOutputQty);

                var stage = new GroupProductionStageDto
                {
                    task_id = task.task_id,
                    seq_num = task.seq_num,
                    process_code = task.process?.process_code,
                    process_name = task.process?.process_name,
                    status = task.status,
                    start_time = task.start_time,
                    end_time = task.end_time,

                    estimated_output_qty = estimatedOutputQty,

                    actual_output_qty = actualOutput > 0
                        ? actualOutput
                        : io.outputs.Sum(x => x.actual_qty),

                    report_image_urls = taskLogs
                        .SelectMany(x => x.report_image_urls)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),

                    input_materials = io.inputs,
                    outputs = io.outputs,
                    logs = taskLogs,
                    allocations = stageAllocations
                };

                stages.Add(stage);

                /*
                 * Previous stage cho stage kế tiếp:
                 * ưu tiên output_json actual > qty_good > estimated.
                 */
                var firstOutput = io.outputs.FirstOrDefault();

                previousOutputQty =
    stage.actual_output_qty > 0
        ? stage.actual_output_qty
        : firstOutput?.estimated_qty > 0
            ? firstOutput.estimated_qty
            : estimatedOutputQty;

                previousOutputName =
                    firstOutput?.name
                    ?? $"BTP sau {task.process?.process_code}";
            }

            var previousStageContext = await BuildPreviousStageContextForGroupAsync(
                prod,
                tasks,
                orderRows,
                ct);

            var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                _db,
                prod.prod_id,
                ct);

            var isScheduled =
                string.Equals(prod.status, "Scheduled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prod.status, "Unassigned", StringComparison.OrdinalIgnoreCase);

            var canStart = isScheduled && dep.can_start;

            var canStartMessage = canStart
                ? "Có thể bắt đầu lệnh sản xuất."
                : !isScheduled
                    ? $"Production đang ở trạng thái {prod.status}, không thể start bằng API start."
                    : dep.message;

            var displayTotalQty = ResolveGroupProductionDetailTotalQty(
    prod,
    orderRows,
    stages);

            var preview = await BuildProductionGroupPreviewForDetailAsync(
                prod,
                orderRows,
                productTypeName,
                ct);

            return new GroupProductionDetailDto
            {
                prod_id = prod.prod_id,
                code = prod.code,
                status = prod.status,
                can_start = canStart,
                can_start_message = canStartMessage,

                product_type_id = prod.product_type_id,
                product_type_name = productTypeName,

                planned_start_date = prod.planned_start_date,
                planned_end_date = prod.planned_end_date,
                actual_start_date = prod.actual_start_date,
                end_date = prod.end_date,

                issue_file = prod.sub_product_issue_file,
                total_qty = displayTotalQty,
                process_codes = prod.group_process_codes,

                orders = orderRows,
                stages = stages,
                previous_stage_context = previousStageContext,

                preview = preview
            };
        }

        private async Task<GroupProductionConfirmPreviewResponse> BuildProductionGroupPreviewForDetailAsync(
    production groupProd,
    List<GroupProductionOrderDto> orderRows,
    string? productTypeName,
    CancellationToken ct)
        {
            orderRows ??= new List<GroupProductionOrderDto>();

            var orderIds = orderRows
                .Select(x => x.order_id)
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var productTypeId = groupProd.product_type_id ?? 0;

            var orders = await BuildSuggestionOrdersForDetailAsync(
                orderRows,
                productTypeName,
                ct);

            /*
             * 1. Private stages: lấy từ single production của từng order.
             * Đây là các batch riêng trước group, ví dụ RALO,CAT,IN.
             */
            var privateStages = await BuildPrivateStagesForExistingGroupDetailAsync(
                orderRows,
                ct);

            /*
             * 2. Group stage: lấy từ chính group production hiện tại.
             * Đây là batch GROUP chung, ví dụ PHU,CAN,BOI.
             */
            var groupStages = await BuildGroupStagesForExistingGroupDetailAsync(
                groupProd,
                orderRows,
                ct);

            /*
             * 3. Split stages: lấy từ các SPLIT production của từng order.
             * Đây là các batch riêng sau group, ví dụ BE,DUT,DAN.
             */
            var splitStages = await BuildSplitStagesForExistingGroupDetailAsync(
                groupProd,
                orderRows,
                ct);

            var timeline = new List<GroupProductionScheduleStageDto>();
            timeline.AddRange(privateStages);
            timeline.AddRange(groupStages);
            timeline.AddRange(splitStages);

            timeline = timeline
                .OrderBy(x => x.planned_start_date)
                .ThenBy(x => StageOrder(x.stage_type))
                .ThenBy(x =>
                    x.order_ids == null || x.order_ids.Count == 0
                        ? int.MaxValue
                        : x.order_ids.Min())
                .ToList();

            var start = timeline.Count > 0
                ? timeline.Min(x => x.planned_start_date)
                : groupProd.planned_start_date ?? AppTime.NowVnUnspecified();

            var end = timeline.Count > 0
                ? timeline.Max(x => x.planned_end_date)
                : groupProd.planned_end_date ?? start;

            /*
             * FIX LỖI LINQ:
             * Không dùng:
             * .Select(x => x.delivery_date!.Value)
             * .DefaultIfEmpty(end)
             * .MinAsync(ct)
             *
             * Vì EF Core/Npgsql không translate được DefaultIfEmpty(end).
             *
             * Cách an toàn:
             * - Query delivery_date ra list trước.
             * - Nếu không có ngày giao thì fallback = end.
             */
            var deliveryDates = orderIds.Count == 0
                ? new List<DateTime>()
                : await _db.orders
                    .AsNoTracking()
                    .Where(x =>
                        orderIds.Contains(x.order_id) &&
                        x.delivery_date != null)
                    .Select(x => x.delivery_date!.Value)
                    .ToListAsync(ct);

            var commonDeadline = deliveryDates.Count > 0
                ? deliveryDates.Min()
                : end;

            var daysLate = Math.Max(
                0,
                (end.Date - commonDeadline.Date).Days);

            var batches = productTypeId > 0
                ? await BuildBatchesFromGroupPreviewAsync(
                    new GroupProductionConfirmPreviewResponse
                    {
                        timeline = timeline
                    },
                    orders,
                    productTypeId,
                    ct)
                : new List<SuggestionBatchPreviewDto>();

            var groupProcessCodes = ParseProcessCodes(groupProd.group_process_codes);

            return new GroupProductionConfirmPreviewResponse
            {
                suggestion_type = "GROUP_WITH_AUTO_SPLIT",

                /*
                 * Đây là detail của group đã tạo rồi, không phải suggestion để create tiếp.
                 */
                can_group = false,
                create_group_allowed = false,

                product_type_id = groupProd.product_type_id,
                product_type_name = productTypeName,
                production_method = groupProd.prod_method,

                order_count = orderRows.Count,
                order_codes = orders
                    .Select(x => x.order_code)
                    .ToList(),

                orders = orders,
                batches = batches,

                order_ids = orderIds,
                process_codes = groupProcessCodes,
                selected_process_codes = groupProcessCodes,

                common_delivery_deadline = commonDeadline,
                suggested_planned_start_date = start,
                estimated_finish_date = end,
                total_duration_days = Math.Max(
                    1,
                    (end.Date - start.Date).Days),

                dept1_private_stage = privateStages.Count == 0
                    ? null
                    : BuildAggregateStageForDetail(
                        deptCode: "PRIVATE_BEFORE_GROUP",
                        deptName: "Công đoạn riêng trước ghép nhóm",
                        stageType: "SINGLE_PRIVATE",
                        stages: privateStages,
                        note: NotePrivateBeforeGroup),

                private_stages = privateStages,
                group_stages = groupStages,
                split_stages = splitStages,
                timeline = timeline,

                can_meet_common_deadline = daysLate == 0,
                days_late_if_any = daysLate,

                warnings = new List<GroupProductionPlanWarningDto>(),
                notes = new List<string>(),

                reason = NoteGroupSuggestion,
                note = NoteGroupSuggestion
            };
        }

        private async Task<List<SuggestionOrderPreviewDto>> BuildSuggestionOrdersForDetailAsync(
    List<GroupProductionOrderDto> orderRows,
    string? productTypeName,
    CancellationToken ct)
        {
            var orderIds = orderRows
                .Select(x => x.order_id)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count == 0)
                return new List<SuggestionOrderPreviewDto>();

            var rows = await (
                from o in _db.orders.AsNoTracking()
                join pr in _db.productions.AsNoTracking()
                    on o.order_id equals pr.order_id
                join oi0 in _db.order_items.AsNoTracking()
                    on o.order_id equals oi0.order_id into oij
                from oi in oij
                    .OrderBy(x => x.item_id)
                    .Take(1)
                    .DefaultIfEmpty()
                where orderIds.Contains(o.order_id)
                      && pr.prod_kind == "SINGLE"
                select new
                {
                    Order = o,
                    SingleProd = pr,
                    Item = oi
                }
            ).ToListAsync(ct);

            return rows
                .OrderBy(x => x.Order.delivery_date)
                .ThenBy(x => x.Order.order_id)
                .Select(x => new SuggestionOrderPreviewDto
                {
                    order_id = x.Order.order_id,
                    order_code = x.Order.code,
                    single_prod_id = x.SingleProd.prod_id,

                    product_type_id = x.Item != null ? x.Item.product_type_id : x.SingleProd.product_type_id,
                    product_type_name = productTypeName,

                    product_name = x.Item?.product_name,
                    quantity = x.Item?.quantity ?? 0,
                    production_process = x.Item?.production_process,

                    production_method = x.SingleProd.prod_method,
                    delivery_date = x.Order.delivery_date
                })
                .ToList();
        }

        private static GroupProductionScheduleStageDto BuildStageFromExistingTasks(
    string deptCode,
    string deptName,
    string stageType,
    List<int> orderIds,
    List<task> tasks,
    DateTime fallbackStart,
    DateTime fallbackEnd,
    string note)
        {
            tasks ??= new List<task>();

            var processCodes = tasks
                .Select(x => NormProcessCode(x.process?.process_code))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            var start = tasks
                .Where(x => x.planned_start_time.HasValue)
                .Select(x => x.planned_start_time!.Value)
                .DefaultIfEmpty(fallbackStart)
                .Min();

            var end = tasks
                .Where(x => x.planned_end_time.HasValue)
                .Select(x => x.planned_end_time!.Value)
                .DefaultIfEmpty(fallbackEnd)
                .Max();

            if (end < start)
                end = start;

            return new GroupProductionScheduleStageDto
            {
                dept_code = deptCode,
                dept_name = deptName,
                stage_type = stageType,

                process_codes = processCodes,
                order_ids = orderIds,

                planned_start_date = start,
                planned_end_date = end,
                duration_days = Math.Max(1, (end.Date - start.Date).Days),

                note = note
            };
        }

        private async Task<List<GroupProductionScheduleStageDto>> BuildPrivateStagesForExistingGroupDetailAsync(
    List<GroupProductionOrderDto> orderRows,
    CancellationToken ct)
        {
            var result = new List<GroupProductionScheduleStageDto>();

            var singleProdIds = orderRows
                .Select(x => x.single_prod_id)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (singleProdIds.Count == 0)
                return result;

            var singleProds = await _db.productions
                .AsNoTracking()
                .Where(x => singleProdIds.Contains(x.prod_id))
                .ToDictionaryAsync(x => x.prod_id, ct);

            var tasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && singleProdIds.Contains(x.prod_id.Value))
                .ToListAsync(ct);

            var privateCodeSet = Dept1Codes
                .Select(NormProcessCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var order in orderRows.OrderBy(x => x.order_id))
            {
                if (order.single_prod_id <= 0)
                    continue;

                var prodTasks = tasks
                    .Where(x => x.prod_id == order.single_prod_id)
                    .Where(x => privateCodeSet.Contains(NormProcessCode(x.process?.process_code)))
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .ToList();

                if (prodTasks.Count == 0)
                    continue;

                singleProds.TryGetValue(order.single_prod_id, out var singleProd);

                var fallbackStart =
                    singleProd?.planned_start_date ??
                    prodTasks.FirstOrDefault()?.planned_start_time ??
                    AppTime.NowVnUnspecified();

                var fallbackEnd =
                    singleProd?.planned_end_date ??
                    prodTasks.LastOrDefault()?.planned_end_time ??
                    fallbackStart;

                var stage = BuildStageFromExistingTasks(
                    deptCode: "PRIVATE_BEFORE_GROUP",
                    deptName: "Công đoạn riêng trước ghép nhóm",
                    stageType: "SINGLE_PRIVATE",
                    orderIds: new List<int> { order.order_id },
                    tasks: prodTasks,
                    fallbackStart: fallbackStart,
                    fallbackEnd: fallbackEnd,
                    note: NotePrivateBeforeGroup);

                result.Add(stage);
            }

            return result;
        }


        private static bool IsGroupSubProductionForDetail(production prod)
        {
            return prod != null &&
                   string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);
        }

        private static int CeilPositiveInt(decimal value, int fallback)
        {
            if (value <= 0)
                return fallback > 0 ? fallback : 1;

            return Math.Max(1, (int)Math.Ceiling(value));
        }

        private static int ResolveBaseGroupOrderTotalQty(
            production prod,
            List<GroupProductionOrderDto> orderRows)
        {
            if (prod != null && prod.group_total_qty > 0)
                return prod.group_total_qty;

            var fromOrders = orderRows?
                .Where(x => x != null)
                .Sum(x => x.qty) ?? 0;

            return fromOrders > 0 ? fromOrders : 1;
        }

        private static bool IsGroupSubPlannedInput(
            GroupStageMaterialDto input)
        {
            if (input == null)
                return false;

            var code = NormGroupDetailCode(input.code);
            var name = NormGroupDetailCode(input.name);

            /*
             * BTP input chính của công đoạn group:
             * - PREV
             * - IN
             * - PHU
             * - CAN...
             * - hoặc tên có Bán thành phẩm/BTP
             */
            if (code is "PREV" or "INPUT" or "BTP" or "REFERENCE")
                return true;

            if (code is "RALO" or "CAT" or "IN" or "PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN")
                return true;

            return name.Contains("BAN_THANH_PHAM") ||
                   name.Contains("BTP") ||
                   name.Contains("CONG_DOAN") ||
                   name.Contains("GIAY_DA_CAT");
        }

        private static decimal ResolveGroupSubQtyWithWasteFromStages(
            List<GroupProductionStageDto> stages)
        {
            if (stages == null || stages.Count == 0)
                return 0m;

            var candidates = new List<decimal>();

            foreach (var stage in stages)
            {
                if (stage == null)
                    continue;

                /*
                 * estimated_output_qty đã được merge từ qr-prepare.
                 * Với case của bạn PHU/CAN = 6290.
                 */
                if (stage.estimated_output_qty > 0)
                    candidates.Add(stage.estimated_output_qty);

                if (stage.outputs != null)
                {
                    foreach (var output in stage.outputs)
                    {
                        if (output == null)
                            continue;

                        if (output.estimated_qty > 0)
                            candidates.Add(output.estimated_qty);
                    }
                }

                if (stage.input_materials != null)
                {
                    foreach (var input in stage.input_materials)
                    {
                        if (input == null)
                            continue;

                        if (!IsGroupSubPlannedInput(input))
                            continue;

                        if (input.estimated_qty > 0)
                            candidates.Add(input.estimated_qty);
                    }
                }
            }

            return candidates
                .Where(x => x > 0)
                .DefaultIfEmpty(0m)
                .Max();
        }

        private async Task<List<GroupProductionScheduleStageDto>> BuildGroupStagesForExistingGroupDetailAsync(
    production groupProd,
    List<GroupProductionOrderDto> orderRows,
    CancellationToken ct)
        {
            var tasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == groupProd.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return new List<GroupProductionScheduleStageDto>();

            var orderIds = orderRows
                .Select(x => x.order_id)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var fallbackStart =
                groupProd.planned_start_date ??
                tasks.FirstOrDefault()?.planned_start_time ??
                AppTime.NowVnUnspecified();

            var fallbackEnd =
                groupProd.planned_end_date ??
                tasks.LastOrDefault()?.planned_end_time ??
                fallbackStart;

            return new List<GroupProductionScheduleStageDto>
    {
        BuildStageFromExistingTasks(
            deptCode: "DEPT_2",
            deptName: "Phủ - Cán - Bồi",
            stageType: "GROUP",
            orderIds: orderIds,
            tasks: tasks,
            fallbackStart: fallbackStart,
            fallbackEnd: fallbackEnd,
            note: NoteGroupDept2)
    };
        }

        private async Task<List<GroupProductionScheduleStageDto>> BuildSplitStagesForExistingGroupDetailAsync(
    production groupProd,
    List<GroupProductionOrderDto> orderRows,
    CancellationToken ct)
        {
            var result = new List<GroupProductionScheduleStageDto>();

            var orderIds = orderRows
                .Select(x => x.order_id)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count == 0)
                return result;

            /*
             * Vì schema hiện chưa có field parent_group_prod_id,
             * tạm xác định SPLIT theo:
             * - cùng order_id
             * - prod_kind = SPLIT
             * - không Cancelled
             * - planned_start_date >= group.planned_end_date hoặc trong vùng kế hoạch của group.
             */
            var groupEnd = groupProd.planned_end_date ?? groupProd.planned_start_date;

            var splitProds = await _db.productions
                .AsNoTracking()
                .Where(x =>
                    x.order_id.HasValue &&
                    orderIds.Contains(x.order_id.Value) &&
                    x.prod_kind == "SPLIT" &&
                    x.status != "Cancelled")
                .Where(x =>
                    groupEnd == null ||
                    x.planned_start_date == null ||
                    x.planned_start_date >= groupEnd)
                .OrderBy(x => x.order_id)
                .ThenBy(x => x.planned_start_date)
                .ThenBy(x => x.prod_id)
                .ToListAsync(ct);

            if (splitProds.Count == 0)
                return result;

            var splitProdIds = splitProds
                .Select(x => x.prod_id)
                .Distinct()
                .ToList();

            var tasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && splitProdIds.Contains(x.prod_id.Value))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            foreach (var splitProd in splitProds)
            {
                if (!splitProd.order_id.HasValue)
                    continue;

                var splitTasks = tasks
                    .Where(x => x.prod_id == splitProd.prod_id)
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .ToList();

                if (splitTasks.Count == 0)
                    continue;

                var fallbackStart =
                    splitProd.planned_start_date ??
                    splitTasks.FirstOrDefault()?.planned_start_time ??
                    groupProd.planned_end_date ??
                    AppTime.NowVnUnspecified();

                var fallbackEnd =
                    splitProd.planned_end_date ??
                    splitTasks.LastOrDefault()?.planned_end_time ??
                    fallbackStart;

                var stage = BuildStageFromExistingTasks(
                    deptCode: "SPLIT",
                    deptName: "Công đoạn riêng sau ghép nhóm",
                    stageType: "SPLIT",
                    orderIds: new List<int> { splitProd.order_id.Value },
                    tasks: splitTasks,
                    fallbackStart: fallbackStart,
                    fallbackEnd: fallbackEnd,
                    note: NoteSplitAfterGroup);

                stage.split_prod_id = splitProd.prod_id;

                result.Add(stage);
            }

            return result;
        }

        private static GroupProductionScheduleStageDto BuildAggregateStageForDetail(
    string deptCode,
    string deptName,
    string stageType,
    List<GroupProductionScheduleStageDto> stages,
    string note)
        {
            stages ??= new List<GroupProductionScheduleStageDto>();

            var processCodes = stages
                .SelectMany(x => x.process_codes ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            var orderIds = stages
                .SelectMany(x => x.order_ids ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var start = stages.Count == 0
                ? AppTime.NowVnUnspecified()
                : stages.Min(x => x.planned_start_date);

            var end = stages.Count == 0
                ? start
                : stages.Max(x => x.planned_end_date);

            return new GroupProductionScheduleStageDto
            {
                dept_code = deptCode,
                dept_name = deptName,
                stage_type = stageType,

                process_codes = processCodes,
                order_ids = orderIds,

                planned_start_date = start,
                planned_end_date = end,
                duration_days = Math.Max(1, (end.Date - start.Date).Days),

                note = note
            };
        }

        private static int ResolveGroupProductionDetailTotalQty(
            production prod,
            List<GroupProductionOrderDto> orderRows,
            List<GroupProductionStageDto> stages)
        {
            var baseQty = ResolveBaseGroupOrderTotalQty(
                prod,
                orderRows);

            /*
             * Chỉ GROUP + SUB mới đổi total_qty theo số lượng đã cộng hao phí.
             * Các case khác giữ nguyên group_total_qty để tránh ảnh hưởng flow NVL/BOTH.
             */
            if (!IsGroupSubProductionForDetail(prod))
                return baseQty;

            var qtyWithWaste = ResolveGroupSubQtyWithWasteFromStages(
                stages);

            /*
             * Không bao giờ để total_qty nhỏ hơn tổng quantity order ghép.
             * Ví dụ:
             * baseQty = 6000
             * qtyWithWaste = 6290
             * => total_qty = 6290
             */
            var finalQty = Math.Max(
                baseQty,
                CeilPositiveInt(qtyWithWaste, baseQty));

            return finalQty;
        }

        private static decimal ResolveEstimatedGroupOutputQty(
    List<GroupStageMaterialDto>? outputs,
    decimal fallbackEstimatedOutput,
    decimal previousOutputQty)
        {
            var fromOutputs = outputs?
                .Where(x => x != null)
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            if (fromOutputs > 0)
                return Math.Round(fromOutputs, 4);

            if (fallbackEstimatedOutput > 0)
                return Math.Round(fallbackEstimatedOutput, 4);

            if (previousOutputQty > 0)
                return Math.Round(previousOutputQty, 4);

            return 0m;
        }

        private static void ApplyQrPrepareEstimateToGroupStageIo(
            List<GroupStageMaterialDto> inputs,
            List<GroupStageMaterialDto> outputs,
            TaskQrMaterialBundleDto? qrBundle,
            string? processCode,
            string? processName,
            decimal previousOutputQty,
            string? previousOutputName)
        {
            if (inputs == null)
                return;

            if (outputs == null)
                return;

            if (qrBundle == null)
                return;

            /*
             * 1. Merge BTP/reference input estimate.
             * Đây là dòng "BTP sau phủ" hoặc "Bán thành phẩm từ công đoạn IN".
             */
            ApplyQrReferenceInputEstimateToGroupInputs(
                inputs,
                qrBundle.reference_inputs,
                previousOutputQty,
                previousOutputName);

            /*
             * 2. Merge NVL/consumable estimate.
             * Đây là các dòng như:
             * - KEO_PHU_NUOC
             * - MANG_12MIC
             * - KEO_BOI
             * - WAVE...
             */
            ApplyQrConsumableEstimateToGroupInputs(
                inputs,
                qrBundle.consumable_materials);

            /*
             * 3. Đồng bộ output estimate.
             * Nếu BTP input estimate là 6290 thì output estimate của task cũng nên là 6290,
             * trừ khi sau này có rule riêng làm giảm output.
             */
            ApplyQrEstimateToGroupOutputs(
                outputs,
                qrBundle.reference_inputs,
                processCode,
                processName);
        }

        private static void ApplyQrReferenceInputEstimateToGroupInputs(
            List<GroupStageMaterialDto> inputs,
            IReadOnlyList<TaskReferenceInputDto>? referenceInputs,
            decimal previousOutputQty,
            string? previousOutputName)
        {
            var refs = referenceInputs?
                .Where(x => x != null)
                .Where(x => x.estimated_qty > 0 || x.actual_qty_prev_stage > 0)
                .ToList() ?? new List<TaskReferenceInputDto>();

            decimal estimateQty = 0m;

            if (refs.Count > 0)
            {
                /*
                 * Với GROUP + SUB, qr-prepare đã normalize:
                 * estimated_qty = planned BTP input có hao phí.
                 */
                estimateQty = refs.Sum(x =>
                    x.estimated_qty > 0
                        ? x.estimated_qty
                        : x.actual_qty_prev_stage);
            }
            else if (previousOutputQty > 0)
            {
                estimateQty = previousOutputQty;
            }

            if (estimateQty <= 0)
                return;

            estimateQty = Math.Round(estimateQty, 4);

            var firstRef = refs.FirstOrDefault();

            var input = inputs.FirstOrDefault(x =>
                IsGroupPrevInputCode(x.code) ||
                IsLikelyGroupBtpInput(x) ||
                refs.Any(r => SameGroupDetailCode(r.input_code, x.code)));

            if (input == null)
            {
                input = new GroupStageMaterialDto
                {
                    code = firstRef?.input_code ?? "PREV",
                    name = firstRef?.input_name
                           ?? previousOutputName
                           ?? "Bán thành phẩm từ công đoạn trước",
                    unit = firstRef?.unit ?? "tờ",
                    estimated_qty = estimateQty,
                    actual_qty = 0
                };

                inputs.Insert(0, input);
                return;
            }

            if (!string.IsNullOrWhiteSpace(firstRef?.input_code) &&
                IsGroupPrevInputCode(input.code))
            {
                /*
                 * Nếu đang là PREV thì có thể giữ PREV hoặc đổi sang IN/PHU.
                 * Để ít ảnh hưởng FE, giữ code PREV nếu FE đang dùng PREV.
                 */
                input.code = string.IsNullOrWhiteSpace(input.code)
                    ? firstRef.input_code
                    : input.code;
            }

            if (!string.IsNullOrWhiteSpace(firstRef?.input_name))
                input.name = firstRef.input_name;
            else if (!string.IsNullOrWhiteSpace(previousOutputName))
                input.name = previousOutputName;

            if (!string.IsNullOrWhiteSpace(firstRef?.unit))
                input.unit = NormalizeGroupUnit(firstRef.unit);

            input.estimated_qty = estimateQty;

            /*
             * Không set actual_qty ở đây.
             * actual_qty chỉ lấy từ log sau khi task finish.
             */
        }

        private static void ApplyQrConsumableEstimateToGroupInputs(
            List<GroupStageMaterialDto> inputs,
            IReadOnlyList<TaskConsumableMaterialDto>? consumables)
        {
            var mats = consumables?
                .Where(x => x != null)
                .Where(x => x.estimated_input_qty > 0)
                .Where(x => x.is_mapped)
                .ToList() ?? new List<TaskConsumableMaterialDto>();

            if (mats.Count == 0)
                return;

            foreach (var mat in mats)
            {
                var estimatedQty = Math.Round(mat.estimated_input_qty, 4);

                if (estimatedQty <= 0)
                    continue;

                var input = inputs.FirstOrDefault(x =>
                    MatchesQrConsumableWithGroupInput(x, mat));

                if (input == null)
                {
                    inputs.Add(new GroupStageMaterialDto
                    {
                        code = mat.material_code,
                        name = mat.material_name,
                        unit = mat.unit,
                        estimated_qty = estimatedQty,
                        actual_qty = 0
                    });

                    continue;
                }

                /*
                 * Nếu BuildGroupStageIO tạo placeholder như LAMINATION/COATING,
                 * đổi sang material thật để FE thấy rõ.
                 */
                if (!string.IsNullOrWhiteSpace(mat.material_code))
                    input.code = mat.material_code;

                if (!string.IsNullOrWhiteSpace(mat.material_name))
                    input.name = mat.material_name;

                if (!string.IsNullOrWhiteSpace(mat.unit))
                    input.unit = mat.unit;

                input.estimated_qty = estimatedQty;

                /*
                 * Không set actual_qty ở đây.
                 * actual_qty sẽ được ApplyTaskLogJsonToGroupStageIo set từ material_usage_json.
                 */
            }
        }

        private static void ApplyQrEstimateToGroupOutputs(
            List<GroupStageMaterialDto> outputs,
            IReadOnlyList<TaskReferenceInputDto>? referenceInputs,
            string? processCode,
            string? processName)
        {
            if (outputs == null)
                return;

            var estimatedInputQty = referenceInputs?
                .Where(x => x != null)
                .Where(x => x.estimated_qty > 0 || x.actual_qty_prev_stage > 0)
                .Sum(x => x.estimated_qty > 0 ? x.estimated_qty : x.actual_qty_prev_stage)
                ?? 0m;

            if (estimatedInputQty <= 0)
                return;

            estimatedInputQty = Math.Round(estimatedInputQty, 4);

            var code = NormGroupDetailCode(processCode);

            var output = outputs.FirstOrDefault(x =>
                SameGroupDetailCode(x.code, code));

            if (output == null)
            {
                output = new GroupStageMaterialDto
                {
                    code = code,
                    name = ResolveGroupOutputNameForDetail(code, processName),
                    unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ",
                    estimated_qty = estimatedInputQty,
                    actual_qty = 0
                };

                outputs.Add(output);
                return;
            }

            /*
             * Chỉ update estimate.
             * actual_qty sẽ được set từ output_json/qty_good sau khi task finish.
             */
            output.estimated_qty = estimatedInputQty;

            if (string.IsNullOrWhiteSpace(output.name))
                output.name = ResolveGroupOutputNameForDetail(code, processName);

            if (string.IsNullOrWhiteSpace(output.unit))
                output.unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ";
        }

        private static bool MatchesQrConsumableWithGroupInput(
            GroupStageMaterialDto input,
            TaskConsumableMaterialDto mat)
        {
            if (input == null || mat == null)
                return false;

            var inputCode = NormGroupDetailCode(input.code);
            var inputName = NormGroupDetailCode(input.name);

            var matCode = NormGroupDetailCode(mat.material_code);
            var matName = NormGroupDetailCode(mat.material_name);

            if (!string.IsNullOrWhiteSpace(matCode) &&
                SameGroupDetailCode(inputCode, matCode))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(matName) &&
                SameGroupDetailCode(inputName, matName))
            {
                return true;
            }

            /*
             * Match placeholder cũ:
             * - COATING -> KEO_PHU_NUOC
             * - LAMINATION -> MANG_12MIC
             * - KEO_BOI -> keo bồi
             * - WAVE -> sóng carton
             */
            if (IsGroupCoatingCode(inputCode) && IsQrCoatingMaterial(matCode, matName))
                return true;

            if (IsGroupLaminationCode(inputCode) && IsQrLaminationMaterial(matCode, matName))
                return true;

            if (IsGroupMountingGlueCode(inputCode) && IsQrMountingGlueMaterial(matCode, matName))
                return true;

            if (inputCode == "WAVE" && IsQrWaveMaterial(matCode, matName))
                return true;

            return false;
        }

        private static bool IsLikelyGroupBtpInput(GroupStageMaterialDto input)
        {
            if (input == null)
                return false;

            var code = NormGroupDetailCode(input.code);
            var name = NormGroupDetailCode(input.name);

            if (code is "PREV" or "INPUT" or "BTP" or "REFERENCE")
                return true;

            if (code is "RALO" or "CAT" or "IN" or "PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN")
                return true;

            return name.Contains("BAN_THANH_PHAM") ||
                   name.Contains("BTP") ||
                   name.Contains("CONG_DOAN") ||
                   name.Contains("GIAY_DA_CAT");
        }

        private static bool IsQrCoatingMaterial(string? code, string? name)
        {
            var c = NormGroupDetailCode(code);
            var n = NormGroupDetailCode(name);

            return c.Contains("KEO_PHU") ||
                   c.Contains("PHU_NUOC") ||
                   c.Contains("COATING") ||
                   n.Contains("KEO_PHU") ||
                   n.Contains("PHU_NUOC") ||
                   n.Contains("COATING");
        }

        private static bool IsQrLaminationMaterial(string? code, string? name)
        {
            var c = NormGroupDetailCode(code);
            var n = NormGroupDetailCode(name);

            return c.Contains("MANG") ||
                   c.Contains("LAMINATION") ||
                   c.Contains("CAN_MANG") ||
                   n.Contains("MANG") ||
                   n.Contains("LAMINATION") ||
                   n.Contains("CAN_MANG");
        }

        private static bool IsQrMountingGlueMaterial(string? code, string? name)
        {
            var c = NormGroupDetailCode(code);
            var n = NormGroupDetailCode(name);

            return c.Contains("KEO_BOI") ||
                   c.Contains("MOUNTING") ||
                   n.Contains("KEO_BOI") ||
                   n.Contains("MOUNTING");
        }

        private static bool IsQrWaveMaterial(string? code, string? name)
        {
            var c = NormGroupDetailCode(code);
            var n = NormGroupDetailCode(name);

            return c.Contains("SONG") ||
                   c.Contains("WAVE") ||
                   n.Contains("SONG") ||
                   n.Contains("WAVE");
        }

        private static string NormalizeGroupUnit(string? unit)
        {
            if (string.IsNullOrWhiteSpace(unit))
                return "tờ";

            var u = unit.Trim();

            if (string.Equals(u, "sp", StringComparison.OrdinalIgnoreCase))
                return "tờ";

            return u;
        }

        private static string ResolveGroupOutputNameForDetail(
            string? processCode,
            string? processName)
        {
            var code = NormGroupDetailCode(processCode);

            return code switch
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
        }

        private async Task<GroupProductionConfirmPreviewResponse> PreviewCoreAsync(
    CreateGroupProductionRequest req,
    bool validateAgainstSuggestion,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            var orderIds = req.order_ids
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (orderIds.Count == 0)
                throw new InvalidOperationException("Cần truyền ít nhất 1 order_id để preview.");

            /*
             * CASE MỚI:
             * Nếu chỉ truyền 1 order_id thì trả preview sản xuất đơn.
             * Không validate suggestion group.
             * Không cần process_codes.
             */
            if (orderIds.Count == 1)
            {
                return await BuildSingleOrderPreviewResponseAsync(
                    orderIds[0],
                    req.planned_start_date,
                    ct);
            }

            /*
             * CASE GROUP:
             * Từ 2 order trở lên thì giữ logic ghép nhóm hiện tại.
             */
            var selectedCodes = NormalizeSelectedCodesForGroup(req.process_codes);

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn PHU/CAN/BOI để preview ghép.");

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            if (validateAgainstSuggestion)
            {
                await ValidateManualSelectionMatchesCurrentSuggestionAsync(
                    orderIds,
                    selectedCodes,
                    ct);
            }

            var rows = await LoadGroupOrderRowsAsync(
                orderIds,
                selectedCodes,
                ct);

            if (rows.Count != orderIds.Count)
                throw new InvalidOperationException("Một số order không tồn tại hoặc chưa có production riêng.");

            var invalidStatusOrders = rows
                .Where(x =>
                    !string.Equals(x.Order.status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(x.SingleProd.status, "Pending", StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                    $"{x.Order.order_id}(order={x.Order.status}, production={x.SingleProd.status})")
                .ToList();

            if (invalidStatusOrders.Count > 0)
            {
                throw new InvalidOperationException(
                    "Chỉ order/production Pending mới được preview ghép. " +
                    $"Không hợp lệ: {string.Join(", ", invalidStatusOrders)}");
            }

            if (rows.Any(x => x.Item == null))
                throw new InvalidOperationException("Một số order chưa có order_item.");

            if (rows.Any(x => !x.Order.layout_confirmed || !x.Order.is_production_ready))
                throw new InvalidOperationException("Tất cả order phải xác nhận layout và sẵn sàng sản xuất.");

            var productTypeIds = rows
                .Select(x => x.Item.product_type_id)
                .Distinct()
                .ToList();

            if (productTypeIds.Count != 1 || productTypeIds[0] == null)
                throw new InvalidOperationException("Các order phải cùng product_type.");

            var productTypeId = productTypeIds[0]!.Value;

            var productTypeName = await _db.product_types
                .AsNoTracking()
                .Where(x => x.product_type_id == productTypeId)
                .Select(x => x.name)
                .FirstOrDefaultAsync(ct);

            ValidateRowsHaveGroupableProductionMethodOrThrow(
                rows,
                "preview ghép/tách production");

            ValidateRowsHaveNoStartedTaskOrThrow(
                rows,
                "preview ghép/tách production");

            var plan = BuildDepartmentProductionPlan(
                rows,
                selectedCodes,
                out var warnings);

            var groupSegments = plan
                .Where(x => x.IsGroup)
                .ToList();

            if (groupSegments.Count == 0)
                throw new InvalidOperationException("Không có công đoạn PHU/CAN/BOI đủ điều kiện để preview GROUP.");

            var commonDeadline = ResolveCommonDeadline(rows);
            var suggestedStart = req.planned_start_date?.Date ?? ResolveSuggestedStart(commonDeadline);

            var privateStages = BuildPrivateStagesBeforeGroup(
                rows,
                plan,
                suggestedStart);

            var allPrivateCodes = privateStages
                .SelectMany(x => x.process_codes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            var aggregatePrivateStage = allPrivateCodes.Count == 0
                ? null
                : BuildStageDto(
                    deptCode: "PRIVATE_BEFORE_GROUP",
                    deptName: "Công đoạn riêng trước ghép nhóm",
                    stageType: "SINGLE_PRIVATE",
                    processCodes: allPrivateCodes,
                    orderIds: orderIds,
                    start: suggestedStart,
                    durationDays: Dept1Days,
                    note: NotePrivateBeforeGroup);

            var privateEnd = privateStages.Count == 0
                ? suggestedStart
                : privateStages.Max(x => x.planned_end_date);

            var groupStages = new List<GroupProductionScheduleStageDto>();

            foreach (var segment in groupSegments)
            {
                groupStages.Add(BuildStageDto(
                    deptCode: "DEPT_2",
                    deptName: "Phủ - Cán - Bồi",
                    stageType: "GROUP",
                    processCodes: segment.ProcessCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),
                    orderIds: segment.Members
                        .Select(x => x.Order.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList(),
                    start: privateEnd,
                    durationDays: Dept2Days,
                    note: NoteGroupDept2));
            }

            var groupEnd = groupStages.Count == 0
                ? privateEnd
                : groupStages.Max(x => x.planned_end_date);

            var splitStages = new List<GroupProductionScheduleStageDto>();

            foreach (var segment in plan.Where(x => !x.IsGroup))
            {
                splitStages.Add(BuildStageDto(
                    deptCode: "SPLIT",
                    deptName: "Công đoạn riêng sau ghép nhóm",
                    stageType: "SPLIT",
                    processCodes: segment.ProcessCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),
                    orderIds: segment.Members
                        .Select(x => x.Order.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList(),
                    start: groupEnd,
                    durationDays: Dept3Days,
                    note: NoteSplitAfterGroup));
            }

            var timeline = new List<GroupProductionScheduleStageDto>();
            timeline.AddRange(privateStages);
            timeline.AddRange(groupStages);
            timeline.AddRange(splitStages);

            timeline = timeline
                .OrderBy(x => x.planned_start_date)
                .ThenBy(x => StageOrder(x.stage_type))
                .ThenBy(x =>
                    x.order_ids == null || x.order_ids.Count == 0
                        ? int.MaxValue
                        : x.order_ids.Min())
                .ToList();

            var estimatedFinish = timeline.Count == 0
                ? suggestedStart
                : timeline.Max(x => x.planned_end_date);

            var daysLate = Math.Max(
                0,
                (estimatedFinish.Date - commonDeadline.Date).Days);

            var orders = BuildSuggestionOrders(
                rows,
                productTypeName);

            var batches = await BuildBatchesFromGroupPreviewAsync(
                new GroupProductionConfirmPreviewResponse
                {
                    timeline = timeline
                },
                orders,
                productTypeId,
                ct);

            return new GroupProductionConfirmPreviewResponse
            {
                suggestion_type = "GROUP_WITH_AUTO_SPLIT",
                can_group = true,
                create_group_allowed = true,

                product_type_id = productTypeId,
                product_type_name = productTypeName,
                production_method = ResolveGroupProductionMethodLabel(rows),

                order_count = rows.Count,
                order_codes = orders.Select(x => x.order_code).ToList(),
                orders = orders,
                batches = batches,

                order_ids = orderIds,
                process_codes = selectedCodes,
                selected_process_codes = selectedCodes,

                common_delivery_deadline = commonDeadline,
                suggested_planned_start_date = suggestedStart,
                estimated_finish_date = estimatedFinish,
                total_duration_days = Math.Max(1, (estimatedFinish.Date - suggestedStart.Date).Days),

                dept1_private_stage = aggregatePrivateStage,
                private_stages = privateStages,
                group_stages = groupStages,
                split_stages = splitStages,
                timeline = timeline,

                can_meet_common_deadline = daysLate == 0,
                days_late_if_any = daysLate,
                warnings = warnings ?? new List<GroupProductionPlanWarningDto>(),
                notes = new List<string>(),

                reason = NoteGroupSuggestion,
                note = NoteGroupSuggestion
            };
        }

        private async Task<GroupProductionConfirmPreviewResponse> BuildSingleOrderPreviewResponseAsync(
    int orderId,
    DateTime? plannedStartDate,
    CancellationToken ct)
        {
            if (orderId <= 0)
                throw new InvalidOperationException("order_id không hợp lệ.");

            /*
             * Load đúng 1 order.
             * Không truyền selectedCodes vì single preview cần full route.
             */
            var rows = await LoadGroupOrderRowsAsync(
                new List<int> { orderId },
                new List<string>(),
                ct);

            if (rows.Count == 0)
                throw new InvalidOperationException("Order không tồn tại hoặc chưa có production riêng.");

            var row = rows.First();

            if (row.Item == null)
                throw new InvalidOperationException("Order chưa có order_item.");

            if (!string.Equals(row.Order.status, "Pending", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.SingleProd.status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chỉ order/production Pending mới được preview sản xuất đơn. " +
                    $"Hiện tại order={row.Order.status}, production={row.SingleProd.status}.");
            }

            if (!row.Order.layout_confirmed || !row.Order.is_production_ready)
                throw new InvalidOperationException("Order phải xác nhận layout và sẵn sàng sản xuất.");

            if (row.SingleProd.planned_start_date != null || row.SingleProd.planned_end_date != null)
                throw new InvalidOperationException("Production đã có ngày kế hoạch, không còn là preview chưa lập lịch.");

            if (row.HasAnyStartedTask)
                throw new InvalidOperationException("Production đã có task bắt đầu hoặc có log, không thể preview như đơn chưa lập lịch.");

            var method = ResolveRowProductionMethodOrNull(row);

            if (string.IsNullOrWhiteSpace(method))
                throw new InvalidOperationException("Production chưa có prod_method hợp lệ.");

            /*
             * BOTH vẫn được preview single.
             * Chỉ không cho BOTH ghép nhóm.
             */
            var productTypeId = row.Item.product_type_id;

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                throw new InvalidOperationException("Order chưa có product_type_id.");

            var productTypeName = await _db.product_types
                .AsNoTracking()
                .Where(x => x.product_type_id == productTypeId.Value)
                .Select(x => x.name)
                .FirstOrDefaultAsync(ct);

            var routeCodes = row.RouteCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (routeCodes.Count == 0)
                throw new InvalidOperationException("Order chưa có production_process hợp lệ.");

            var commonDeadline = ResolveCommonDeadline(new List<GroupOrderRow> { row });

            var suggestedStart = plannedStartDate?.Date ?? ResolveSuggestedStart(commonDeadline);

            var plannedEnd = suggestedStart.AddDays(MinProductionDays);

            var tasks = await BuildTaskPreviewDtosAsync(
                productTypeId.Value,
                routeCodes,
                suggestedStart,
                plannedEnd,
                ct);

            var orderDto = new SuggestionOrderPreviewDto
            {
                order_id = row.Order.order_id,
                order_code = row.Order.code,
                single_prod_id = row.SingleProd.prod_id,
                product_type_id = row.Item.product_type_id,
                product_type_name = productTypeName,
                product_name = row.Item.product_name,
                quantity = row.Item.quantity,
                production_process = row.Item.production_process,
                production_method = method,
                delivery_date = row.Order.delivery_date
            };

            var batch = new SuggestionBatchPreviewDto
            {
                batch_type = "SINGLE",
                prod_kind = "SINGLE",
                department_code = "FULL_PATH",
                department_name = "Full production path",

                order_ids = new List<int> { row.Order.order_id },
                order_codes = new List<string?> { row.Order.code },

                process_codes = routeCodes,

                planned_start_date = suggestedStart,
                planned_end_date = plannedEnd,
                duration_days = MinProductionDays,

                tasks = tasks,
                note = NoteSinglePreview
            };

            var stage = BuildStageDto(
                deptCode: "FULL_PATH",
                deptName: "Full production path",
                stageType: "SINGLE",
                processCodes: routeCodes,
                orderIds: new List<int> { row.Order.order_id },
                start: suggestedStart,
                durationDays: MinProductionDays,
                note: NoteSinglePreview);

            var daysLate = Math.Max(
                0,
                (plannedEnd.Date - commonDeadline.Date).Days);

            return new GroupProductionConfirmPreviewResponse
            {
                suggestion_type = "SINGLE_PREVIEW",
                can_group = false,
                create_group_allowed = false,

                product_type_id = productTypeId.Value,
                product_type_name = productTypeName,
                production_method = method,

                order_count = 1,
                order_codes = new List<string?> { row.Order.code },
                orders = new List<SuggestionOrderPreviewDto> { orderDto },
                batches = new List<SuggestionBatchPreviewDto> { batch },

                order_ids = new List<int> { row.Order.order_id },
                process_codes = routeCodes,
                selected_process_codes = routeCodes,

                common_delivery_deadline = commonDeadline,
                suggested_planned_start_date = suggestedStart,
                estimated_finish_date = plannedEnd,
                total_duration_days = MinProductionDays,

                dept1_private_stage = null,
                private_stages = new List<GroupProductionScheduleStageDto>(),
                group_stages = new List<GroupProductionScheduleStageDto>(),
                split_stages = new List<GroupProductionScheduleStageDto>(),
                timeline = new List<GroupProductionScheduleStageDto> { stage },

                can_meet_common_deadline = daysLate == 0,
                days_late_if_any = daysLate,

                warnings = new List<GroupProductionPlanWarningDto>(),
                notes = new List<string>(),

                reason = NoteSinglePreview,
                note = NoteSinglePreview
            };
        }

        public async Task<GroupProductionConfirmPreviewResponse> PreviewAsync(
    CreateGroupProductionRequest req,
    CancellationToken ct = default)
        {

            return await PreviewCoreAsync(
                req,
                validateAgainstSuggestion: true,
                ct);
        }

        private static int StageOrder(string? stageType)
        {
            return NormProcessCode(stageType) switch
            {
                "SINGLE_PRIVATE" => 1,
                "GROUP" => 2,
                "SPLIT" => 3,
                _ => 99
            };
        }

        private static List<string> BuildPrivateBeforeFirstGroupCodes(
    List<GroupOrderRow> rows,
    List<ProductionPlanSegment> plan)
        {
            var groupedCodeByOrderId = plan
                .Where(x => x.IsGroup)
                .SelectMany(segment => segment.Members.Select(member => new
                {
                    order_id = member.Order.order_id,
                    codes = segment.ProcessCodes
                }))
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(x => x.codes)
                        .Select(NormProcessCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!groupedCodeByOrderId.TryGetValue(row.Order.order_id, out var groupedCodes) ||
                    groupedCodes.Count == 0)
                    continue;

                var firstGroupedIndex = row.RouteCodes
                    .Select((code, index) => new
                    {
                        code = NormProcessCode(code),
                        index
                    })
                    .Where(x => groupedCodes.Contains(x.code, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.index)
                    .DefaultIfEmpty(-1)
                    .Min();

                if (firstGroupedIndex <= 0)
                    continue;

                foreach (var code in row.RouteCodes.Take(firstGroupedIndex))
                {
                    var norm = NormProcessCode(code);
                    if (!string.IsNullOrWhiteSpace(norm))
                        result.Add(norm);
                }
            }

            return result
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private async Task ValidateManualSelectionMatchesCurrentSuggestionAsync(
    List<int> selectedOrderIds,
    List<string> selectedCodes,
    CancellationToken ct)
        {
            selectedOrderIds = selectedOrderIds
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            if (selectedOrderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để preview/tạo lệnh ghép.");

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn để preview/tạo lệnh ghép.");

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            var currentSuggestions = await BuildCurrentSuggestionsForManualSelectionAsync(
                selectedCodes,
                ct);

            var groupSuggestions = currentSuggestions
                .Where(x => x.can_group || x.create_group_allowed)
                .Where(x => !string.Equals(
                    x.suggestion_type,
                    "SINGLE_PREVIEW",
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => x.suggest_order != null && x.suggest_order.Count >= 2)
                .Where(x => x.suggest_process != null && x.suggest_process.Count > 0)
                .ToList();

            var matchedSuggestion = groupSuggestions.FirstOrDefault(s =>
                IsSelectedOrderSubsetOfSuggestion(
                    suggestionOrderIds: s.suggest_order,
                    selectedOrderIds: selectedOrderIds)
                &&
                IsSelectedProcessSubsetOfSuggestion(
                    s.suggest_process,
                    selectedCodes));

            if (matchedSuggestion != null)
                return;

            var validSuggestionText = groupSuggestions.Count == 0
                ? "Không có suggestion ghép hợp lệ."
                : string.Join(" | ", groupSuggestions.Select(FormatSuggestionForManualSelectionError));

            var selectedText =
                $"Order chọn=[{string.Join(",", selectedOrderIds)}], " +
                $"Process chọn=[{string.Join(",", selectedCodes)}]";

            throw new InvalidOperationException(
                "Tổ hợp order/process bạn chọn không thuộc bất kỳ suggestion hợp lệ nào hiện tại. " +
                "Bạn được phép chọn một phần order trong suggestion, nhưng tất cả order đã chọn phải cùng nằm trong một suggestion. " +
                $"{selectedText}. " +
                $"Suggestion hợp lệ hiện tại: {validSuggestionText}");
        }

        private async Task<List<SuggestedGroupProductionDto>> BuildCurrentSuggestionsForManualSelectionAsync(
    List<string> selectedCodes,
    CancellationToken ct)
        {
            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            if (selectedCodes.Count == 0)
                return new List<SuggestedGroupProductionDto>();

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            /*
             * Load các order Pending đủ điều kiện.
             * Hàm này đã loại BOTH và giữ NVL/SUB.
             */
            var allCleanRows = await LoadCleanRowsForSuggestionAsync(
                productTypeId: null,
                selectedCodes: selectedCodes,
                ct: ct);

            if (allCleanRows.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var result = new List<SuggestedGroupProductionDto>();

            /*
             * Logic mới:
             * - Chỉ group theo product_type.
             * - Không group theo prod_method.
             * - NVL + SUB được ghép chung.
             */
            foreach (var productTypeGroup in allCleanRows
                .Where(x => x.Item != null)
                .Where(x => x.Item.product_type_id.HasValue)
                .Where(x => IsGroupableProductionMethod(x.SingleProd.prod_method))
                .GroupBy(x => x.Item.product_type_id!.Value)
                .OrderBy(g => g.Key))
            {
                var rowsOfOneProductType = productTypeGroup
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                var suggestions = BuildSuggestionPreviewFromSelectedCodes(
                    rowsOfOneProductType,
                    selectedCodes)
                    .Where(x => !string.Equals(
                        x.suggestion_type,
                        "SPLIT_ONLY",
                        StringComparison.OrdinalIgnoreCase))
                    .Where(x => x.suggest_order != null && x.suggest_order.Count >= 2)
                    .Where(x => x.suggest_process != null && x.suggest_process.Count > 0)
                    .ToList();

                foreach (var s in suggestions)
                {
                    var memberRows = rowsOfOneProductType
                        .Where(x => s.suggest_order.Contains(x.Order.order_id))
                        .ToList();

                    s.product_type_id = productTypeGroup.Key;
                    s.production_method = ResolveGroupProductionMethodLabel(memberRows);
                    s.can_group = true;
                    s.create_group_allowed = true;
                    s.suggestion_key = BuildSuggestionKey(s);
                    s.reason = NoteGroupSuggestion;
                    s.note = NoteGroupSuggestion;

                    result.Add(s);
                }
            }

            return result;
        }

        private static bool IsSelectedOrderSubsetOfSuggestion(
            List<int>? suggestionOrderIds,
            List<int> selectedOrderIds)
        {
            var suggestionSet = (suggestionOrderIds ?? new List<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToHashSet();

            var selectedSet = selectedOrderIds
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (selectedSet.Count < 2)
                return false;

            /*
             * Quan trọng:
             * Không check bằng nhau nữa.
             * Chỉ cần selected là tập con của suggestion.
             */
            return selectedSet.All(suggestionSet.Contains);
        }

        private static bool SameProcessSet(
    List<string>? a,
    List<string> b)
        {
            var aa = (a ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            var bb = b
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            return aa.SequenceEqual(bb, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsSelectedProcessSubsetOfSuggestion(
    List<string>? suggestionProcessCodes,
    List<string> selectedProcessCodes)
        {
            var suggestionSet = (suggestionProcessCodes ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var selectedSet = selectedProcessCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedSet.Count == 0)
                return false;

            return selectedSet.All(x => suggestionSet.Contains(x));
        }

        private static string FormatSuggestionForManualSelectionError(
    SuggestedGroupProductionDto s)
        {
            var orders = s.suggest_order == null
                ? ""
                : string.Join(",", s.suggest_order.Distinct().OrderBy(x => x));

            var processes = s.suggest_process == null
                ? ""
                : string.Join(",", s.suggest_process
                    .Select(NormProcessCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex));

            return
                $"orders=[{orders}], " +
                $"process=[{processes}], " +
                $"method={s.production_method}, " +
                $"product_type_id={s.product_type_id}, " +
                $"material_key={s.material_key}";
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

        private async Task<GroupProductionPreviousStageContextDto?> BuildPreviousStageContextForGroupAsync(
    production groupProd,
    List<task> groupTasks,
    List<GroupProductionOrderDto> orderRows,
    CancellationToken ct)
        {
            var firstGroupTask = groupTasks
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .FirstOrDefault();

            if (firstGroupTask == null)
                return null;

            var currentCode = GroupProductionHelper.Norm(firstGroupTask.process?.process_code);

            if (string.IsNullOrWhiteSpace(currentCode))
                return null;

            var result = new GroupProductionPreviousStageContextDto
            {
                current_group_task_id = firstGroupTask.task_id,
                current_group_prod_id = groupProd.prod_id,
                current_process_code = firstGroupTask.process?.process_code,
                current_process_name = firstGroupTask.process?.process_name,
                previous_process_code = null,
                all_previous_finished = true
            };

            foreach (var orderRow in orderRows)
            {
                var route = await GetOrderRouteCodesAsync(orderRow.order_id, ct);

                var previousCode = ResolvePreviousProcessCode(route, currentCode);

                if (!string.IsNullOrWhiteSpace(previousCode))
                    result.previous_process_code ??= previousCode;

                if (string.IsNullOrWhiteSpace(previousCode))
                {
                    result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                    {
                        order_id = orderRow.order_id,
                        order_code = orderRow.order_code,
                        previous_process_code = null,
                        is_finished = true,
                        message = $"Order {orderRow.order_id}: công đoạn {currentCode} là công đoạn đầu tiên trong path, không có công đoạn trước."
                    });

                    continue;
                }

                var previousTask = await FindPreviousProcessTaskForOrderAsync(
                    orderRow.order_id,
                    previousCode,
                    ct);

                if (previousTask == null)
                {
                    result.all_previous_finished = false;

                    result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                    {
                        order_id = orderRow.order_id,
                        order_code = orderRow.order_code,
                        previous_process_code = previousCode,
                        previous_task_status = null,
                        is_finished = false,
                        message = $"Order {orderRow.order_id}: không tìm thấy task công đoạn trước {previousCode}."
                    });

                    continue;
                }

                var isFinished = IsTaskFinished(
                    previousTask.status,
                    previousTask.end_time);

                if (!isFinished)
                    result.all_previous_finished = false;

                result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                {
                    order_id = orderRow.order_id,
                    order_code = orderRow.order_code,

                    previous_task_id = previousTask.task_id,
                    previous_prod_id = previousTask.prod_id,
                    previous_prod_kind = previousTask.prod_kind,
                    previous_seq_num = previousTask.seq_num,

                    previous_process_code = previousTask.process_code,
                    previous_process_name = previousTask.process_name,

                    previous_task_status = previousTask.status,
                    previous_start_time = previousTask.start_time,
                    previous_end_time = previousTask.end_time,

                    is_finished = isFinished,

                    message = isFinished
                        ? $"Order {orderRow.order_id}: công đoạn trước {previousCode} đã Finished."
                        : $"Order {orderRow.order_id}: công đoạn trước {previousCode} chưa Finished."
                });
            }

            return result;
        }

        private async Task<List<string>> GetOrderRouteCodesAsync(
    int orderId,
    CancellationToken ct)
        {
            var processCsv = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => x.production_process)
                .FirstOrDefaultAsync(ct);

            return ParseProcessCodes(processCsv);
        }

        private static string? ResolvePreviousProcessCode(
            List<string> routeCodes,
            string currentProcessCode)
        {
            if (routeCodes == null || routeCodes.Count == 0)
                return null;

            var current = GroupProductionHelper.Norm(currentProcessCode);

            var currentIndex = routeCodes.FindIndex(x =>
                string.Equals(
                    GroupProductionHelper.Norm(x),
                    current,
                    StringComparison.OrdinalIgnoreCase));

            if (currentIndex <= 0)
                return null;

            return routeCodes[currentIndex - 1];
        }

        private sealed class PreviousProcessTaskRef
        {
            public int task_id { get; set; }

            public int? prod_id { get; set; }

            public string? prod_kind { get; set; }

            public int? seq_num { get; set; }

            public string? process_code { get; set; }

            public string? process_name { get; set; }

            public string? status { get; set; }

            public DateTime? start_time { get; set; }

            public DateTime? end_time { get; set; }
        }

        private static bool IsDept1(string code)
            => Dept1Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static bool IsDept2(string code)
            => Dept2Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static bool IsDept3(string code)
            => Dept3Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static int FullRouteIndex(string? processCode)
        {
            var code = NormProcessCode(processCode);

            var idx = Array.FindIndex(FullRouteOrder, x =>
                string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

            return idx < 0 ? 999 : idx;
        }

        private static string ResolveDepartmentCode(string processCode)
        {
            var code = NormProcessCode(processCode);

            if (IsDept1(code)) return "DEPT_1";
            if (IsDept2(code)) return "DEPT_2";
            if (IsDept3(code)) return "DEPT_3";

            return "OTHER";
        }

        private static string ResolveDepartmentName(string departmentCode)
        {
            return departmentCode switch
            {
                "DEPT_1" => "Ralo - Cắt - In",
                "DEPT_2" => "Phủ - Cán - Bồi",
                "DEPT_3" => "Bế - Dứt - Dán",
                _ => "Khác"
            };
        }

        private static bool RequiresSameMaterialKey(string processCode)
        {
            var code = NormProcessCode(processCode);

            return code is "PHU" or "CAN" or "CAN_MANG" or "BOI";
        }

        private async Task<PreviousProcessTaskRef?> FindPreviousProcessTaskForOrderAsync(
    int orderId,
    string previousProcessCode,
    CancellationToken ct)
        {
            var previousCode = GroupProductionHelper.Norm(previousProcessCode);

            var directTasks = await (
                from t in _db.tasks.AsNoTracking()

                join p in _db.productions.AsNoTracking()
                    on t.prod_id equals p.prod_id

                join pp in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where p.order_id == orderId
                      && p.status != "Cancelled"

                select new PreviousProcessTaskRef
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    prod_kind = p.prod_kind,
                    seq_num = t.seq_num,
                    process_code = pp != null ? pp.process_code : null,
                    process_name = pp != null ? pp.process_name : null,
                    status = t.status,
                    start_time = t.start_time,
                    end_time = t.end_time
                }
            ).ToListAsync(ct);

            var matchedDirect = directTasks
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => IsTaskFinished(x.status, x.end_time))
                .ThenByDescending(x => x.end_time ?? DateTime.MinValue)
                .ThenByDescending(x => x.task_id)
                .FirstOrDefault();

            if (matchedDirect != null)
                return matchedDirect;

            var linkedTasks = await (
                from tl in _db.task_links.AsNoTracking()

                join gt in _db.tasks.AsNoTracking()
                    on tl.group_task_id equals gt.task_id

                join gp in _db.productions.AsNoTracking()
                    on tl.group_prod_id equals gp.prod_id

                join pp in _db.product_type_processes.AsNoTracking()
                    on gt.process_id equals pp.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where tl.order_id == orderId
                      && gp.status != "Cancelled"

                select new PreviousProcessTaskRef
                {
                    task_id = gt.task_id,
                    prod_id = gt.prod_id,
                    prod_kind = gp.prod_kind,
                    seq_num = gt.seq_num,
                    process_code = pp != null ? pp.process_code : tl.process_code,
                    process_name = pp != null ? pp.process_name : null,
                    status = gt.status,
                    start_time = gt.start_time,
                    end_time = gt.end_time
                }
            ).ToListAsync(ct);

            var matchedLinked = linkedTasks
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => IsTaskFinished(x.status, x.end_time))
                .ThenByDescending(x => x.end_time ?? DateTime.MinValue)
                .ThenByDescending(x => x.task_id)
                .FirstOrDefault();

            if (matchedLinked != null)
                return matchedLinked;

            var qtyRow = await _db.task_qtys
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .ToListAsync(ct);

            var matchedQty = qtyRow
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.created_at)
                .FirstOrDefault();

            if (matchedQty != null)
            {
                return new PreviousProcessTaskRef
                {
                    task_id = (int)matchedQty.single_task_id,
                    prod_id = null,
                    prod_kind = "GROUP_QTY",
                    seq_num = null,
                    process_code = matchedQty.process_code,
                    process_name = matchedQty.process_code,
                    status = "Finished",
                    start_time = null,
                    end_time = matchedQty.created_at
                };
            }

            return null;
        }

        private static bool IsTaskFinished(string? status, DateTime? endTime)
        {
            return string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
                   || endTime != null;
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

        private sealed class GroupOrderRow
        {
            public order Order { get; init; } = null!;
            public production SingleProd { get; init; } = null!;
            public order_item Item { get; init; } = null!;
            public order_request? Request { get; init; }
            public cost_estimate? Estimate { get; init; }
            public List<string> RouteCodes { get; init; } = new();
            public bool HasAnyStartedTask { get; init; }
        }

        private sealed class ProductionPlanSegment
        {
            public string DepartmentCode { get; init; } = "";
            public string DepartmentName { get; init; } = "";

            public List<string> ProcessCodes { get; set; } = new();

            public List<GroupOrderRow> Members { get; set; } = new();

            public string? MaterialKey { get; set; }

            public bool IsGroup => Members.Count >= 2;
        }

        private static string NormProcessCode(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static List<string> ParseProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int DepartmentOrder(string departmentCode)
        {
            return departmentCode switch
            {
                "DEPT_1" => 1,
                "DEPT_2" => 2,
                "DEPT_3" => 3,
                _ => 99
            };
        }

        private static string SafeKey(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "NULL"
                : NormProcessCode(value);
        }

        private string ResolveMaterialGroupKey(string processCode, GroupOrderRow row)
        {
            var code = NormProcessCode(processCode);

            if (code == "PHU")
            {
                var coating = ResolveCoatingMaterialCodeForGroup(row.Estimate);
                return $"PHU:COATING={SafeKey(coating)}";
            }

            if (code is "CAN" or "CAN_MANG")
            {
                var lamination =
                    !string.IsNullOrWhiteSpace(row.Estimate?.lamination_material_code)
                        ? row.Estimate!.lamination_material_code
                        : !string.IsNullOrWhiteSpace(row.Estimate?.lamination_material_name)
                            ? row.Estimate!.lamination_material_name
                            : row.Estimate?.lamination_material_id?.ToString();

                return $"{code}:LAMINATION={SafeKey(lamination)}";
            }

            if (code == "BOI")
            {
                var wave = EstimateMaterialAlternativeHelper.ResolveWaveType(
                    row.Estimate?.wave_alternative,
                    row.Estimate?.wave_type);

                return $"BOI:WAVE={SafeKey(wave)}";
            }

            return $"{code}:NO_MATERIAL_CHECK";
        }

        private async Task<List<GroupOrderRow>> LoadGroupOrderRowsAsync(
    List<int> orderIds,
    CancellationToken ct)
        {
            return await LoadGroupOrderRowsAsync(
                orderIds,
                selectedCodes: new List<string>(),
                ct);
        }

        private async Task<List<GroupOrderRow>> LoadGroupOrderRowsAsync(
            List<int> orderIds,
            List<string>? selectedCodes,
            CancellationToken ct)
        {
            orderIds = orderIds
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            if (orderIds.Count == 0)
                return new List<GroupOrderRow>();

            var rows = await (
                from o in _db.orders
                join pr in _db.productions
                    on o.order_id equals pr.order_id

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

            var singleProdIds = rows
                .Select(x => x.singleProd.prod_id)
                .Distinct()
                .ToList();

            /*
             * FIX:
             * HasAnyStartedTask không còn check giống nhau cho mọi method.
             * NVL: check toàn bộ task.
             * SUB: chỉ check task công đoạn đang group.
             */
            var startedSingleProdIds = await LoadSingleProdIdsHavingStartedTaskAsync(
                singleProdIds,
                selectedCodes,
                ct);

            var result = new List<GroupOrderRow>();

            foreach (var row in rows)
            {
                if (row.item == null)
                    continue;

                var req = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == row.order.order_id)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

                cost_estimate? est = null;

                if (req != null)
                {
                    if (req.accepted_estimate_id.HasValue &&
                        req.accepted_estimate_id.Value > 0)
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

                var itemRouteCodes = ParseProcessCodes(row.item.production_process);
                var estimateRouteCodes = ParseProcessCodes(est?.production_processes);

                var finalRouteCodes = itemRouteCodes.Count > 0
                    ? itemRouteCodes
                    : estimateRouteCodes;

                /*
                 * Chỉ để response dễ đọc.
                 * Không SaveChanges ở đây.
                 */
                if (itemRouteCodes.Count == 0 && estimateRouteCodes.Count > 0)
                {
                    row.item.production_process = est?.production_processes;
                }

                result.Add(new GroupOrderRow
                {
                    Order = row.order,
                    SingleProd = row.singleProd,
                    Item = row.item,
                    Request = req,
                    Estimate = est,
                    RouteCodes = finalRouteCodes,
                    HasAnyStartedTask = startedSingleProdIds.Contains(row.singleProd.prod_id)
                });
            }

            return result;
        }

        private static string? NormalizeProductionMethodForGroup(string? method)
        {
            var value = (method ?? "").Trim().ToUpperInvariant();

            if (value == "NVL" || value == "SUB" || value == "BOTH")
                return value;

            return null;
        }

        private static List<string> NormalizeSelectedCodesForGroup(
            IEnumerable<string>? selectedCodes)
        {
            return (selectedCodes ?? Enumerable.Empty<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static List<string> ResolveSubTaskCheckCodesForGroup(
            List<string>? selectedCodes)
        {
            var codes = NormalizeSelectedCodesForGroup(selectedCodes);

            /*
             * Nếu FE không truyền processCodes thì API auto suggest Dept2.
             * Với SUB, chỉ check Dept2 vì Dept1 có thể đã Finished do dùng BTP.
             */
            if (codes.Count == 0)
            {
                return Dept2Codes
                    .Select(NormProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList();
            }

            return codes;
        }

        private static bool IsTaskStartedForGrouping(
            string? status,
            DateTime? startTime,
            DateTime? endTime,
            bool hasLog)
        {
            if (startTime != null || endTime != null || hasLog)
                return true;

            var s = (status ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(s))
                return false;

            /*
             * Chỉ Unassigned/null được xem là chưa bắt đầu.
             * Ready cũng xem là đã bắt đầu vì task đã được mở/giữ máy/cho phép report.
             */
            return s != "UNASSIGNED";
        }

        private async Task<HashSet<int>> LoadSingleProdIdsHavingStartedTaskAsync(
            List<int> singleProdIds,
            CancellationToken ct)
        {
            return await LoadSingleProdIdsHavingStartedTaskAsync(
                singleProdIds,
                selectedCodes: new List<string>(),
                ct);
        }

        private async Task<HashSet<int>> LoadSingleProdIdsHavingStartedTaskAsync(
            List<int> singleProdIds,
            List<string>? selectedCodes,
            CancellationToken ct)
        {
            singleProdIds = singleProdIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            if (singleProdIds.Count == 0)
                return new HashSet<int>();

            var prodMethodMap = await _db.productions
                .AsNoTracking()
                .Where(x => singleProdIds.Contains(x.prod_id))
                .Select(x => new
                {
                    x.prod_id,
                    x.prod_method
                })
                .ToDictionaryAsync(
                    x => x.prod_id,
                    x => NormalizeProductionMethodForGroup(x.prod_method),
                    ct);

            if (prodMethodMap.Count == 0)
                return new HashSet<int>();

            var taskRows = await (
                from t in _db.tasks.AsNoTracking()

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where t.prod_id.HasValue &&
                      singleProdIds.Contains(t.prod_id.Value)

                select new
                {
                    t.task_id,
                    prod_id = t.prod_id!.Value,
                    process_code = pp != null ? pp.process_code : null,
                    task_name = t.name,
                    t.status,
                    t.start_time,
                    t.end_time
                }
            ).ToListAsync(ct);

            if (taskRows.Count == 0)
                return new HashSet<int>();

            var taskIds = taskRows
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdsWithLogs = await _db.task_logs
                .AsNoTracking()
                .Where(x =>
                    x.task_id.HasValue &&
                    taskIds.Contains(x.task_id.Value))
                .Select(x => x.task_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var logSet = taskIdsWithLogs.ToHashSet();

            var subTaskCheckCodes = ResolveSubTaskCheckCodesForGroup(selectedCodes);

            var startedProdIds = new HashSet<int>();

            foreach (var task in taskRows)
            {
                if (!prodMethodMap.TryGetValue(task.prod_id, out var method))
                    continue;

                var processCode = NormProcessCode(task.process_code);

                if (string.IsNullOrWhiteSpace(processCode))
                    processCode = NormProcessCode(task.task_name);

                /*
                 * CASE NVL:
                 * Check toàn bộ task như logic cũ.
                 * Nếu bất kỳ task nào đã Ready/Finished/log/start/end thì loại production.
                 */
                if (method == "NVL")
                {
                    if (IsTaskStartedForGrouping(
                        task.status,
                        task.start_time,
                        task.end_time,
                        logSet.Contains(task.task_id)))
                    {
                        startedProdIds.Add(task.prod_id);
                    }

                    continue;
                }

                /*
                 * CASE SUB:
                 * Cho phép RALO/CAT/IN đã Finished vì đó là phần lấy BTP.
                 * Chỉ check các công đoạn group đang xét, mặc định là Dept2.
                 */
                if (method == "SUB")
                {
                    if (!subTaskCheckCodes.Contains(
                        processCode,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsTaskStartedForGrouping(
                        task.status,
                        task.start_time,
                        task.end_time,
                        logSet.Contains(task.task_id)))
                    {
                        startedProdIds.Add(task.prod_id);
                    }

                    continue;
                }

                /*
                 * CASE BOTH hoặc method khác:
                 * Giữ chặt như NVL để tránh group sai khi một phần đã chạy.
                 * Nếu sau này muốn BOTH riêng thì tách tiếp.
                 */
                if (IsTaskStartedForGrouping(
                    task.status,
                    task.start_time,
                    task.end_time,
                    logSet.Contains(task.task_id)))
                {
                    startedProdIds.Add(task.prod_id);
                }
            }

            return startedProdIds;
        }

        private List<ProductionPlanSegment> BuildDepartmentProductionPlan(
    List<GroupOrderRow> rows,
    List<string> selectedCodes,
    out List<GroupProductionPlanWarningDto> warnings)
        {
            warnings = new List<GroupProductionPlanWarningDto>();

            ValidateRowsHaveGroupableProductionMethodOrThrow(
                rows,
                "build kế hoạch group");

            selectedCodes = NormalizeSelectedCodesForGroup(selectedCodes);

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            /*
             * Chỉ lấy các code thật sự có trong route của ít nhất 2 order.
             */
            var groupCodes = selectedCodes
                .Where(code =>
                    rows.Count(r =>
                        r.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase) &&
                        IsGroupableProductionMethod(r.SingleProd.prod_method)) >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (groupCodes.Count == 0)
            {
                warnings.Add(new GroupProductionPlanWarningDto
                {
                    process_code = string.Join(",", selectedCodes),
                    reason = "Không có công đoạn phòng ban 2 đủ điều kiện ghép nhóm.",
                    affected_order_ids = rows
                        .Select(x => x.Order.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList()
                });

                return new List<ProductionPlanSegment>();
            }

            /*
             * Rule mới:
             * PHU/CAN/BOI nếu cùng bộ selectedCodes và cùng material key tổng hợp
             * thì gom thành 1 GROUP segment.
             *
             * Ví dụ selectedCodes = PHU,CAN
             * => tạo 1 GROUP production có 2 task PHU, CAN.
             */
            var rowsHavingAllGroupCodes = rows
                .Where(r => groupCodes.All(code =>
                    r.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
                .Where(r => IsGroupableProductionMethod(r.SingleProd.prod_method))
                .ToList();

            var result = new List<ProductionPlanSegment>();

            var compositeMaterialGroups = rowsHavingAllGroupCodes
                .GroupBy(r => BuildCompositeGroupMaterialKey(groupCodes, r))
                .ToList();

            foreach (var mg in compositeMaterialGroups)
            {
                var memberRows = mg
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                if (memberRows.Count < 2)
                {
                    warnings.Add(new GroupProductionPlanWarningDto
                    {
                        process_code = string.Join(",", groupCodes),
                        reason = "Nhóm vật tư chỉ có một đơn hàng nên không tạo lệnh ghép.",
                        affected_order_ids = memberRows
                            .Select(x => x.Order.order_id)
                            .ToList()
                    });

                    continue;
                }

                result.Add(new ProductionPlanSegment
                {
                    DepartmentCode = "DEPT_2",
                    DepartmentName = "Phủ - Cán - Bồi",
                    ProcessCodes = groupCodes,
                    Members = memberRows,
                    MaterialKey = mg.Key
                });
            }

            /*
             * Các công đoạn sau GROUP:
             * - Tách riêng từng order.
             * - BE,DUT,DAN giống RALO,CAT,IN: mỗi order 1 production riêng.
             */
            var groupedCodeByOrderId = result
                .Where(x => x.IsGroup)
                .SelectMany(segment => segment.Members.Select(member => new
                {
                    order_id = member.Order.order_id,
                    codes = segment.ProcessCodes
                }))
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(x => x.codes)
                        .Select(NormProcessCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList());

            foreach (var row in rows.OrderBy(x => x.Order.order_id))
            {
                if (!groupedCodeByOrderId.TryGetValue(row.Order.order_id, out var groupedCodesForOrder) ||
                    groupedCodesForOrder.Count == 0)
                {
                    continue;
                }

                var lastGroupedIndex = row.RouteCodes
                    .Select((code, index) => new
                    {
                        code = NormProcessCode(code),
                        index
                    })
                    .Where(x => groupedCodesForOrder.Contains(x.code, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.index)
                    .DefaultIfEmpty(-1)
                    .Max();

                if (lastGroupedIndex < 0)
                    continue;

                var afterGroupPrivateCodes = row.RouteCodes
                    .Skip(lastGroupedIndex + 1)
                    .Select(NormProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Where(x => !groupedCodesForOrder.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList();

                if (afterGroupPrivateCodes.Count == 0)
                    continue;

                result.Add(new ProductionPlanSegment
                {
                    DepartmentCode = "SPLIT",
                    DepartmentName = "Công đoạn riêng sau ghép nhóm",
                    ProcessCodes = afterGroupPrivateCodes,
                    Members = new List<GroupOrderRow> { row },
                    MaterialKey = $"ORDER:{row.Order.order_id}:AFTER_GROUP"
                });
            }

            return result
                .OrderBy(x => x.IsGroup ? 1 : 2)
                .ThenBy(x => x.Members.Min(m => m.Order.order_id))
                .ToList();
        }

        private static DateTime DateOnlyStart(DateTime value)
        {
            return value.Date;
        }

        private static DateTime MaxDate(DateTime a, DateTime b)
        {
            return a >= b ? a : b;
        }

        private static DateTime MinDate(IEnumerable<DateTime?> values)
        {
            var dates = values
                .Where(x => x.HasValue)
                .Select(x => x!.Value.Date)
                .ToList();

            if (dates.Count == 0)
                throw new InvalidOperationException("Tất cả order phải có delivery_date để ghép.");

            return dates.Min();
        }

        private static DateTime ResolveCommonDeadline(List<GroupOrderRow> rows)
        {
            var earliestDelivery = MinDate(rows.Select(x => x.Order.delivery_date));
            var minDeadline = AppTime.NowVnUnspecified().Date.AddDays(MinProductionDays);

            /*
             * Nếu đơn giao quá gấp, vẫn giữ rule tối thiểu 7 ngày.
             */
            return MaxDate(earliestDelivery, minDeadline);
        }

        private static DateTime ResolveSuggestedStart(DateTime commonDeadline)
        {
            return commonDeadline.Date.AddDays(-MinProductionDays);
        }

        private static GroupProductionScheduleStageDto BuildStageDto(
            string deptCode,
            string deptName,
            string stageType,
            List<string> processCodes,
            List<int> orderIds,
            DateTime start,
            int durationDays,
            string note)
        {
            return new GroupProductionScheduleStageDto
            {
                dept_code = deptCode,
                dept_name = deptName,
                stage_type = stageType,
                process_codes = processCodes,
                order_ids = orderIds,
                planned_start_date = start,
                planned_end_date = start.AddDays(durationDays),
                duration_days = durationDays,
                note = note
            };
        }

        private static DateTime ResolveStageStart(
    GroupProductionConfirmPreviewResponse preview,
    ProductionPlanSegment segment)
        {
            if (segment.IsGroup)
            {
                var matched = preview.group_stages.FirstOrDefault(x =>
                    SameOrderSet(
                        x.order_ids,
                        segment.Members.Select(m => m.Order.order_id).ToList())
                    &&
                    SameProcessCodes(
                        x.process_codes,
                        segment.ProcessCodes));

                if (matched != null)
                    return matched.planned_start_date;

                return preview.group_stages.Count > 0
                    ? preview.group_stages.Min(x => x.planned_start_date)
                    : preview.suggested_planned_start_date;
            }

            var orderIds = segment.Members
                .Select(x => x.Order.order_id)
                .Distinct()
                .ToList();

            var split = preview.split_stages.FirstOrDefault(x =>
                SameOrderSet(x.order_ids, orderIds) &&
                SameProcessCodes(x.process_codes, segment.ProcessCodes));

            if (split != null)
                return split.planned_start_date;

            return preview.suggested_planned_start_date;
        }

        private static DateTime ResolveStageEnd(
            GroupProductionConfirmPreviewResponse preview,
            ProductionPlanSegment segment)
        {
            if (segment.IsGroup)
            {
                var matched = preview.group_stages.FirstOrDefault(x =>
                    SameOrderSet(
                        x.order_ids,
                        segment.Members.Select(m => m.Order.order_id).ToList())
                    &&
                    SameProcessCodes(
                        x.process_codes,
                        segment.ProcessCodes));

                if (matched != null)
                    return matched.planned_end_date;

                return ResolveStageStart(preview, segment).AddDays(Dept2Days);
            }

            var orderIds = segment.Members
                .Select(x => x.Order.order_id)
                .Distinct()
                .ToList();

            var split = preview.split_stages.FirstOrDefault(x =>
                SameOrderSet(x.order_ids, orderIds) &&
                SameProcessCodes(x.process_codes, segment.ProcessCodes));

            if (split != null)
                return split.planned_end_date;

            return ResolveStageStart(preview, segment).AddDays(Dept3Days);
        }

        private static int GetGlobalRouteIndex(
            List<GroupOrderRow> rows,
            string processCode)
        {
            var indexes = rows
                .Select(r => r.RouteCodes.FindIndex(x =>
                    string.Equals(x, processCode, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .ToList();

            return indexes.Count == 0 ? 999 : indexes.Min();
        }

        private static string ShortDepartmentCode(string? departmentCode)
        {
            var code = (departmentCode ?? "").Trim().ToUpperInvariant();

            return code switch
            {
                "DEPT_1" => "D1",
                "DEPT_2" => "D2",
                "DEPT_3" => "D3",
                _ => "DX"
            };
        }

        private static readonly JsonSerializerOptions GroupDetailJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static List<T> ParseGroupJsonArraySafe<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<T>();

            try
            {
                return JsonSerializer.Deserialize<List<T>>(json, GroupDetailJsonOptions)
                       ?? new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }

        private static string NormGroupDetailCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static bool SameGroupDetailCode(string? a, string? b)
        {
            var aa = NormGroupDetailCode(a);
            var bb = NormGroupDetailCode(b);

            if (string.IsNullOrWhiteSpace(aa) || string.IsNullOrWhiteSpace(bb))
                return false;

            return aa == bb;
        }

        private static bool IsGroupPrevInputCode(string? code)
        {
            var c = NormGroupDetailCode(code);

            return c is "PREV" or "INPUT" or "BTP" or "REFERENCE";
        }

        private static bool IsGroupLaminationCode(string? code)
        {
            var c = NormGroupDetailCode(code);

            return c.Contains("LAMINATION")
                || c.Contains("MANG")
                || c.Contains("CAN");
        }

        private static bool IsGroupCoatingCode(string? code)
        {
            var c = NormGroupDetailCode(code);

            return c.Contains("COATING")
                || c.Contains("KEO_PHU")
                || c.Contains("PHU");
        }

        private static bool IsGroupMountingGlueCode(string? code)
        {
            var c = NormGroupDetailCode(code);

            return c.Contains("KEO_BOI")
                || c.Contains("MOUNTING_GLUE");
        }

        private static List<TaskMaterialUsageLogItemDto> ResolveGroupMaterialUsages(
            List<GroupTaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    ParseGroupJsonArraySafe<TaskMaterialUsageLogItemDto>(
                        log.material_usage_json))
                .ToList();
        }

        private static List<TaskReferenceUsageInputDto> ResolveGroupReferenceInputs(
            List<GroupTaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    ParseGroupJsonArraySafe<TaskReferenceUsageInputDto>(
                        log.reference_input_json))
                .ToList();
        }

        private static List<TaskOutputReportDto> ResolveGroupOutputs(
            List<GroupTaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    ParseGroupJsonArraySafe<TaskOutputReportDto>(
                        log.output_json))
                .ToList();
        }

        private static decimal ResolveGroupActualOutputQty(
            List<GroupTaskLogDto> logs)
        {
            var fromOutputJson = ResolveGroupOutputs(logs)
                .Sum(x => x.quantity_good);

            if (fromOutputJson > 0)
                return Math.Round(fromOutputJson, 4);

            return logs.Sum(x => x.qty_good);
        }

        private static void ApplyTaskLogJsonToGroupStageIo(
            List<GroupStageMaterialDto> inputs,
            List<GroupStageMaterialDto> outputs,
            List<GroupTaskLogDto> logs)
        {
            if (logs == null || logs.Count == 0)
                return;

            var materialUsages = ResolveGroupMaterialUsages(logs);
            var referenceInputs = ResolveGroupReferenceInputs(logs);
            var outputReports = ResolveGroupOutputs(logs);

            /*
             * 1. input_materials
             */
            if (inputs != null)
            {
                foreach (var input in inputs)
                {
                    var inputCode = NormGroupDetailCode(input.code);

                    /*
                     * BTP từ công đoạn trước.
                     */
                    var refMatches = referenceInputs
                        .Where(x =>
                            SameGroupDetailCode(x.input_code, input.code)
                            || IsGroupPrevInputCode(input.code))
                        .ToList();

                    if (refMatches.Count > 0)
                    {
                        var used = refMatches.Sum(x => x.quantity_used);
                        var estimated = refMatches.Sum(x => x.quantity_used + x.quantity_left);

                        if (used > 0)
                            input.actual_qty = Math.Round(used, 4);

                        if (input.estimated_qty <= 0 && estimated > 0)
                            input.estimated_qty = Math.Round(estimated, 4);

                        var firstRef = refMatches.FirstOrDefault();

                        if (firstRef != null)
                        {
                            if (string.IsNullOrWhiteSpace(input.name))
                                input.name = firstRef.input_name;

                            if (string.IsNullOrWhiteSpace(input.unit))
                                input.unit = firstRef.unit;
                        }

                        continue;
                    }

                    /*
                     * NVL kho: màng/keo phủ/keo bồi...
                     */
                    var materialMatches = materialUsages
                        .Where(x =>
                            SameGroupDetailCode(x.material_code, input.code)
                            || SameGroupDetailCode(x.material_name, input.name)
                            || (IsGroupLaminationCode(inputCode) && IsGroupLaminationCode(x.material_code))
                            || (IsGroupCoatingCode(inputCode) && IsGroupCoatingCode(x.material_code))
                            || (IsGroupMountingGlueCode(inputCode) && IsGroupMountingGlueCode(x.material_code)))
                        .ToList();

                    if (materialMatches.Count == 0)
                        continue;

                    var usedQty = materialMatches.Sum(x => x.quantity_used);

                    var estimatedQty = materialMatches.Sum(x =>
                        x.estimated_input_qty > 0
                            ? x.estimated_input_qty
                            : x.quantity_used + x.quantity_left + x.quantity_waste);

                    if (usedQty > 0)
                        input.actual_qty = Math.Round(usedQty, 4);

                    if (input.estimated_qty <= 0 && estimatedQty > 0)
                        input.estimated_qty = Math.Round(estimatedQty, 4);

                    var firstMaterial = materialMatches.FirstOrDefault();

                    if (firstMaterial != null)
                    {
                        if (string.IsNullOrWhiteSpace(input.code))
                            input.code = firstMaterial.material_code;

                        if (string.IsNullOrWhiteSpace(input.name))
                            input.name = firstMaterial.material_name;

                        if (string.IsNullOrWhiteSpace(input.unit))
                            input.unit = firstMaterial.unit;
                    }
                }
            }

            /*
             * 2. outputs
             */
            if (outputs != null)
            {
                foreach (var output in outputs)
                {
                    var matches = outputReports
                        .Where(x =>
                            SameGroupDetailCode(x.output_code, output.code)
                            || SameGroupDetailCode(x.output_name, output.name))
                        .ToList();

                    if (matches.Count == 0 && outputReports.Count > 0)
                        matches = outputReports;

                    if (matches.Count == 0)
                        continue;

                    var good = matches.Sum(x => x.quantity_good);
                    var bad = matches.Sum(x => x.quantity_bad);

                    if (good > 0)
                        output.actual_qty = Math.Round(good, 4);

                    var first = matches.FirstOrDefault();

                    if (first != null)
                    {
                        if (string.IsNullOrWhiteSpace(output.code))
                            output.code = first.output_code;

                        if (string.IsNullOrWhiteSpace(output.name))
                            output.name = first.output_name;

                        if (string.IsNullOrWhiteSpace(output.unit))
                            output.unit = first.unit;
                    }

                    if (output.estimated_qty <= 0 && good + bad > 0)
                        output.estimated_qty = Math.Round(good + bad, 4);
                }
            }
        }

        private async Task<string> GenerateShortProductionCodeAsync(
            string prefix,
            string? departmentCode,
            CancellationToken ct)
        {
            var safePrefix = (prefix ?? "PRD")
                .Trim()
                .ToUpperInvariant();

            if (safePrefix.Length > 3)
                safePrefix = safePrefix[..3];

            if (safePrefix.Length < 3)
                safePrefix = safePrefix.PadRight(3, 'X');

            var dept = ShortDepartmentCode(departmentCode);

            for (var i = 0; i < 20; i++)
            {
                var now = AppTime.NowVnUnspecified();

                var code = $"{safePrefix}{dept}{now:MMddHHmmss}{Random.Shared.Next(100, 999)}";

                if (code.Length > 20)
                    code = code[..20];

                var exists = await _db.productions
                    .AsNoTracking()
                    .AnyAsync(x => x.code == code, ct);

                if (!exists)
                    return code;
            }

            var fallback = $"{safePrefix}{dept}{Random.Shared.Next(100000000, 999999999)}";

            return fallback.Length <= 20
                ? fallback
                : fallback[..20];
        }

        private static List<ProductionPlanSegment> MergeAdjacentSegments(
    List<ProductionPlanSegment> segments)
        {
            var result = new List<ProductionPlanSegment>();

            foreach (var seg in segments)
            {
                var last = result.LastOrDefault();

                if (last != null &&
                    last.DepartmentCode == seg.DepartmentCode &&
                    SameMembers(last.Members, seg.Members) &&
                    CanMergeSegmentKey(last.MaterialKey, seg.MaterialKey))
                {
                    last.ProcessCodes = last.ProcessCodes
                        .Concat(seg.ProcessCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList();

                    last.MaterialKey = CombineMaterialKeys(
                        last.MaterialKey,
                        seg.MaterialKey);

                    continue;
                }

                result.Add(new ProductionPlanSegment
                {
                    DepartmentCode = seg.DepartmentCode,
                    DepartmentName = seg.DepartmentName,
                    ProcessCodes = seg.ProcessCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),
                    Members = seg.Members.ToList(),
                    MaterialKey = seg.MaterialKey
                });
            }

            return result;
        }

        private static bool CanMergeSegmentKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return true;

            var methodA = ExtractMethodFromMaterialKey(a);
            var methodB = ExtractMethodFromMaterialKey(b);

            if (!string.IsNullOrWhiteSpace(methodA) || !string.IsNullOrWhiteSpace(methodB))
            {
                return string.Equals(methodA, methodB, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractMethodFromMaterialKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var parts = key.Split('|', StringSplitOptions.RemoveEmptyEntries);

            var methodPart = parts.FirstOrDefault(x =>
                x.Trim().StartsWith("METHOD=", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(methodPart))
                return null;

            return methodPart
                .Trim()
                .Substring("METHOD=".Length)
                .Trim()
                .ToUpperInvariant();
        }

        private static bool SameMembers(
            List<GroupOrderRow> a,
            List<GroupOrderRow> b)
        {
            var aa = a.Select(x => x.Order.order_id).OrderBy(x => x).ToList();
            var bb = b.Select(x => x.Order.order_id).OrderBy(x => x).ToList();

            return aa.SequenceEqual(bb);
        }

        private static string? NormalizeProductionMethod(string? method)
        {
            var value = (method ?? "")
                .Trim()
                .ToUpperInvariant();

            return value switch
            {
                "SUB" => "SUB",
                "NVL" => "NVL",
                "BOTH" => "BOTH",
                _ => null
            };
        }

        private static string? ResolveRowProductionMethodOrNull(GroupOrderRow row)
        {
            if (row?.SingleProd == null)
                return null;

            return NormalizeProductionMethod(row.SingleProd.prod_method);
        }

        private static string ResolveRowProductionMethodOrThrow(GroupOrderRow row)
        {
            var method = ResolveRowProductionMethodOrNull(row);

            if (!string.IsNullOrWhiteSpace(method))
                return method;

            throw new InvalidOperationException(
                $"Order {row.Order.order_id}, single_prod_id={row.SingleProd.prod_id} chưa có prod_method hợp lệ. " +
                $"prod_method hiện tại={ShowText(row.SingleProd.prod_method)}. " +
                $"Chỉ cho phép SUB/NVL/BOTH trước khi ghép đơn.");
        }

        private static string ResolveSegmentProductionMethodOrThrow(ProductionPlanSegment segment)
        {
            if (segment == null)
                throw new InvalidOperationException("segment is required.");

            if (segment.Members == null || segment.Members.Count == 0)
                throw new InvalidOperationException("Segment không có order member.");

            var invalidMembers = segment.Members
                .Where(x => ResolveRowProductionMethodOrNull(x) == null)
                .Select(x =>
                    $"order_id={x.Order.order_id}, single_prod_id={x.SingleProd.prod_id}, prod_method={ShowText(x.SingleProd.prod_method)}")
                .ToList();

            if (invalidMembers.Count > 0)
            {
                throw new InvalidOperationException(
                    "Không thể tạo production GROUP/SPLIT vì có order chưa có prod_method hợp lệ SUB/NVL/BOTH. " +
                    $"Chi tiết: {string.Join(" | ", invalidMembers)}");
            }

            var methods = segment.Members
                .Select(ResolveRowProductionMethodOrThrow)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (methods.Count != 1)
            {
                var detail = segment.Members
                    .Select(x =>
                        $"order_id={x.Order.order_id}, single_prod_id={x.SingleProd.prod_id}, prod_method={ShowText(x.SingleProd.prod_method)}")
                    .ToList();

                throw new InvalidOperationException(
                    "Không thể tạo production GROUP/SPLIT từ các order có prod_method khác nhau. " +
                    "Các order trong cùng GROUP/SPLIT bắt buộc phải cùng prod_method SUB/NVL/BOTH. " +
                    $"Chi tiết: {string.Join(" | ", detail)}");
            }

            return methods[0];
        }

        private static void ValidateRowsHaveGroupableProductionMethodOrThrow(
    List<GroupOrderRow> rows,
    string actionName)
        {
            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException($"Không có order để {actionName}.");

            var invalidRows = rows
                .Where(x => ResolveRowProductionMethodOrNull(x) == null)
                .Select(x =>
                    $"order_id={x.Order.order_id}, single_prod_id={x.SingleProd.prod_id}, prod_method={ShowText(x.SingleProd.prod_method)}")
                .ToList();

            if (invalidRows.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Không thể {actionName} vì có order chưa có prod_method hợp lệ. " +
                    $"Chi tiết: {string.Join(" | ", invalidRows)}");
            }

            var bothRows = rows
                .Where(x => string.Equals(
                    ResolveRowProductionMethodOrNull(x),
                    "BOTH",
                    StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Order.order_id}(prod_id={x.SingleProd.prod_id})")
                .ToList();

            if (bothRows.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Không thể {actionName} vì method BOTH không được ghép. " +
                    $"Các order BOTH phải sản xuất đơn riêng. Order BOTH: {string.Join(", ", bothRows)}");
            }

            var unsupported = rows
                .Where(x => !IsGroupableProductionMethod(x.SingleProd.prod_method))
                .Select(x =>
                    $"order_id={x.Order.order_id}, method={ShowText(x.SingleProd.prod_method)}")
                .ToList();

            if (unsupported.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Không thể {actionName}. Chỉ cho phép ghép NVL/SUB. " +
                    $"Không hợp lệ: {string.Join(" | ", unsupported)}");
            }
        }

        private static string ShowText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(null)" : value.Trim();
        }

        private static string? ResolveCoatingMaterialCodeForGroup(cost_estimate? est)
        {
            if (est == null)
                return null;

            if (!string.IsNullOrWhiteSpace(est.coating_material_code))
                return NormProcessCode(est.coating_material_code);

            var raw = NormProcessCode(est.coating_type);

            return raw switch
            {
                "KEO_NUOC" or "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" or "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" or "KEO_UV" or "PHU_UV" or "KEO_PHU_UV" => "KEO_PHU_UV",
                _ => string.IsNullOrWhiteSpace(raw) ? null : raw
            };
        }

        private string BuildGroupPlanKey(string processCode, GroupOrderRow row)
        {
            /*
             * - NVL và SUB có thể ghép chung nếu cùng product_type,
             *   cùng công đoạn groupable, cùng nhóm vật tư/kỹ thuật.
             */
            var materialKey = ResolveMaterialGroupKey(processCode, row);

            return materialKey;
        }

        private static void ValidateRowsHaveNoStartedTaskOrThrow(
    List<GroupOrderRow> rows,
    string actionName)
        {
            var invalidRows = rows
                .Where(x => x.HasAnyStartedTask)
                .Select(x =>
                {
                    var method = NormalizeProductionMethodForGroup(x.SingleProd.prod_method);

                    return
                        $"order_id={x.Order.order_id}, " +
                        $"single_prod_id={x.SingleProd.prod_id}, " +
                        $"prod_method={method}";
                })
                .ToList();

            if (invalidRows.Count == 0)
                return;

            throw new InvalidOperationException(
                $"Không thể {actionName} vì có order đã bắt đầu công đoạn không được phép ghép. " +
                $"Với NVL: không được có bất kỳ task nào đã bắt đầu. " +
                $"Với SUB: chỉ cho phép RALO/CAT/IN đã Finished do lấy BTP, nhưng công đoạn group đang chọn phải chưa bắt đầu. " +
                $"Chi tiết: {string.Join(" | ", invalidRows)}");
        }

    private static bool IsGroupableDept2Process(string? processCode)
        {
            var code = NormProcessCode(processCode);

            return GroupableDept2Codes.Contains(
                code,
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsPrivateOrderProcess(string? processCode)
        {
            var code = NormProcessCode(processCode);

            /*
             * Tất cả công đoạn không thuộc PHU/CAN/BOI đều không tạo GROUP.
             * Chúng sẽ nằm trong SINGLE private hoặc SPLIT riêng từng order.
             */
            return !IsGroupableDept2Process(code);
        }


        private static List<string> NormalizeSelectedCodesForGroup(List<string>? selectedCodes)
        {
            return (selectedCodes ?? new List<string>())
                .SelectMany(x => GroupProductionHelper.ParseCodes(x))
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static void EnsureOnlyGroupableDept2Codes(List<string> selectedCodes)
        {
            var invalid = selectedCodes
                .Select(NormProcessCode)
                .Where(x => !IsGroupableDept2Process(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (invalid.Count > 0)
            {
                throw new InvalidOperationException(
                    "Chỉ được ghép nhóm sản xuất ở công đoạn PHU, CAN, BOI. " +
                    $"Không hợp lệ: {string.Join(",", invalid)}");
            }
        }

        private static bool IsGroupableProductionMethod(string? method)
        {
            var value = NormalizeProductionMethodForGroup(method);

            return value == "NVL" || value == "SUB";
        }

        private static string ResolveGroupProductionMethodLabel(IEnumerable<GroupOrderRow> rows)
        {
            var methods = rows
                .Select(x => NormalizeProductionMethodForGroup(x.SingleProd.prod_method))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            if (methods.Count == 0)
                throw new InvalidOperationException("Không xác định được prod_method cho group.");

            if (methods.Any(x => x == "BOTH"))
            {
                throw new InvalidOperationException(
                    "Không thể ghép production có method BOTH. BOTH phải sản xuất đơn riêng.");
            }

            if (methods.Count == 1)
                return methods[0]!;

            /*
             * Có cả NVL và SUB.
             */
            return "MIXED";
        }

        private static string ResolveMethodSummary(IEnumerable<GroupOrderRow> rows)
        {
            return string.Join(", ",
                rows
                    .Select(x => $"{x.Order.order_id}:{ResolveRowProductionMethodOrNull(x)}")
                    .OrderBy(x => x));
        }

        private static bool SameProcessCodes(
    List<string>? a,
    List<string>? b)
        {
            var aa = (a ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            var bb = (b ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            return aa.SequenceEqual(bb, StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameOrderSet(
            List<int>? a,
            List<int>? b)
        {
            var aa = (a ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var bb = (b ?? new List<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return aa.SequenceEqual(bb);
        }

        private string BuildCompositeGroupMaterialKey(
            List<string> processCodes,
            GroupOrderRow row)
        {
            var keys = processCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .Select(code => BuildGroupPlanKey(code, row))
                .ToList();

            return string.Join(" | ", keys);
        }

        private static List<string> BuildPrivateCodesBeforeGroupForOrder(
    GroupOrderRow row,
    List<string> groupedCodes)
        {
            groupedCodes = groupedCodes
                .Select(NormProcessCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var firstGroupedIndex = row.RouteCodes
                .Select((code, index) => new
                {
                    code = NormProcessCode(code),
                    index
                })
                .Where(x => groupedCodes.Contains(x.code, StringComparer.OrdinalIgnoreCase))
                .Select(x => x.index)
                .DefaultIfEmpty(-1)
                .Min();

            if (firstGroupedIndex <= 0)
                return new List<string>();

            return row.RouteCodes
                .Take(firstGroupedIndex)
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static List<GroupProductionScheduleStageDto> BuildPrivateStagesBeforeGroup(
            List<GroupOrderRow> rows,
            List<ProductionPlanSegment> plan,
            DateTime start)
        {
            var groupSegments = plan
                .Where(x => x.IsGroup)
                .ToList();

            if (groupSegments.Count == 0)
                return new List<GroupProductionScheduleStageDto>();

            var groupedCodesByOrder = groupSegments
                .SelectMany(segment => segment.Members.Select(member => new
                {
                    order_id = member.Order.order_id,
                    codes = segment.ProcessCodes
                }))
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(x => x.codes)
                        .Select(NormProcessCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList());

            var result = new List<GroupProductionScheduleStageDto>();

            foreach (var row in rows.OrderBy(x => x.Order.order_id))
            {
                if (!groupedCodesByOrder.TryGetValue(row.Order.order_id, out var groupedCodes))
                    continue;

                var privateCodes = BuildPrivateCodesBeforeGroupForOrder(
                    row,
                    groupedCodes);

                if (privateCodes.Count == 0)
                    continue;

                result.Add(BuildStageDto(
                    deptCode: "PRIVATE_BEFORE_GROUP",
                    deptName: "Công đoạn riêng trước ghép nhóm",
                    stageType: "SINGLE_PRIVATE",
                    processCodes: privateCodes,
                    orderIds: new List<int> { row.Order.order_id },
                    start: start,
                    durationDays: Dept1Days,
                    note: NotePrivateBeforeGroup));
            }

            return result;
        }
    }
}
