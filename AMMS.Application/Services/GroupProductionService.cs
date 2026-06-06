using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Twilio.Annotations;

namespace AMMS.Application.Services
{
    public class GroupProductionService : IGroupProductionService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _noti;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly ITaskScanService _scanSvc;
        private readonly WorkCalendar _cal;
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

        public GroupProductionService(AppDbContext db, NotificationService noti, IHubContext<RealtimeHub> hub, ITaskScanService scanSvc, WorkCalendar cal)
        {
            _db = db;
            _noti = noti;
            _hub = hub;
            _scanSvc = scanSvc;
            _cal = cal;
        }

        public async Task<List<GroupProductionCandidateDto>> GetCandidatesAsync(
    int? productTypeId,
    string? processCodes,
    CancellationToken ct = default)
        {
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

            req.order_ids ??= new List<int>();
            req.process_codes ??= new List<string>();

            var isPriority = req.is_priority ?? false;

            var orderIds = req.order_ids
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (orderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để sản xuất ghép.");

            var selectedCodes = NormalizeSelectedCodesForGroup(req.process_codes);

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn PHU/CAN/BOI để tạo lệnh sản xuất ghép.");

            EnsureOnlyGroupableDept2Codes(selectedCodes);

            await ValidateCreateSelectionBelongsToGroupSuggestionAsync(
                orderIds,
                selectedCodes,
                ct);

            var preview = await PreviewAsync(req, ct);

            if (string.Equals(preview.suggestion_type, "SINGLE_PREVIEW", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "API tạo lệnh ghép không dùng cho sản xuất đơn. Cần chọn ít nhất 2 order đủ điều kiện ghép.");
            }

            preview.batches ??= new List<SuggestionBatchPreviewDto>();

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

                await EnsureSinglePrivateTasksBeforeGroupAsync(
                    rows,
                    plan,
                    allSteps,
                    preview,
                    ct);

                var createdGroupProdIds = new List<int>();
                var createdSplitProdIds = new List<int>();

                var groupBatches = preview.batches
                    .Where(x =>
                        string.Equals(x.batch_type, "GROUP", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.planned_start_date)
                    .ThenBy(x => x.process_codes == null || x.process_codes.Count == 0
                        ? int.MaxValue
                        : x.process_codes.Select(FullRouteIndex).Min())
                    .ToList();

                if (groupBatches.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Preview không có batch GROUP nên không thể tạo production ghép.");
                }

                foreach (var groupBatch in groupBatches)
                {
                    groupBatch.order_ids ??= new List<int>();

                    var memberOrderIdSet = groupBatch.order_ids
                        .Where(x => x > 0)
                        .Distinct()
                        .ToHashSet();

                    if (memberOrderIdSet.Count < 2)
                    {
                        throw new InvalidOperationException(
                            "Batch GROUP cần ít nhất 2 order.");
                    }

                    var members = rows
                        .Where(x => memberOrderIdSet.Contains(x.Order.order_id))
                        .OrderBy(x => x.Order.delivery_date)
                        .ThenBy(x => x.Order.order_id)
                        .ToList();

                    if (members.Count < 2)
                    {
                        throw new InvalidOperationException(
                            "Không tìm thấy đủ member rows để tạo batch GROUP.");
                    }

                    var groupProd = await CreateDepartmentGroupProductionAsync(
                        batch: groupBatch,
                        members: members,
                        productTypeId: productTypeId,
                        managerUserId: managerUserId,
                        note: req.note,
                        isPriority: isPriority,
                        ct: ct);

                    createdGroupProdIds.Add(groupProd.prod_id);
                }

                var splitBatches = preview.batches
                    .Where(x =>
                        string.Equals(x.batch_type, "SPLIT", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.planned_start_date)
                    .ThenBy(x => x.order_ids == null || x.order_ids.Count == 0
                        ? int.MaxValue
                        : x.order_ids.Min())
                    .ToList();

                foreach (var splitBatch in splitBatches)
                {
                    splitBatch.order_ids ??= new List<int>();

                    var orderId = splitBatch.order_ids
                        .Where(x => x > 0)
                        .Distinct()
                        .FirstOrDefault();

                    if (orderId <= 0)
                        continue;

                    var row = rows.FirstOrDefault(x => x.Order.order_id == orderId);

                    if (row == null)
                        continue;

                    var splitProd = await CreateSplitProductionAsync(
                        batch: splitBatch,
                        row: row,
                        productTypeId: productTypeId,
                        managerUserId: managerUserId,
                        note: req.note,
                        isPriority: isPriority,
                        ct: ct);

                    createdSplitProdIds.Add(splitProd.prod_id);
                }

                await MarkGroupedOrdersAndSingleProductionsScheduledAsync(
                    rows,
                    preview,
                    isPriority,
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
    bool isPriority,
    CancellationToken ct)
        {
            rows ??= new List<GroupOrderRow>();

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
                ord.status = "Scheduled";
            }

            var singleProds = await _db.productions
                .Where(x => singleProdIds.Contains(x.prod_id))
                .ToListAsync(ct);

            foreach (var prod in singleProds)
            {
                var row = rows.FirstOrDefault(x => x.SingleProd.prod_id == prod.prod_id);

                prod.status = "Scheduled";
                prod.actual_start_date = null;
                prod.end_date = null;
                prod.created_at = AppTime.NowVnUnspecified();
                prod.is_priority = isPriority;

                if (row == null || !prod.order_id.HasValue)
                {
                    prod.planned_start_date ??= preview.suggested_planned_start_date;
                    prod.planned_end_date ??= preview.suggested_planned_start_date;
                    continue;
                }

                var privateStage = preview.private_stages
                    .FirstOrDefault(x =>
                        x.order_ids != null &&
                        x.order_ids.Contains(prod.order_id.Value));

                if (privateStage != null)
                {
                    prod.planned_start_date = privateStage.planned_start_date;
                    prod.planned_end_date = privateStage.planned_end_date;

                    prod.group_process_codes = string.Join(",",
                        (privateStage.process_codes ?? new List<string>())
                            .Select(NormProcessCode)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(FullRouteIndex));
                }
                else
                {
                    prod.planned_start_date ??= preview.suggested_planned_start_date;
                    prod.planned_end_date ??= preview.suggested_planned_start_date;

                    var privateCodes = ResolvePrivateCodesBeforeFirstGroupForOrderFromPreview(
                        row,
                        preview);

                    if (privateCodes.Count > 0)
                    {
                        prod.group_process_codes = string.Join(",", privateCodes);
                    }
                }
            }
        }

        private static List<string> ResolvePrivateCodesBeforeFirstGroupForOrderFromPreview(
    GroupOrderRow row,
    GroupProductionConfirmPreviewResponse preview)
        {
            var groupCodes = preview.group_stages
                .Where(x => x.order_ids != null && x.order_ids.Contains(row.Order.order_id))
                .SelectMany(x => x.process_codes ?? new List<string>())
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groupCodes.Count == 0)
                return new List<string>();

            return BuildPrivateCodesBeforeGroupForOrder(
                row,
                groupCodes);
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
                        .OrderBy(FullRouteIndex)
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
                    x.order_ids != null &&
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

                var existingTasks = await _db.tasks
                    .Include(x => x.process)
                    .Where(x => x.prod_id == row.SingleProd.prod_id)
                    .ToListAsync(ct);

                var hasProgress = existingTasks.Any(x =>
                    string.Equals(x.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    x.start_time != null ||
                    x.end_time != null);

                if (hasProgress)
                {
                    throw new InvalidOperationException(
                        $"Production single {row.SingleProd.prod_id} của order {row.Order.order_id} đã có task chạy, không thể tạo group.");
                }

                if (existingTasks.Count > 0)
                {
                    _db.tasks.RemoveRange(existingTasks);
                    await _db.SaveChangesAsync(ct);
                }

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
                        .Select(NormProcessCode)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                var steps = allSteps
                    .Where(x => privateCodesBeforeGroup.Contains(
                        NormProcessCode(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => FullRouteIndex(x.process_code))
                    .ToList();

                if (steps.Count != privateCodesBeforeGroup.Count)
                {
                    var found = steps
                        .Select(x => NormProcessCode(x.process_code))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var missing = privateCodesBeforeGroup
                        .Where(x => !found.Contains(x))
                        .ToList();

                    throw new InvalidOperationException(
                        $"Thiếu product_type_process cho công đoạn riêng trước group: {string.Join(",", missing)}.");
                }

                var totalMinutes = Math.Max(
                    1,
                    (int)(stage.planned_end_date - stage.planned_start_date).TotalMinutes);

                var minutesPerTask = Math.Max(
                    1,
                    totalMinutes / Math.Max(1, steps.Count));

                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    var code = NormProcessCode(step.process_code);

                    var isFinishedBySub =
                        method == "SUB" &&
                        subCoveredCodes.Contains(code);

                    var taskStart = stage.planned_start_date.AddMinutes(minutesPerTask * i);

                    var taskEnd = i == steps.Count - 1
                        ? stage.planned_end_date
                        : stage.planned_start_date.AddMinutes(minutesPerTask * (i + 1));

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

                        planned_start_time = taskStart,
                        planned_end_time = taskEnd,

                        reason = isFinishedBySub
                            ? "Bán thành phẩm đã đáp ứng công đoạn riêng trước ghép nhóm."
                            : "Công đoạn riêng trước ghép nhóm.",

                        is_taken_sub_product = isFinishedBySub
                    };

                    await _db.tasks.AddAsync(task, ct);
                }

                row.SingleProd.status = "Scheduled";
                row.SingleProd.actual_start_date = null;
                row.SingleProd.end_date = null;
                row.SingleProd.planned_start_date = stage.planned_start_date;
                row.SingleProd.planned_end_date = stage.planned_end_date;
                row.SingleProd.group_process_codes = string.Join(",", privateCodesBeforeGroup);
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
        private async Task<production> CreateDepartmentGroupProductionAsync(
    SuggestionBatchPreviewDto batch,
    List<GroupOrderRow> members,
    int productTypeId,
    int? managerUserId,
    string? note,
    bool isPriority,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var processCodes = batch.process_codes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (processCodes.Count == 0)
                throw new InvalidOperationException("Group batch không có process_codes.");

            var groupTotalQty = members.Sum(x => x.Item?.quantity ?? 0);

            if (groupTotalQty <= 0)
                groupTotalQty = members.Count;

            var groupProd = new production
            {
                code = $"GRP-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}",
                order_id = null,
                product_type_id = productTypeId,
                manager_id = managerUserId,
                status = "Scheduled",

                prod_kind = "GROUP",
                prod_method = ResolveGroupProductionMethodLabel(members),
                is_full_process = false,

                production_approval_flow = ResolveGroupProductionApprovalFlow(),

                group_process_codes = string.Join(",", processCodes),
                group_total_qty = groupTotalQty,

                planned_start_date = batch.planned_start_date,
                planned_end_date = batch.planned_end_date,
                actual_start_date = null,
                end_date = null,
                created_at = now,

                gm_note = note,
                is_priority = isPriority
            };

            await _db.productions.AddAsync(groupProd, ct);
            await _db.SaveChangesAsync(ct);

            await CreateTasksFromPreviewBatchAsync(
                prodId: groupProd.prod_id,
                productTypeId: productTypeId,
                batch: batch,
                reason: "Công đoạn ghép nhóm.",
                ct: ct);

            await _db.SaveChangesAsync(ct);

            var groupTasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == groupProd.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            foreach (var row in members)
            {
                await _db.prod_orders.AddAsync(new prod_order
                {
                    prod_id = groupProd.prod_id,
                    order_id = row.Order.order_id,
                    single_prod_id = row.SingleProd.prod_id,
                    qty = row.Item?.quantity ?? 0,
                    product_type_id = productTypeId,
                    product_process = string.Join(",", processCodes),
                    status = "Active",
                    created_at = now
                }, ct);
            }

            await _db.SaveChangesAsync(ct);

            foreach (var groupTask in groupTasks)
            {
                var taskProcessCode = NormProcessCode(groupTask.process?.process_code);

                foreach (var row in members)
                {
                    await _db.task_links.AddAsync(new task_link
                    {
                        group_prod_id = groupProd.prod_id,
                        group_task_id = groupTask.task_id,

                        single_prod_id = row.SingleProd.prod_id,
                        single_task_id = null,
                        original_single_task_id = null,

                        order_id = row.Order.order_id,
                        process_code = taskProcessCode,
                        qty_plan = row.Item?.quantity ?? 0,

                        status = "Active",
                        created_at = now,
                        done_at = null
                    }, ct);
                }
            }

            await _db.SaveChangesAsync(ct);

            return groupProd;
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
    SuggestionBatchPreviewDto batch,
    GroupOrderRow row,
    int productTypeId,
    int? managerUserId,
    string? note,
    bool isPriority,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var processCodes = batch.process_codes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (processCodes.Count == 0)
                throw new InvalidOperationException("Split batch không có process_codes.");

            EnsureSingleMemberApprovalFlow(row.SingleProd);

            var splitProd = new production
            {
                code = $"SPL-{row.Order.order_id:D5}-{now:HHmmss}",
                order_id = row.Order.order_id,
                product_type_id = productTypeId,
                manager_id = managerUserId,
                status = "Scheduled",

                prod_kind = "SPLIT",
                prod_method = row.SingleProd.prod_method,
                is_full_process = false,

                sub_product_id = row.SingleProd.sub_product_id,
                sub_product_used_qty = row.SingleProd.sub_product_used_qty,
                nvl_qty = row.SingleProd.nvl_qty,

                production_approval_flow = ResolveSplitProductionApprovalFlow(row.SingleProd),

                group_process_codes = string.Join(",", processCodes),
                group_total_qty = row.Item?.quantity ?? 0,

                planned_start_date = batch.planned_start_date,
                planned_end_date = batch.planned_end_date,
                actual_start_date = null,
                end_date = null,
                created_at = now,

                gm_note = note,
                is_priority = isPriority
            };

            await _db.productions.AddAsync(splitProd, ct);
            await _db.SaveChangesAsync(ct);

            await CreateTasksFromPreviewBatchAsync(
                prodId: splitProd.prod_id,
                productTypeId: productTypeId,
                batch: batch,
                reason: "Công đoạn tách riêng sau ghép nhóm.",
                ct: ct);

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

                var singleRows = rowsOfOneProductType
                    .Where(x => !groupedOrderIds.Contains(x.Order.order_id))
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                foreach (var row in singleRows)
                {
                    var method = ResolveRowProductionMethodOrNull(row);

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
                var preview = await PreviewCoreAsync(
                    new CreateGroupProductionRequest
                    {
                        order_ids = suggestion.suggest_order,
                        process_codes = suggestion.suggest_process,
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

                suggestion.batches = preview.batches ?? new List<SuggestionBatchPreviewDto>();

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

            var rowsHavingAllCodes = rows
                .Where(row => selectedCodes.All(code =>
                    row.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (rowsHavingAllCodes.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var result = new List<SuggestedGroupProductionDto>();

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

            var previousEstimatedOutputQty = prod.group_total_qty > 0
    ? (decimal)prod.group_total_qty
    : orderRows.Sum(x => x.qty);

            var previousActualOutputQty = 0m;

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
                    previousEstimatedOutputQty,
                    previousOutputName,
                    taskLogs);

                var qrBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                    task.task_id,
                    ct);

                ApplyQrPrepareEstimateToGroupStageIo(
                    io.inputs,
                    io.outputs,
                    qrBundle,
                    task.process?.process_code,
                    task.process?.process_name,
                    previousEstimatedOutputQty,
                    previousOutputName);

                ApplyTaskLogJsonToGroupStageIo(
                    io.inputs,
                    io.outputs,
                    taskLogs);

                await ApplyConfirmedSubActualToGroupFirstStageInputAsync(
                    groupProd: prod,
                    groupTask: task,
                    inputs: io.inputs,
                    outputs: io.outputs,
                    qrBundle: qrBundle,
                    ct: ct);

                var actualOutput = ResolveGroupActualOutputQty(taskLogs);

                var estimatedOutputQty = ResolveEstimatedGroupOutputQty(
                    io.outputs,
                    io.estimatedOutputQty,
                    previousEstimatedOutputQty);

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

                var firstOutput = io.outputs.FirstOrDefault();

                previousEstimatedOutputQty = ResolveGroupStageEstimatedOutputForNext(
                    stage,
                    firstOutput,
                    estimatedOutputQty);

                previousActualOutputQty = ResolveGroupStageActualOutputForNext(
                    stage,
                    firstOutput);

                previousOutputName =
                    firstOutput?.name
                    ?? $"BTP sau {task.process?.process_code}";
            }

            NormalizeGroupDetailStagesBySequentialFlow(stages);


            NormalizeGroupDetailEstimatedQtyByGroupAnchor(stages);

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

        private static void NormalizeGroupDetailEstimatedQtyByGroupAnchor(
    List<GroupProductionStageDto>? stages)
        {
            if (stages == null || stages.Count == 0)
                return;

            var orderedStages = stages
                .Where(x => x != null)
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .ToList();

            if (orderedStages.Count == 0)
                return;

            var anchorEstimateQty = ResolveGroupStableEstimateAnchorQty(orderedStages);

            if (anchorEstimateQty <= 0)
                return;

            decimal previousActualOutputQty = 0m;

            for (var i = 0; i < orderedStages.Count; i++)
            {
                var stage = orderedStages[i];

                stage.estimated_output_qty = RoundQty(anchorEstimateQty);

                ApplyGroupStableEstimateToPrevInput(
                    stage,
                    anchorEstimateQty,
                    i == 0 ? null : previousActualOutputQty);

                ApplyGroupStableEstimateToMainOutput(
                    stage,
                    anchorEstimateQty);

                previousActualOutputQty = ResolveGroupStageActualOutputForNextStable(
                    stage);
            }
        }

        private static decimal ResolveGroupStableEstimateAnchorQty(
            List<GroupProductionStageDto> orderedStages)
        {
            if (orderedStages == null || orderedStages.Count == 0)
                return 0m;

            var firstStage = orderedStages.FirstOrDefault();

            var fromFirstStage = ResolveGroupStageEstimatedQtyForAnchor(firstStage);

            if (fromFirstStage > 0)
                return RoundQty(fromFirstStage);

            var maxEstimate = orderedStages
                .Select(ResolveGroupStageEstimatedQtyForAnchor)
                .Where(x => x > 0)
                .DefaultIfEmpty(0m)
                .Max();

            return maxEstimate > 0
                ? RoundQty(maxEstimate)
                : 0m;
        }

        private static decimal ResolveGroupStageEstimatedQtyForAnchor(
            GroupProductionStageDto? stage)
        {
            if (stage == null)
                return 0m;

            if (stage.estimated_output_qty > 0)
                return stage.estimated_output_qty;

            var fromOutput = stage.outputs?
                .Where(x => x != null && x.estimated_qty > 0)
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            if (fromOutput > 0)
                return fromOutput;

            var fromPrevInput = stage.input_materials?
                .Where(x => IsPreviousStageInputMaterial(x.code, x.name))
                .Where(x => x.estimated_qty > 0)
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            return fromPrevInput;
        }

        private static void ApplyGroupStableEstimateToPrevInput(
            GroupProductionStageDto stage,
            decimal anchorEstimateQty,
            decimal? previousActualOutputQty)
        {
            if (stage == null)
                return;

            stage.input_materials ??= new List<GroupStageMaterialDto>();

            var prevInput = stage.input_materials
                .FirstOrDefault(x => IsPreviousStageInputMaterial(x.code, x.name));

            if (prevInput == null)
                return;

            prevInput.code = "PREV";

            prevInput.estimated_qty = RoundQty(anchorEstimateQty);

            if (previousActualOutputQty.HasValue && previousActualOutputQty.Value > 0)
            {
                prevInput.actual_qty = RoundQty(previousActualOutputQty.Value);
            }

            if (string.IsNullOrWhiteSpace(prevInput.unit))
                prevInput.unit = "sp";
        }

        private static void ApplyGroupStableEstimateToMainOutput(
            GroupProductionStageDto stage,
            decimal anchorEstimateQty)
        {
            if (stage == null)
                return;

            if (stage.outputs == null || stage.outputs.Count == 0)
                return;

            var currentCode = NormGroupDetailCode(stage.process_code);

            var mainOutput = stage.outputs
                .FirstOrDefault(x =>
                    SameGroupDetailCode(x.code, currentCode));

            if (mainOutput == null)
            {
                mainOutput = stage.outputs.FirstOrDefault();
            }

            if (mainOutput == null)
                return;

            mainOutput.estimated_qty = RoundQty(anchorEstimateQty);
        }

        private static decimal ResolveGroupStageActualOutputForNextStable(
            GroupProductionStageDto stage)
        {
            if (stage == null)
                return 0m;

            if (stage.actual_output_qty > 0)
                return RoundQty(stage.actual_output_qty);

            var fromOutput = stage.outputs?
                .Where(x => x != null && x.actual_qty > 0)
                .Select(x => x.actual_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            return fromOutput > 0
                ? RoundQty(fromOutput)
                : 0m;
        }

        private static decimal ResolveGroupStageEstimatedOutputForNext(
    GroupProductionStageDto stage,
    GroupStageMaterialDto? firstOutput,
    decimal fallbackEstimatedOutputQty)
        {
            if (firstOutput != null && firstOutput.estimated_qty > 0)
                return RoundQty(firstOutput.estimated_qty);

            if (stage != null && stage.estimated_output_qty > 0)
                return RoundQty(stage.estimated_output_qty);

            if (fallbackEstimatedOutputQty > 0)
                return RoundQty(fallbackEstimatedOutputQty);

            return 0m;
        }

        private static decimal ResolveGroupStageActualOutputForNext(
            GroupProductionStageDto stage,
            GroupStageMaterialDto? firstOutput)
        {
            if (stage != null && stage.actual_output_qty > 0)
                return RoundQty(stage.actual_output_qty);

            if (firstOutput != null && firstOutput.actual_qty > 0)
                return RoundQty(firstOutput.actual_qty);

            return 0m;
        }

        private sealed class GroupDetailOrderEstimateContext
        {
            public int order_id { get; set; }

            public int order_qty { get; set; }

            public int n_up { get; set; } = 1;

            public string? coating_type { get; set; }

            public List<string> route_codes { get; set; } = new();
        }

        private async Task ApplyConfirmedSubActualToGroupFirstStageInputAsync(
            production groupProd,
            task groupTask,
            List<GroupStageMaterialDto> inputs,
            List<GroupStageMaterialDto> outputs,
            TaskQrMaterialBundleDto? qrBundle,
            CancellationToken ct)
        {
            if (groupProd == null || groupTask == null)
                return;

            if (inputs == null)
                return;

            var isGroupSub =
                string.Equals(groupProd.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(groupProd.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);

            if (!isGroupSub)
                return;

            var currentCode = NormGroupDetailCode(groupTask.process?.process_code);

            if (string.IsNullOrWhiteSpace(currentCode))
                return;

            var hasActualFromQrReport = inputs.Any(x =>
                IsGroupBtpInputForConfirmedSubActual(x) &&
                x.actual_qty > 0);

            if (hasActualFromQrReport)
                return;

            var links = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.group_task_id == groupTask.task_id &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .OrderBy(x => x.id)
                .ToListAsync(ct);

            if (links.Count == 0)
                return;

            decimal totalActualInputQty = 0m;
            string? previousCodeForDisplay = null;
            var confirmedLinkCount = 0;

            foreach (var link in links)
            {
                if (link.single_prod_id <= 0 || link.order_id <= 0)
                    return;

                var singleProd = await _db.productions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.prod_id == link.single_prod_id &&
                        x.order_id == link.order_id,
                        ct);

                if (singleProd == null)
                    return;

                if (!string.Equals(singleProd.prod_method, "SUB", StringComparison.OrdinalIgnoreCase))
                    return;

                var ctx = await LoadGroupDetailOrderEstimateContextAsync(
                    link.order_id,
                    ct);

                if (ctx == null || ctx.route_codes.Count == 0)
                    return;

                var currentIndex = ctx.route_codes.FindIndex(x =>
                    string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));

                if (currentIndex <= 0)
                    return;

                var previousCode = ctx.route_codes[currentIndex - 1];
                previousCodeForDisplay ??= previousCode;

                var subCodes = await ResolveSingleSubProductCodesForGroupDetailAsync(
                    singleProd,
                    ctx.route_codes,
                    ct);

                if (subCodes.Count == 0)
                    return;

                var subIndexes = subCodes
                    .Select(code => ctx.route_codes.FindIndex(routeCode =>
                        string.Equals(routeCode, code, StringComparison.OrdinalIgnoreCase)))
                    .Where(index => index >= 0)
                    .ToList();

                if (subIndexes.Count == 0)
                    return;

                var subLastIndex = subIndexes.Max();

                if (currentIndex != subLastIndex + 1)
                    return;

                var confirmed = await IsSingleSubPathConfirmedForGroupDetailAsync(
                    singleProdId: singleProd.prod_id,
                    requiredSubCodes: subCodes,
                    ct: ct);

                if (!confirmed)
                    return;

                var productQty = link.qty_plan > 0
                    ? link.qty_plan
                    : singleProd.sub_product_used_qty > 0
                        ? singleProd.sub_product_used_qty
                        : ctx.order_qty;

                if (productQty <= 0)
                    productQty = 1;

                var nUp = ctx.n_up > 0 ? ctx.n_up : 1;

                var sheetsBase = Math.Max(
                    1,
                    (int)Math.Ceiling(productQty / (decimal)nUp));

                var stageQty = SubProductionQuantityHelper.ResolveStageQty(
                    currentProcessCode: currentCode,
                    routeProcessCodes: ctx.route_codes.Cast<string?>().ToList(),
                    productQty: productQty,
                    nUp: nUp,
                    explicitSheetsBase: sheetsBase,
                    coatingType: ctx.coating_type);

                var actualForThisOrder =
                    stageQty.input_qty > 0
                        ? stageQty.input_qty
                        : productQty;

                if (actualForThisOrder <= 0)
                    return;

                totalActualInputQty += actualForThisOrder;
                confirmedLinkCount++;
            }

            if (confirmedLinkCount != links.Count)
                return;

            if (totalActualInputQty <= 0)
                return;

            var estimatedFromQr = ResolveGroupReferenceEstimatedQtyFromQrBundle(qrBundle);

            var estimatedQty = estimatedFromQr > 0
                ? estimatedFromQr
                : totalActualInputQty;

            estimatedQty = RoundQty(estimatedQty);
            totalActualInputQty = RoundQty(totalActualInputQty);

            ApplyConfirmedActualQtyToGroupBtpInput(
                inputs: inputs,
                previousCode: previousCodeForDisplay,
                estimatedQty: estimatedQty,
                actualQty: totalActualInputQty);
        }

        private static decimal ResolveGroupReferenceEstimatedQtyFromQrBundle(
            TaskQrMaterialBundleDto? qrBundle)
        {
            if (qrBundle?.reference_inputs == null || qrBundle.reference_inputs.Count == 0)
                return 0m;

            var qty = qrBundle.reference_inputs
                .Where(x => x != null)
                .Sum(x =>
                    x.estimated_qty > 0
                        ? x.estimated_qty
                        : x.actual_qty_prev_stage);

            return qty > 0
                ? RoundQty(qty)
                : 0m;
        }

        private static void ApplyConfirmedActualQtyToGroupBtpInput(
    List<GroupStageMaterialDto> inputs,
    string? previousCode,
    decimal estimatedQty,
    decimal actualQty)
        {
            if (inputs == null)
                return;

            previousCode = string.IsNullOrWhiteSpace(previousCode)
                ? "PREV"
                : NormGroupDetailCode(previousCode);

            var mainInput = inputs.FirstOrDefault(x => IsGroupPrevInputCode(x.code))
                ?? inputs.FirstOrDefault(x => SameGroupDetailCode(x.code, previousCode))
                ?? inputs.FirstOrDefault(IsGroupBtpInputForConfirmedSubActual);

            if (mainInput == null)
            {
                inputs.Insert(0, new GroupStageMaterialDto
                {
                    code = "PREV",

                    name = $"Bán thành phẩm từ công đoạn {previousCode}",
                    unit = previousCode is "BE" or "DUT" or "DAN" ? "sp" : "tờ",

                    estimated_qty = Math.Round(estimatedQty, 4),
                    actual_qty = Math.Round(actualQty, 4)
                });

                return;
            }

            mainInput.code = "PREV";

            mainInput.estimated_qty = Math.Round(estimatedQty, 4);
            mainInput.actual_qty = Math.Round(actualQty, 4);

            if (string.IsNullOrWhiteSpace(mainInput.name))
                mainInput.name = $"Bán thành phẩm từ công đoạn {previousCode}";

            if (string.IsNullOrWhiteSpace(mainInput.unit))
                mainInput.unit = previousCode is "BE" or "DUT" or "DAN" ? "sp" : "tờ";

            var duplicateBtpInputs = inputs
                .Where(x => !ReferenceEquals(x, mainInput))
                .Where(x =>
                    IsGroupPrevInputCode(x.code) ||
                    SameGroupDetailCode(x.code, previousCode) ||
                    IsGroupBtpInputForConfirmedSubActual(x))
                .ToList();

            foreach (var duplicate in duplicateBtpInputs)
            {
                var code = NormGroupDetailCode(duplicate.code);

                if (code is "KEO_PHU_NUOC" or "KEO_PHU_DAU" or "KEO_PHU_UV" or
                    "MANG_12MIC" or "SONG_B_NAU" or "SONG_E_NAU" or "SONG_E_MOC" or
                    "KEO_BOI" or "INK" or "PLATE" or "PAPER")
                {
                    continue;
                }

                inputs.Remove(duplicate);
            }
        }

        private static bool IsGroupBtpInputForConfirmedSubActual(
            GroupStageMaterialDto input)
        {
            if (input == null)
                return false;

            var code = NormGroupDetailCode(input.code);
            var name = NormGroupDetailCode(input.name);

            if (code is "PREV" or "INPUT" or "BTP" or "REFERENCE" or "REFERENCE_INPUT")
                return true;

            if (code is "RALO" or "CAT" or "IN" or "PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN")
                return true;

            return name.Contains("BAN_THANH_PHAM") ||
                   name.Contains("BTP") ||
                   name.Contains("CONG_DOAN") ||
                   name.Contains("GIAY_DA_CAT");
        }

        private async Task<GroupDetailOrderEstimateContext?> LoadGroupDetailOrderEstimateContextAsync(
            int orderId,
            CancellationToken ct)
        {
            if (orderId <= 0)
                return null;

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.product_type_id,
                    x.quantity,
                    x.production_process
                })
                .FirstOrDefaultAsync(ct);

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            cost_estimate? est = null;

            if (req?.accepted_estimate_id.HasValue == true &&
                req.accepted_estimate_id.Value > 0)
            {
                est = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == req.accepted_estimate_id.Value &&
                        x.order_request_id == req.order_request_id,
                        ct);
            }

            if (est == null && req != null)
            {
                est = await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == req.order_request_id)
                    .OrderByDescending(x => x.is_active)
                    .ThenByDescending(x => x.estimate_id)
                    .FirstOrDefaultAsync(ct);
            }

            var route = ParseGroupDetailRoute(item?.production_process);

            if (route.Count == 0)
                route = ParseGroupDetailRoute(est?.production_processes);

            if (route.Count == 0 && item?.product_type_id != null && item.product_type_id.Value > 0)
            {
                var fromProductType = await _db.product_type_processes
                    .AsNoTracking()
                    .Where(x =>
                        x.product_type_id == item.product_type_id.Value &&
                        (x.is_active ?? true))
                    .OrderBy(x => x.seq_num)
                    .Select(x => x.process_code)
                    .ToListAsync(ct);

                route = fromProductType
                    .Select(NormGroupDetailCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }

            var orderQty = item?.quantity ?? req?.quantity ?? 0;

            if (orderQty <= 0)
                orderQty = 1;

            var nUp = est?.n_up ?? 1;

            if (nUp <= 0)
                nUp = 1;

            return new GroupDetailOrderEstimateContext
            {
                order_id = orderId,
                order_qty = orderQty,
                n_up = nUp,
                coating_type = est?.coating_type,
                route_codes = route
            };
        }

        private async Task<List<string>> ResolveSingleSubProductCodesForGroupDetailAsync(
            production singleProd,
            IReadOnlyList<string> routeCodes,
            CancellationToken ct)
        {
            if (singleProd == null)
                return new List<string>();

            var result = new List<string>();

            if (singleProd.sub_product_id.HasValue && singleProd.sub_product_id.Value > 0)
            {
                var csv = await _db.sub_products
                    .AsNoTracking()
                    .Where(x => x.id == singleProd.sub_product_id.Value)
                    .Select(x => x.product_process)
                    .FirstOrDefaultAsync(ct);

                result = ParseGroupDetailRoute(csv);
            }

            if (result.Count == 0)
            {
                var finishedBySub = await (
                    from t in _db.tasks.AsNoTracking()

                    join pp0 in _db.product_type_processes.AsNoTracking()
                        on t.process_id equals pp0.process_id into ppj
                    from pp in ppj.DefaultIfEmpty()

                    where t.prod_id == singleProd.prod_id &&
                          (
                              t.is_taken_sub_product == true ||
                              (
                                  t.reason != null &&
                                  t.reason.ToLower().Contains("bán thành phẩm")
                              )
                          ) &&
                          (
                              t.status == "Finished" ||
                              t.end_time != null
                          )

                    orderby t.seq_num, t.task_id

                    select pp != null ? pp.process_code : t.name
                )
                .ToListAsync(ct);

                result = finishedBySub
                    .Select(NormGroupDetailCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var routeSet = (routeCodes ?? Array.Empty<string>())
                .Select(NormGroupDetailCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (routeSet.Count > 0)
            {
                result = result
                    .Where(x => routeSet.Contains(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList();
            }

            return result;
        }

        private async Task<bool> IsSingleSubPathConfirmedForGroupDetailAsync(
            int singleProdId,
            IReadOnlyList<string> requiredSubCodes,
            CancellationToken ct)
        {
            if (singleProdId <= 0)
                return false;

            var required = (requiredSubCodes ?? Array.Empty<string>())
                .Select(NormGroupDetailCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(CanConfirmSubStageForGroupDetail)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (required.Count == 0)
                return false;

            var taskRows = await (
                from t in _db.tasks.AsNoTracking()

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where t.prod_id == singleProdId

                select new
                {
                    process_code = pp != null ? pp.process_code : t.name,
                    t.status,
                    t.end_time,
                    t.is_taken_sub_product,
                    t.reason
                }
            )
            .ToListAsync(ct);

            var confirmedSet = taskRows
                .Where(x =>
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    x.end_time != null)
                .Where(x =>
                    x.is_taken_sub_product == true ||
                    (
                        x.reason != null &&
                        x.reason.ToLower().Contains("bán thành phẩm")
                    ))
                .Select(x => NormGroupDetailCode(x.process_code))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return required.All(x => confirmedSet.Contains(x));
        }

        private static bool CanConfirmSubStageForGroupDetail(string? processCode)
        {
            var code = NormGroupDetailCode(processCode);

            return code is
                "RALO" or
                "CAT" or
                "IN" or
                "PHU" or
                "CAN" or
                "BOI" or
                "BE" or
                "DUT";
        }

        private static List<string> ParseGroupDetailRoute(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormGroupDetailCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static void NormalizeGroupDetailStagesBySequentialFlow(
    List<GroupProductionStageDto>? stages)
        {
            if (stages == null || stages.Count <= 1)
                return;

            var orderedStages = stages
                .Where(x => x != null)
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .ToList();

            if (orderedStages.Count <= 1)
                return;

            for (var i = 0; i < orderedStages.Count; i++)
            {
                var currentStage = orderedStages[i];

                if (i == 0)
                {
                    SyncCurrentStageOutputEstimate(
                        currentStage,
                        currentStage.estimated_output_qty);

                    continue;
                }

                var previousStage = orderedStages[i - 1];

                var previousEstimatedOutput = ResolveEstimatedOutputForGroupSequentialFlow(
                    previousStage);

                var previousActualOutput = ResolveActualOutputForGroupSequentialFlow(
                    previousStage);

                if (currentStage.estimated_output_qty <= 0 && previousEstimatedOutput > 0)
                {
                    currentStage.estimated_output_qty = RoundQty(previousEstimatedOutput);
                }

                SyncPreviousStageInputMaterialKeepEstimate(
                    currentStage,
                    previousEstimatedOutput,
                    previousActualOutput);

                SyncCurrentStageOutputEstimate(
                    currentStage,
                    currentStage.estimated_output_qty);
            }
        }

        private static decimal ResolveEstimatedOutputForGroupSequentialFlow(
    GroupProductionStageDto stage)
        {
            if (stage == null)
                return 0m;

            var outputEstimated = stage.outputs?
                .Where(x => x != null && x.estimated_qty > 0)
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            if (outputEstimated > 0)
                return RoundQty(outputEstimated);

            if (stage.estimated_output_qty > 0)
                return RoundQty(stage.estimated_output_qty);

            return 0m;
        }

        private static decimal ResolveActualOutputForGroupSequentialFlow(
            GroupProductionStageDto stage)
        {
            if (stage == null)
                return 0m;

            if (stage.actual_output_qty > 0)
                return RoundQty(stage.actual_output_qty);

            var outputActual = stage.outputs?
                .Where(x => x != null && x.actual_qty > 0)
                .Select(x => x.actual_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            if (outputActual > 0)
                return RoundQty(outputActual);

            return 0m;
        }

        private static void SyncPreviousStageInputMaterialKeepEstimate(
            GroupProductionStageDto stage,
            decimal previousEstimatedOutput,
            decimal previousActualOutput)
        {
            if (stage.input_materials == null || stage.input_materials.Count == 0)
                return;

            foreach (var input in stage.input_materials)
            {
                if (!IsPreviousStageInputMaterial(input.code, input.name))
                    continue;
                input.code = "PREV";

                if (input.estimated_qty <= 0 && previousEstimatedOutput > 0)
                {
                    input.estimated_qty = RoundQty(previousEstimatedOutput);
                }

                /*
                 * actual_qty mới được lấy theo actual thật của công đoạn trước.
                 */
                if (previousActualOutput > 0)
                {
                    input.actual_qty = RoundQty(previousActualOutput);
                }
                else
                {

                    input.actual_qty = 0m;
                }
            }
        }
        private static decimal ResolveOldEstimatedQtyBeforeNormalize(
            GroupProductionStageDto stage)
        {
            if (stage == null)
                return 0m;

            if (stage.estimated_output_qty > 0)
                return stage.estimated_output_qty;

            var prevInput = stage.input_materials?
                .Where(x => IsPreviousStageInputMaterial(x.code, x.name))
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            if (prevInput > 0)
                return prevInput;

            var outputEstimated = stage.outputs?
                .Where(x => x != null)
                .Select(x => x.estimated_qty)
                .DefaultIfEmpty(0m)
                .Max() ?? 0m;

            return outputEstimated;
        }
        private static void SyncCurrentStageOutputEstimate(
            GroupProductionStageDto stage,
            decimal estimatedQty)
        {
            if (stage.outputs == null || stage.outputs.Count == 0)
                return;

            var currentCode = NormGroupDetailCode(stage.process_code);

            foreach (var output in stage.outputs)
            {
                var outputCode = NormGroupDetailCode(output.code);

                if (!string.Equals(outputCode, currentCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                output.estimated_qty = RoundQty(estimatedQty);

            }
        }

        private static bool IsPreviousStageInputMaterial(string? code, string? name)
        {
            var c = NormGroupDetailCode(code);
            var n = NormGroupDetailCode(name);

            return c == "PREV"
                || c == "BTP"
                || c == "REFERENCE_INPUT"
                || c == "INPUT"
                || n.Contains("BAN_THANH_PHAM")
                || n.Contains("BTP");
        }

        private static string NormGroupDetailCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static decimal RoundQty(decimal value)
        {
            return Math.Round(value, 4, MidpointRounding.AwayFromZero);
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

            var privateStages = await BuildPrivateStagesForExistingGroupDetailAsync(
                orderRows,
                ct);

            var groupStages = await BuildGroupStagesForExistingGroupDetailAsync(
                groupProd,
                orderRows,
                ct);

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

            if (!IsGroupSubProductionForDetail(prod))
                return baseQty;

            var qtyWithWaste = ResolveGroupSubQtyWithWasteFromStages(
                stages);

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

            ApplyQrReferenceInputEstimateToGroupInputs(
                inputs,
                qrBundle.reference_inputs,
                previousOutputQty,
                previousOutputName);

            ApplyQrConsumableEstimateToGroupInputs(
                inputs,
                qrBundle.consumable_materials);

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
            if (inputs == null)
                return;

            var refs = referenceInputs?
                .Where(x => x != null)
                .Where(x => x.estimated_qty > 0 || x.actual_qty_prev_stage > 0)
                .ToList() ?? new List<TaskReferenceInputDto>();

            decimal estimateQty = 0m;

            if (refs.Count > 0)
            {
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
                    code = "PREV",

                    name =
                        !string.IsNullOrWhiteSpace(firstRef?.input_name)
                            ? firstRef!.input_name
                            : !string.IsNullOrWhiteSpace(previousOutputName)
                                ? previousOutputName
                                : "Bán thành phẩm đầu vào",

                    unit =
                        !string.IsNullOrWhiteSpace(firstRef?.unit)
                            ? firstRef!.unit
                            : "tờ",

                    estimated_qty = estimateQty,
                    actual_qty = 0m
                };

                inputs.Insert(0, input);
                return;
            }

            input.code = "PREV";

            input.estimated_qty = estimateQty;

            if (!string.IsNullOrWhiteSpace(firstRef?.input_name))
                input.name = firstRef!.input_name;
            else if (string.IsNullOrWhiteSpace(input.name) && !string.IsNullOrWhiteSpace(previousOutputName))
                input.name = previousOutputName;

            if (!string.IsNullOrWhiteSpace(firstRef?.unit))
                input.unit = firstRef!.unit;
            else if (string.IsNullOrWhiteSpace(input.unit))
                input.unit = "tờ";
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

                if (!string.IsNullOrWhiteSpace(mat.material_code))
                    input.code = mat.material_code;

                if (!string.IsNullOrWhiteSpace(mat.material_name))
                    input.name = mat.material_name;

                if (!string.IsNullOrWhiteSpace(mat.unit))
                    input.unit = mat.unit;

                input.estimated_qty = estimatedQty;

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

            if (orderIds.Count == 1)
            {
                return await BuildSingleOrderPreviewResponseAsync(
                    orderIds[0],
                    ct);
            }

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
            var suggestedStart = await ResolveSuggestedStartBySystemAsync(ct);

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

        private async Task<DateTime> ResolveSuggestedStartBySystemAsync(CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var hours = await _db.estimate_config
                .AsNoTracking()
                .Where(x =>
                    x.config_group == "planning" &&
                    x.config_key == "min_start_wait_hours")
                .OrderByDescending(x => x.updated_at)
                .Select(x => (decimal?)x.value_num)
                .FirstOrDefaultAsync(ct);

            var resolvedHours = hours.HasValue && hours.Value > 0m
                ? hours.Value
                : 24m;

            var start = now.AddMinutes((double)(resolvedHours * 60m));

            if (start.Date <= now.Date)
                start = now.Date.AddDays(1);

            return start.Date;
        }

        private async Task<GroupProductionConfirmPreviewResponse> BuildSingleOrderPreviewResponseAsync(
    int orderId,
    CancellationToken ct)
        {
            if (orderId <= 0)
                throw new InvalidOperationException("order_id không hợp lệ.");

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

            var suggestedStart = await ResolveSuggestedStartBySystemAsync(ct);

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

            var preview = new GroupProductionConfirmPreviewResponse
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

            return preview;
        }

        public async Task<GroupProductionConfirmPreviewResponse> PreviewAsync(
    CreateGroupProductionRequest req,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            req.order_ids ??= new List<int>();
            req.process_codes ??= new List<string>();

            var preview = await PreviewCoreAsync(
                req,
                validateAgainstSuggestion: true,
                ct);

            preview = await ApplyMachineScheduleToPreviewAsync(
                preview,
                ct);

            return preview;
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
        private async Task CreateTasksFromPreviewBatchAsync(
    int prodId,
    int productTypeId,
    SuggestionBatchPreviewDto batch,
    string reason,
    CancellationToken ct)
        {
            if (batch.tasks == null || batch.tasks.Count == 0)
                throw new InvalidOperationException("Preview batch không có tasks để tạo.");

            var processCodes = batch.tasks
                .Select(x => NormProcessCode(x.process_code))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var processRows = await _db.product_type_processes
                .AsNoTracking()
                .Where(x =>
                    x.product_type_id == productTypeId &&
                    (x.is_active ?? true))
                .ToListAsync(ct);

            foreach (var taskPlan in batch.tasks
                         .OrderBy(x => x.seq_num)
                         .ThenBy(x => FullRouteIndex(x.process_code)))
            {
                var code = NormProcessCode(taskPlan.process_code);

                var process = processRows.FirstOrDefault(x =>
                    string.Equals(
                        NormProcessCode(x.process_code),
                        code,
                        StringComparison.OrdinalIgnoreCase));

                if (process == null)
                {
                    throw new InvalidOperationException(
                        $"Không tìm thấy product_type_process cho product_type_id={productTypeId}, process={code}.");
                }

                await _db.tasks.AddAsync(new task
                {
                    prod_id = prodId,
                    process_id = process.process_id,
                    seq_num = process.seq_num,
                    name = process.process_name ?? code,
                    status = "Unassigned",
                    machine = !string.IsNullOrWhiteSpace(taskPlan.machine)
                        ? taskPlan.machine
                        : process.machine,
                    input_mode = "MANUAL",
                    planned_start_time = taskPlan.planned_start_time,
                    planned_end_time = taskPlan.planned_end_time,
                    reason = reason
                }, ct);
            }
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

            var allCleanRows = await LoadCleanRowsForSuggestionAsync(
                productTypeId: null,
                selectedCodes: selectedCodes,
                ct: ct);

            if (allCleanRows.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var result = new List<SuggestedGroupProductionDto>();

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

            return selectedSet.All(suggestionSet.Contains);
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

        private static List<string> ResolveSubTaskCheckCodesForGroup(
            List<string>? selectedCodes)
        {
            var codes = NormalizeSelectedCodesForGroup(selectedCodes);

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

        private async Task<GroupProductionConfirmPreviewResponse> ApplyMachineScheduleToPreviewAsync(
    GroupProductionConfirmPreviewResponse preview,
    CancellationToken ct)
        {
            if (preview == null)
                throw new InvalidOperationException("Preview is null.");

            if (!preview.product_type_id.HasValue || preview.product_type_id.Value <= 0)
                return preview;

            preview.timeline ??= new List<GroupProductionScheduleStageDto>();
            preview.batches ??= new List<SuggestionBatchPreviewDto>();
            preview.orders ??= new List<SuggestionOrderPreviewDto>();
            preview.order_ids ??= new List<int>();

            if (preview.timeline.Count == 0)
                return preview;

            var productTypeId = preview.product_type_id.Value;

            var anchor = await ProductionMachinePlanHelper.ResolveEarliestScheduleAnchorAsync(
                _db,
                _cal,
                ct);

            var reservations = new List<MachineReservationDto>();

            var allOrderIds = preview.order_ids
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (allOrderIds.Count == 0)
            {
                allOrderIds = preview.orders
                    .Where(x => x.order_id > 0)
                    .Select(x => x.order_id)
                    .Distinct()
                    .ToList();
            }

            var orderReadyAt = allOrderIds.ToDictionary(
                x => x,
                _ => anchor);

            var orderedStages = preview.timeline
                .OrderBy(x => StageOrder(x.stage_type))
                .ThenBy(x => x.planned_start_date)
                .ThenBy(x => x.order_ids == null || x.order_ids.Count == 0
                    ? int.MaxValue
                    : x.order_ids.Min())
                .ToList();

            foreach (var stage in orderedStages)
            {
                stage.process_codes ??= new List<string>();
                stage.order_ids ??= new List<int>();

                var processCodes = stage.process_codes
                    .Select(NormProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList();

                if (processCodes.Count == 0)
                    continue;

                var stageOrderIds = stage.order_ids
                    .Where(x => x > 0)
                    .Distinct()
                    .ToList();

                var earliestStart = anchor;

                if (stageOrderIds.Count > 0)
                {
                    earliestStart = stageOrderIds
                        .Select(orderId => orderReadyAt.TryGetValue(orderId, out var readyAt)
                            ? readyAt
                            : anchor)
                        .DefaultIfEmpty(anchor)
                        .Max();
                }

                var stageQty = ResolvePreviewStageQuantity(
                    preview,
                    stage);

                var requiredUnits = BuildRequiredUnitsForGroupStage(
                    preview,
                    stage,
                    stageQty);

                var machinePlans = await ProductionMachinePlanHelper.BuildMachinePlanAsync(
                    db: _db,
                    cal: _cal,
                    productTypeId: productTypeId,
                    processCodes: processCodes,
                    quantity: stageQty,
                    earliestStart: earliestStart,
                    inMemoryReservations: reservations,
                    requiredUnitsByProcessCode: requiredUnits,
                    ct: ct);

                if (machinePlans.Count == 0)
                    continue;

                stage.planned_start_date = machinePlans.Min(x => x.planned_start_time);
                stage.planned_end_date = machinePlans.Max(x => x.planned_end_time);
                stage.duration_days = Math.Max(
                    1,
                    (int)Math.Ceiling((stage.planned_end_date - stage.planned_start_date).TotalDays));

                foreach (var orderId in stageOrderIds)
                {
                    orderReadyAt[orderId] = stage.planned_end_date;
                }

                SyncPreviewBatchFromStageAndPlans(
                    preview,
                    stage,
                    machinePlans);
            }

            preview.timeline = preview.timeline
                .OrderBy(x => x.planned_start_date)
                .ThenBy(x => StageOrder(x.stage_type))
                .ThenBy(x => x.order_ids == null || x.order_ids.Count == 0
                    ? int.MaxValue
                    : x.order_ids.Min())
                .ToList();

            preview.private_stages = preview.timeline
                .Where(x =>
                    string.Equals(x.stage_type, "SINGLE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.stage_type, "SINGLE_PRIVATE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            preview.group_stages = preview.timeline
                .Where(x => string.Equals(x.stage_type, "GROUP", StringComparison.OrdinalIgnoreCase))
                .ToList();

            preview.split_stages = preview.timeline
                .Where(x => string.Equals(x.stage_type, "SPLIT", StringComparison.OrdinalIgnoreCase))
                .ToList();

            preview.dept1_private_stage = preview.private_stages.FirstOrDefault();

            if (preview.timeline.Count > 0)
            {
                preview.suggested_planned_start_date = preview.timeline.Min(x => x.planned_start_date);
                preview.estimated_finish_date = preview.timeline.Max(x => x.planned_end_date);
                preview.total_duration_days = Math.Max(
                    1,
                    (int)Math.Ceiling((preview.estimated_finish_date - preview.suggested_planned_start_date).TotalDays));

                preview.can_meet_common_deadline =
                    preview.common_delivery_deadline == default ||
                    preview.estimated_finish_date.Date <= preview.common_delivery_deadline.Date;

                preview.days_late_if_any = preview.can_meet_common_deadline
                    ? 0
                    : Math.Max(0, (preview.estimated_finish_date.Date - preview.common_delivery_deadline.Date).Days);
            }

            return preview;
        }

        private void SyncPreviewBatchFromStageAndPlans(
    GroupProductionConfirmPreviewResponse preview,
    GroupProductionScheduleStageDto stage,
    List<MachineTaskPlanDto> plans)
        {
            preview.batches ??= new List<SuggestionBatchPreviewDto>();
            preview.orders ??= new List<SuggestionOrderPreviewDto>();

            var batch = FindPreviewBatchByStage(
                preview.batches,
                stage);

            if (batch == null)
            {
                batch = new SuggestionBatchPreviewDto
                {
                    batch_type = stage.stage_type,
                    prod_kind = ResolveProdKindFromStageType(stage.stage_type),
                    department_code = stage.dept_code,
                    department_name = stage.dept_name,
                    order_ids = stage.order_ids?.ToList() ?? new List<int>(),
                    process_codes = stage.process_codes?.ToList() ?? new List<string>(),
                    note = ResolveFixedBatchNote(stage.stage_type)
                };

                preview.batches.Add(batch);
            }

            batch.batch_type = stage.stage_type;
            batch.prod_kind = ResolveProdKindFromStageType(stage.stage_type);
            batch.department_code = stage.dept_code;
            batch.department_name = stage.dept_name;

            batch.order_ids = stage.order_ids?.ToList() ?? new List<int>();
            batch.process_codes = stage.process_codes?
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList() ?? new List<string>();

            var orderCodeMap = preview.orders
                .Where(x => x.order_id > 0)
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().order_code);

            batch.order_codes = batch.order_ids
                .Select(orderId => orderCodeMap.TryGetValue(orderId, out var code)
                    ? code
                    : null)
                .ToList();

            batch.planned_start_date = stage.planned_start_date;
            batch.planned_end_date = stage.planned_end_date;
            batch.duration_days = stage.duration_days;

            batch.tasks = plans
                .OrderBy(x => x.seq_num)
                .ThenBy(x => FullRouteIndex(x.process_code))
                .Select(x =>
                {
                    var deptCode = ResolveDepartmentCode(x.process_code);

                    return new SuggestionTaskPreviewDto
                    {
                        process_code = x.process_code,
                        process_name = x.process_name,
                        department_code = deptCode,
                        department_name = ResolveDepartmentName(deptCode),
                        machine = x.machine_code,
                        seq_num = x.seq_num,
                        planned_start_time = x.planned_start_time,
                        planned_end_time = x.planned_end_time
                    };
                })
                .ToList();
        }

        private static decimal ResolvePreviewStageQuantity(
    GroupProductionConfirmPreviewResponse preview,
    GroupProductionScheduleStageDto stage)
        {
            if (preview.orders == null || preview.orders.Count == 0)
                return 1m;

            var orderIds = stage.order_ids?
                .Where(x => x > 0)
                .Distinct()
                .ToHashSet() ?? new HashSet<int>();

            var qty = preview.orders
                .Where(x => orderIds.Count == 0 || orderIds.Contains(x.order_id))
                .Sum(x => x.quantity);

            if (qty <= 0)
                qty = preview.orders.Sum(x => x.quantity);

            return Math.Max(1m, qty);
        }

        private static Dictionary<string, decimal> BuildRequiredUnitsForGroupStage(
            GroupProductionConfirmPreviewResponse preview,
            GroupProductionScheduleStageDto stage,
            decimal quantity)
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            var processCodes = stage.process_codes ?? new List<string>();

            foreach (var codeRaw in processCodes)
            {
                var code = NormProcessCode(codeRaw);

                if (string.IsNullOrWhiteSpace(code))
                    continue;

                result[code] = Math.Max(1m, quantity);
            }

            return result;
        }

        private static SuggestionBatchPreviewDto? FindPreviewBatchByStage(
            List<SuggestionBatchPreviewDto> batches,
            GroupProductionScheduleStageDto stage)
        {
            if (batches == null || batches.Count == 0)
                return null;

            var stageType = NormProcessCode(stage.stage_type);

            var stageOrders = stage.order_ids?
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList() ?? new List<int>();

            var stageProcesses = stage.process_codes?
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList() ?? new List<string>();

            return batches.FirstOrDefault(batch =>
            {
                var batchType = NormProcessCode(batch.batch_type);

                var batchOrders = batch.order_ids?
                    .Where(x => x > 0)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList() ?? new List<int>();

                var batchProcesses = batch.process_codes?
                    .Select(NormProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList() ?? new List<string>();

                return string.Equals(batchType, stageType, StringComparison.OrdinalIgnoreCase)
                       && SameIntList(batchOrders, stageOrders)
                       && SameStringList(batchProcesses, stageProcesses);
            });
        }

        private static bool SameIntList(List<int> a, List<int> b)
        {
            a ??= new List<int>();
            b ??= new List<int>();

            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        private static bool SameStringList(List<string> a, List<string> b)
        {
            a ??= new List<string>();
            b ??= new List<string>();

            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static string ResolveProdKindFromStageType(string? stageType)
        {
            var type = NormProcessCode(stageType);

            return type switch
            {
                "GROUP" => "GROUP",
                "SPLIT" => "SPLIT",
                "SINGLE" => "SINGLE",
                "SINGLE_PRIVATE" => "SINGLE",
                _ => "SINGLE"
            };
        }

        private static string NormalizeProductionApprovalFlowOrDefault(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return ProductionApprovalFlowHelper.ManualGeneralManager;

            var flow = value.Trim().ToUpperInvariant();

            return flow switch
            {
                ProductionApprovalFlowHelper.AutoSingleOption => ProductionApprovalFlowHelper.AutoSingleOption,
                ProductionApprovalFlowHelper.WaitingManager => ProductionApprovalFlowHelper.WaitingManager,
                ProductionApprovalFlowHelper.ManualManager => ProductionApprovalFlowHelper.ManualManager,
                ProductionApprovalFlowHelper.ManualGeneralManager => ProductionApprovalFlowHelper.ManualGeneralManager,
                _ => ProductionApprovalFlowHelper.ManualGeneralManager
            };
        }

        private static string ResolveGroupProductionApprovalFlow()
        {
            return ProductionApprovalFlowHelper.ManualGeneralManager;
        }

        private static string ResolveSplitProductionApprovalFlow(production? sourceSingleProd)
        {
            return NormalizeProductionApprovalFlowOrDefault(
                sourceSingleProd?.production_approval_flow);
        }

        private static void EnsureSingleMemberApprovalFlow(production singleProd)
        {
            if (singleProd == null)
                return;

            if (!string.IsNullOrWhiteSpace(singleProd.production_approval_flow))
            {
                singleProd.production_approval_flow =
                    NormalizeProductionApprovalFlowOrDefault(singleProd.production_approval_flow);
                return;
            }

            singleProd.production_approval_flow = ProductionApprovalFlowHelper.ManualGeneralManager;
        }
    }
}
