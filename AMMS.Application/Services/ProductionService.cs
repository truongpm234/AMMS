using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class ProductionService : IProductionService
    {
        private readonly IProductionRepository _repo;
        private readonly IRealtimePublisher _hub;
        private readonly IOrderRepository _orderRepo;
        private readonly AppDbContext _db;
        private readonly IHubContext<RealtimeHub> _rt;
        private readonly NotificationService _notiService;
        private readonly IRequestRepository _requestRepo;
        private readonly ICloudinaryFileStorageService _fileStorage;
        private readonly IProductionSchedulingService _scheduling;
        public ProductionService(
            IHubContext<RealtimeHub> rt,
            IProductionRepository repo,
            IRealtimePublisher hub,
            AppDbContext db,
            IOrderRepository orderRepository,
            IRequestRepository requestRepository,
            NotificationService notiService,
            IWebHostEnvironment env,
            ICloudinaryFileStorageService fileStorage,
            IProductionSchedulingService scheduling)
        {
            _db = db;
            _rt = rt;
            _repo = repo;
            _hub = hub;
            _orderRepo = orderRepository;
            _notiService = notiService;
            _requestRepo = requestRepository;
            _env = env;
            _fileStorage = fileStorage;
            _scheduling = scheduling;
        }

        public async Task<NearestDeliveryResponse> GetNearestDeliveryAsync()
        {
            var nearestDate = await _repo.GetNearestDeliveryDateAsync();

            var days = nearestDate == null
                ? 0
                : Math.Max(1, (nearestDate.Value.Date - DateTime.UtcNow.Date).Days);

            return new NearestDeliveryResponse
            {
                nearest_delivery_date = nearestDate,
                days_until_free = days
            };
        }

        public Task<List<string>> GetAllProcessTypeAsync()
        {
            var result = Enum.GetNames(typeof(ProcessType)).ToList();
            return Task.FromResult(result);
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByProdIdAsync(
    int prodId,
    CancellationToken ct = default)
        {
            return await _repo.GetProductionDetailByProdIdAsync(prodId, ct);
        }

        public Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            return _repo.GetProducingOrdersAsync(page, pageSize, ct);
        }

        public async Task<ProductionProgressResponse> GetProgressAsync(int prodId)
        {
            return await _repo.GetProgressAsync(prodId);
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _repo.GetProductionDetailByOrderIdAsync(orderId, ct);
        }

        public async Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default)
        {
            return await _repo.GetProductionWasteAsync(prodId, ct);
        }

        public async Task<bool> SetProductionDeliveryAsync(int orderId, CancellationToken ct = default)
        {
            return await _repo.SetProductionDeliveryByOrderIdAsync(orderId, ct);
        }

        public async Task<bool> SetCompletedAsync(int orderId, CancellationToken ct = default)
        {
            return await _repo.SetCompletedByOrderIdAsync(orderId, ct);
        }

        public async Task<int?> StartProductionAndPromoteFirstTaskByProdIdAsync(
     int prodId,
     CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null)
                return null;

            if (string.Equals(prod.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prod.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prod.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Không thể bắt đầu production vì production đang ở trạng thái {prod.status}.");
            }

            var isGroupProduction = string.Equals(
                prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

            var isSplitProduction = string.Equals(
                prod.prod_kind,
                "SPLIT",
                StringComparison.OrdinalIgnoreCase);

            /*
             * SINGLE/SPLIT: production có order_id, check order ready như logic cũ.
             */
            if (!isGroupProduction)
            {
                if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                    throw new InvalidOperationException("Production chưa gắn với order.");

                var ord = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == prod.order_id.Value, ct);

                if (ord == null)
                    throw new InvalidOperationException("Order not found");

                if (!ord.is_production_ready)
                    throw new InvalidOperationException(
                        "General manager has not confirmed production readiness.");
            }

            /*
             * GROUP: không có order_id trực tiếp.
             * Check tất cả order member active đã được ready.
             */
            if (isGroupProduction)
            {
                var memberOrders = await (
                    from po in _db.prod_orders.AsNoTracking()
                    join o in _db.orders.AsNoTracking()
                        on po.order_id equals o.order_id
                    where po.prod_id == prod.prod_id
                          && po.status == "Active"
                    select new
                    {
                        o.order_id,
                        o.code,
                        o.is_production_ready
                    }
                ).ToListAsync(ct);

                if (memberOrders.Count < 2)
                    throw new InvalidOperationException("Production ghép cần ít nhất 2 order active.");

                var notReady = memberOrders
                    .Where(x => !x.is_production_ready)
                    .ToList();

                if (notReady.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Không thể bắt đầu production ghép vì còn order chưa được xác nhận sẵn sàng sản xuất: " +
                        string.Join(", ", notReady.Select(x => $"{x.order_id}-{x.code}")));
                }
            }

            /*
             * Quan trọng cho GROUP/SPLIT:
             * Chặn start nếu công đoạn trước đó chưa xong.
             * Ví dụ:
             * - GROUP PHU,CAN chỉ start khi SINGLE RALO,CAT,IN xong.
             * - SPLIT BE,DUT,DAN chỉ start khi GROUP PHU,CAN xong.
             */
            var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                _db,
                prod.prod_id,
                ct);

            if (!dep.can_start)
            {
                throw new InvalidOperationException(
                    "Chưa thể bắt đầu production vì công đoạn trước đó chưa hoàn thành. " +
                    dep.message);
            }

            return await _repo.StartProductionByProdIdOnlyAsync(
                prod.prod_id,
                AppTime.NowVnUnspecified(),
                ct);
        }

        public async Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(
            DateTime from,
            DateTime to,
            CancellationToken ct = default)
        {
            return await _repo.GetMachineScheduleBoardAsync(from, to, ct);
        }

        public async Task<ProductionReadyCheckResponse?> GetProductionReadyAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var ord = await _orderRepo.GetByIdAsync(orderId);

            if (ord == null)
                return null;

            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.product_type_id,
                    x.production_process,
                    x.quantity
                })
                .FirstOrDefaultAsync(ct);

            var orderQty = req?.quantity ?? 0;

            if (orderQty <= 0)
                orderQty = item?.quantity ?? 0;

            if (orderQty <= 0)
                orderQty = 1;

            var fullMaterials = await GetMaterialReadinessAsync(orderId, null, ct);
            var machines = await GetMachineReadinessAsync(orderId, ct);

            var hasEnoughMaterial =
                fullMaterials.Count > 0 &&
                fullMaterials.All(x => x.is_enough);

            var hasFreeMachine =
                machines.Count > 0 &&
                machines.All(x => x.machine_found && x.is_available);

            var validSubOptions = await FindValidSubProductOptionsAsync(
                orderId,
                orderQty,
                requireEnoughQty: false,
                ct);

            var subSelection = SelectSubOptionForMethod(
    validSubOptions,
    orderQty);

            var displaySubOption = subSelection.DisplayOption;
            var bestSubEnoughOption = subSelection.EnoughForSubOption;
            var bestSubPartialOption = subSelection.PartialForBothOption;

            var matchedSubProduct = displaySubOption == null
                ? null
                : ToMatchedSubProductDto(
                    displaySubOption.Sub,
                    displaySubOption.reason);

            var hasMatchedSubProduct = matchedSubProduct != null;

            var displaySubQty = matchedSubProduct?.quantity ?? 0;
            var subQtyForBoth = bestSubPartialOption?.Sub.quantity ?? 0;

            var canUseNvl =
                hasEnoughMaterial &&
                hasFreeMachine;

            var canUseSub =
                bestSubEnoughOption != null &&
                orderQty > 0 &&
                hasFreeMachine;

            var nvlQtyForBoth =
                bestSubPartialOption != null &&
                orderQty > 0 &&
                subQtyForBoth > 0 &&
                subQtyForBoth < orderQty
                    ? orderQty - subQtyForBoth
                    : 0;

            var remainingMaterialsForBoth = nvlQtyForBoth > 0
                ? await GetMaterialReadinessAsync(orderId, nvlQtyForBoth, ct)
                : new List<ProductionReadyMaterialDto>();

            var canUseBoth =
                bestSubPartialOption != null &&
                orderQty > subQtyForBoth &&
                subQtyForBoth > 0 &&
                remainingMaterialsForBoth.Count > 0 &&
                remainingMaterialsForBoth.All(x => x.is_enough) &&
                hasFreeMachine;

            var optionCount = 0;

            if (canUseNvl)
                optionCount++;

            if (canUseSub)
                optionCount++;

            if (canUseBoth)
                optionCount++;

            var needManagerApproval = optionCount >= 2;

            string? subMessage;

            if (!hasMatchedSubProduct)
            {
                subMessage = "Không có bán thành phẩm phù hợp với đơn hàng theo product_type, kích thước, path và NVL theo stage.";
            }
            else if (bestSubEnoughOption != null)
            {
                subMessage = "Có bán thành phẩm phù hợp và đủ số lượng để dùng SUB.";
            }
            else
            {
                subMessage =
                    $"Có bán thành phẩm phù hợp nhưng chưa đủ số lượng, có thể dùng BOTH nếu NVL còn lại đủ. " +
                    $"Có {displaySubQty}, cần {orderQty}, còn thiếu {Math.Max(orderQty - displaySubQty, 0)}.";
            }

            var methodCostOptions = await BuildMethodCostOptionsAsync(
    orderId,
    orderQty,
    item?.production_process,
    bestSubEnoughOption,
    bestSubPartialOption,
    ct);

            foreach (var option in methodCostOptions)
            {
                if (string.Equals(option.method, "NVL", StringComparison.OrdinalIgnoreCase))
                    option.is_available = canUseNvl;

                if (string.Equals(option.method, "SUB", StringComparison.OrdinalIgnoreCase))
                    option.is_available = canUseSub;

                if (string.Equals(option.method, "BOTH", StringComparison.OrdinalIgnoreCase))
                    option.is_available = canUseBoth;
            }

            NormalizeStartReadyCostOptionsByRoundedTotal(
                methodCostOptions,
                orderQty);

            var nvlCost = methodCostOptions
                .FirstOrDefault(x => string.Equals(x.method, "NVL", StringComparison.OrdinalIgnoreCase));

            var subCost = methodCostOptions
                .FirstOrDefault(x => string.Equals(x.method, "SUB", StringComparison.OrdinalIgnoreCase));

            var bothCost = methodCostOptions
                .FirstOrDefault(x => string.Equals(x.method, "BOTH", StringComparison.OrdinalIgnoreCase));

            return new ProductionReadyCheckResponse
            {
                order_id = orderId,
                production_id = prod?.prod_id,
                is_production_ready = ord.is_production_ready,
                has_enough_material = hasEnoughMaterial,
                has_free_machine = hasFreeMachine,
                materials = fullMaterials,
                remaining_materials_for_both = remainingMaterialsForBoth,
                machines = machines,
                product_type_id = prod?.product_type_id ?? item?.product_type_id,
                request_print_width_mm = req?.print_width_mm,
                request_print_length_mm = req?.print_length_mm,
                order_quantity = orderQty,
                production_approval_flow = prod?.production_approval_flow,
                is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(prod?.production_approval_flow),
                production_approval_label = ProductionApprovalFlowHelper.Label(prod?.production_approval_flow),
                is_full_process = prod?.is_full_process,
                production_method = prod?.prod_method,
                gm_proposed_method = prod?.gm_proposed_method,
                proposed_production_method = prod?.gm_proposed_method,
                sub_product_issue_file = prod?.sub_product_issue_file,
                gm_note = prod?.gm_note,
                mgr_note = prod?.mgr_note,
                can_use_nvl = canUseNvl,
                can_use_sub = canUseSub,
                can_use_both = canUseBoth,
                need_manager_approval = needManagerApproval,
                nvl_qty = prod?.nvl_qty ?? nvlQtyForBoth,
                selected_sub_product_id = prod?.sub_product_id ?? displaySubOption?.Sub.id,
                sub_product_used_qty = prod?.sub_product_used_qty ?? 0,
                has_matched_sub_product = hasMatchedSubProduct,
                sub_product_message = subMessage,
                matched_sub_product = matchedSubProduct,
                method_cost_options = methodCostOptions,
                nvl_estimated_unit_cost = nvlCost?.unit_cost,
                sub_estimated_unit_cost = subCost?.is_available == true
                ? subCost.unit_cost
                : null,
                both_estimated_unit_cost = bothCost?.is_available == true
                ? bothCost.unit_cost
                : null,
                nvl_estimated_total_cost = nvlCost?.total_cost,
                sub_estimated_total_cost = subCost?.is_available == true
                ? subCost.total_cost
                : null,
                both_estimated_total_cost = bothCost?.is_available == true
                ? bothCost.total_cost
                : null
            };
        }

        public async Task<bool> SetProductionReadyAsync(
    int orderId,
    bool isProductionReady,
    string? gmNote = null,
    string? proposedProductionMethod = null,
    CancellationToken ct = default)
        {
            var shouldAutoSchedule = false;

            var strategy = _db.Database.CreateExecutionStrategy();

            var result = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var ord = await _db.orders
                    .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

                if (ord == null)
                    return false;

                var prod = await _db.productions
                    .Where(x => x.order_id == orderId)
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
                        "Không thể thay đổi trạng thái sẵn sàng sản xuất vì đơn hàng đã bắt đầu hoặc đã hoàn tất sản xuất.");
                }

                prod.gm_note = NormalizeNote(gmNote);

                var proposedMethod = NormalizeProductionMethodOrNull(proposedProductionMethod);
                prod.gm_proposed_method = proposedMethod;

                if (!isProductionReady)
                {
                    ord.is_production_ready = false;
                    ord.is_enough = false;

                    await RollbackPreviousSubProductSelectionAsync(prod, ct);
                    await RollbackSubProductFinishedTasksAsync(prod.prod_id, ct);

                    prod.prod_method = null;
                    prod.gm_proposed_method = null;
                    prod.is_full_process = null;
                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                    prod.nvl_qty = 0;
                    prod.production_approval_flow = null;
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                var req = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

                if (req == null)
                    throw new InvalidOperationException("Order request not found for this order.");

                var orderQty = req.quantity ?? 0;

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
                    throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ.");

                var fullMaterials = await GetMaterialReadinessAsync(orderId, null, ct);
                var machines = await GetMachineReadinessAsync(orderId, ct);

                var hasEnoughMaterial =
                    fullMaterials.Count > 0 &&
                    fullMaterials.All(x => x.is_enough);

                var hasFreeMachine =
                    machines.Count > 0 &&
                    machines.All(x => x.machine_found && x.is_available);

                var validSubOptions = await FindValidSubProductOptionsAsync(
                    orderId,
                    orderQty,
                    requireEnoughQty: false,
                    ct);

                var subSelection = SelectSubOptionForMethod(
    validSubOptions,
    orderQty);

                var displaySubOption = subSelection.DisplayOption;
                var bestSubEnoughOption = subSelection.EnoughForSubOption;
                var bestSubPartialOption = subSelection.PartialForBothOption;

                var matchedSubProduct = displaySubOption?.Sub;

                var displaySubQty = matchedSubProduct?.quantity ?? 0;
                var subQtyForBoth = bestSubPartialOption?.Sub.quantity ?? 0;

                var canUseNvl =
                    hasEnoughMaterial &&
                    hasFreeMachine;

                var canUseSub =
    bestSubEnoughOption != null &&
    orderQty > 0 &&
    hasFreeMachine;

                var nvlQtyForBoth =
                    bestSubPartialOption != null &&
                    orderQty > 0 &&
                    subQtyForBoth > 0 &&
                    subQtyForBoth < orderQty
                        ? orderQty - subQtyForBoth
                        : 0;

                var remainingMaterialsForBoth = nvlQtyForBoth > 0
                    ? await GetMaterialReadinessAsync(orderId, nvlQtyForBoth, ct)
                    : new List<ProductionReadyMaterialDto>();

                var canUseBoth =
                    bestSubPartialOption != null &&
                    orderQty > subQtyForBoth &&
                    subQtyForBoth > 0 &&
                    remainingMaterialsForBoth.Count > 0 &&
                    remainingMaterialsForBoth.All(x => x.is_enough) &&
                    hasFreeMachine;

                var optionCount = 0;

                if (canUseNvl)
                    optionCount++;

                if (canUseSub)
                    optionCount++;

                if (canUseBoth)
                    optionCount++;

                if (optionCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Order chưa đủ điều kiện sản xuất: thiếu NVL, thiếu bán thành phẩm hoặc chưa có đủ máy rảnh.");
                }

                if (proposedMethod == "NVL" && !canUseNvl)
                {
                    throw new InvalidOperationException(
                        "Không thể đề xuất NVL vì chưa đủ NVL hoặc chưa có máy phù hợp.");
                }

                if (proposedMethod == "SUB" && !canUseSub)
                {
                    throw new InvalidOperationException(
                        "Không thể đề xuất SUB vì chưa có bán thành phẩm phù hợp hoặc chưa đủ số lượng.");
                }

                if (proposedMethod == "BOTH" && !canUseBoth)
                {
                    throw new InvalidOperationException(
                        "Không thể đề xuất BOTH vì chưa đủ điều kiện dùng bán thành phẩm kết hợp NVL.");
                }

                /*
                 * RULE MỚI:
                 * Nếu chỉ có đúng 1 method khả dụng => auto duyệt luôn.
                 *
                 * Ví dụ:
                 * - Chỉ canUseNvl  => auto NVL
                 * - Chỉ canUseSub  => auto SUB
                 * - Chỉ canUseBoth => auto BOTH
                 */
                var onlyAvailableMethod = ResolveOnlyAvailableProductionMethod(
    canUseNvl,
    canUseSub,
    canUseBoth);

                if (optionCount == 1 && onlyAvailableMethod != null)
                {
                    if (proposedMethod != null &&
                        !string.Equals(proposedMethod, onlyAvailableMethod, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Chỉ có một phương thức khả dụng là {onlyAvailableMethod}, không thể đề xuất {proposedMethod}.");
                    }

                    /*
                     * Giữ lại đề xuất GM nếu có.
                     * Nếu GM không đề xuất gì thì có thể lưu chính method auto để tracking.
                     */
                    prod.gm_proposed_method ??= onlyAvailableMethod;
                    await _rt.Clients.Group(RealtimeGroups.ByRole("production manager")).SendAsync("auto scheduled", new { message = $"Lệnh sản xuất {prod.prod_id} đã được tự động duyệt" });
                    await _notiService.CreateNotfi(6, $"Lệnh sản xuất {prod.prod_id} đã được tự động duyệt", null, prod.prod_id, "Scheduled");

                    var selectedSubOptionForAuto = onlyAvailableMethod switch
                    {
                        "SUB" => bestSubEnoughOption,
                        "BOTH" => bestSubPartialOption,
                        _ => null
                    };

                    await ApplyAutoProductionMethodAsync(
                        ord,
                        prod,
                        req,
                        onlyAvailableMethod,
                        orderQty,
                        selectedSubOptionForAuto?.Sub.id,
                        ct);

                    prod.production_approval_flow = ProductionApprovalFlowHelper.AutoSingleOption;

                    var confirmedMethod = prod.prod_method;

                    if (!string.IsNullOrWhiteSpace(confirmedMethod))
                    {
                        var confirmedEstimate = await LoadAcceptedEstimateOrThrowAsync(
                            req,
                            ct);

                        await ReserveMaterialsForConfirmedProductionMethodAsync(
                            prod,
                            req,
                            confirmedEstimate,
                            confirmedMethod,
                            orderQty,
                            ct);
                    }

                    shouldAutoSchedule = true;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                /*
                 * Nếu có từ 2 option trở lên:
                 * - Không auto duyệt.
                 * - Chỉ lưu gợi ý của GM nếu có.
                 * - Đợi manager gọi API /production-method để xác nhận.
                 */
                ord.is_production_ready = false;

                await RollbackPreviousSubProductSelectionAsync(prod, ct);
                await RollbackSubProductFinishedTasksAsync(prod.prod_id, ct);

                prod.prod_method = null;
                prod.is_full_process = null;
                prod.sub_product_id = null;
                prod.sub_product_used_qty = 0;
                prod.nvl_qty = 0;
                prod.production_approval_flow = ProductionApprovalFlowHelper.WaitingManager;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return true;
            });

            if (result && shouldAutoSchedule)
            {
                await ScheduleTasksAfterMethodAsync(orderId, ct);
            }

            return result;
        }

        private static string? ResolveOnlyAvailableProductionMethod(
    bool canUseNvl,
    bool canUseSub,
    bool canUseBoth)
        {
            var methods = new List<string>();

            if (canUseNvl)
                methods.Add("NVL");

            if (canUseSub)
                methods.Add("SUB");

            if (canUseBoth)
                methods.Add("BOTH");

            return methods.Count == 1
                ? methods[0]
                : null;
        }

        private static readonly HashSet<string> SubHeadCodesForService = new(StringComparer.OrdinalIgnoreCase)
{
    "RALO",
    "CAT",
    "IN"
};

        private async Task<bool> IsFinishedSubHeadProductionWaitingImportingAsync(
            int prodId,
            CancellationToken ct)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null)
                return false;

            if (string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase))
                return false;

            var tasks = await (
                from t in _db.tasks.AsNoTracking()
                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()
                where t.prod_id == prodId
                orderby t.seq_num, t.task_id
                select new
                {
                    process_code = pp != null ? pp.process_code : null,
                    t.status,
                    t.end_time
                }
            ).ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            var codes = tasks
                .Select(x => NormCodeForService(x.process_code))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (codes.Count == 0)
                return false;

            var isSubHead = codes.All(x => SubHeadCodesForService.Contains(x));

            if (!isSubHead)
                return false;

            var allFinished = tasks.All(x =>
                string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                x.end_time != null);

            return allFinished;
        }

        private static string NormCodeForService(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private async Task ApplyAutoProductionMethodAsync(
            order ord,
            production prod,
            order_request req,
            string method,
            int orderQty,
            int? matchedSubProductId,
            CancellationToken ct)
        {
            method = (method ?? "").Trim().ToUpperInvariant();

            /*
             * Nếu GM đã đề xuất thì giữ nguyên.
             * Nếu chưa có đề xuất thì ghi nhận method auto để tracking.
             */
            prod.gm_proposed_method ??= method;

            await RollbackPreviousSubProductSelectionAsync(prod, ct);

            if (method == "NVL")
            {
                await RollbackSubProductFinishedTasksAsync(prod.prod_id, ct);

                prod.prod_method = "NVL";
                prod.is_full_process = true;
                prod.sub_product_id = null;
                prod.sub_product_used_qty = 0;
                prod.nvl_qty = orderQty;

                ord.is_enough = true;
                ord.is_production_ready = true;

                return;
            }

            if (method == "SUB")
            {
                if (!matchedSubProductId.HasValue || matchedSubProductId.Value <= 0)
                    throw new InvalidOperationException("Không tìm thấy bán thành phẩm phù hợp để auto duyệt SUB.");

                var selectedSubProduct = await ResolveValidSubProductAsync(
                    matchedSubProductId.Value,
                    prod,
                    req,
                    orderQty,
                    requireEnoughQty: true,
                    ct);

                if (selectedSubProduct.quantity < orderQty)
                {
                    throw new InvalidOperationException(
                        $"Không đủ tồn bán thành phẩm. SubProductId={selectedSubProduct.id}, " +
                        $"tồn={selectedSubProduct.quantity}, cần={orderQty}.");
                }

                selectedSubProduct.quantity -= orderQty;
                selectedSubProduct.updated_at = AppTime.NowVnUnspecified();

                prod.prod_method = "SUB";
                prod.is_full_process = false;
                prod.sub_product_id = selectedSubProduct.id;
                prod.sub_product_used_qty = orderQty;
                prod.nvl_qty = 0;

                ord.is_enough = true;
                ord.is_production_ready = true;

                await CreateAndUploadSubProductIssueFileAsync(
                    prod,
                    ord,
                    req,
                    selectedSubProduct,
                    orderQty,
                    "Hệ thống tự động duyệt SUB vì chỉ có 1 phương thức sản xuất khả dụng.",
                    ct);

                await ApplySubProductToExistingTasksAsync(
                    prod,
                    selectedSubProduct,
                    orderQty,
                    ct);

                return;
            }

            if (method == "BOTH")
            {
                if (!matchedSubProductId.HasValue || matchedSubProductId.Value <= 0)
                    throw new InvalidOperationException("Không tìm thấy bán thành phẩm phù hợp để auto duyệt BOTH.");

                var selectedSubProduct = await ResolveValidSubProductAsync(
                    matchedSubProductId.Value,
                    prod,
                    req,
                    orderQty,
                    requireEnoughQty: false,
                    ct);

                var subUseQty = Math.Min(selectedSubProduct.quantity, orderQty);
                var nvlQty = orderQty - subUseQty;

                if (subUseQty <= 0)
                    throw new InvalidOperationException("Không có số lượng bán thành phẩm hợp lệ để dùng BOTH.");

                if (nvlQty <= 0)
                    throw new InvalidOperationException("Bán thành phẩm đã đủ số lượng, nên dùng SUB thay vì BOTH.");

                selectedSubProduct.quantity -= subUseQty;
                selectedSubProduct.updated_at = AppTime.NowVnUnspecified();

                prod.prod_method = "BOTH";
                prod.is_full_process = null;
                prod.sub_product_id = selectedSubProduct.id;
                prod.sub_product_used_qty = subUseQty;
                prod.nvl_qty = nvlQty;

                ord.is_enough = true;
                ord.is_production_ready = true;

                return;
            }

            throw new InvalidOperationException("Unsupported production method.");
        }

        private async Task RollbackPreviousSubProductSelectionAsync(
            production prod,
            CancellationToken ct)
        {
            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                return;

            if (prod.sub_product_used_qty <= 0)
                return;

            var oldSubProduct = await _db.sub_products
                .FirstOrDefaultAsync(x => x.id == prod.sub_product_id.Value, ct);

            if (oldSubProduct != null)
            {
                oldSubProduct.quantity += prod.sub_product_used_qty;
            }

            prod.sub_product_id = null;
            prod.sub_product_used_qty = 0;
            prod.sub_product_issue_file = null;
        }

        private static string? NormalizeProductionMethodOrNull(string? method)
        {
            if (string.IsNullOrWhiteSpace(method))
                return null;

            var value = method.Trim().ToUpperInvariant();

            if (value is not ("NVL" or "SUB" or "BOTH"))
                throw new InvalidOperationException("proposed_production_method must be NVL | SUB | BOTH.");

            return value;
        }

        private async Task SendProductionReadyNotificationAsync(int orderId)
        {
            var message = $"Đơn hàng {orderId} đã được xác nhận sẵn sàng sản xuất.";

            await _rt.Clients
                .Group(RealtimeGroups.ByRole("production manager"))
                .SendAsync("scheduled", new
                {
                    message = message
                });

            var req = await _requestRepo.GetByOderIdAsync(orderId);

            if (req != null)
            {
                await _notiService.CreateNotfi(
                    6,
                    message,
                    null,
                    req.order_request_id,
                    "Scheduled");
            }
        }

        private async Task<List<ProductionReadyMaterialDto>> GetMaterialReadinessAsync(
    int orderId,
    int? overrideOrderQty = null,
    CancellationToken ct = default)
        {
            var rows = await (
                from oi in _db.order_items.AsNoTracking()
                join b in _db.boms.AsNoTracking() on oi.item_id equals b.order_item_id
                join m in _db.materials.AsNoTracking() on b.material_id equals m.material_id into mj
                from m in mj.DefaultIfEmpty()
                where oi.order_id == orderId
                select new
                {
                    order_qty = overrideOrderQty.HasValue && overrideOrderQty.Value > 0 ? overrideOrderQty.Value : oi.quantity,
                    b.material_id,
                    b.material_code,
                    b.material_name,
                    b.unit,
                    b.qty_total,
                    b.qty_per_product,
                    b.wastage_percent,
                    material_exists = m != null,
                    db_material_code = m != null ? m.code : null,
                    db_material_name = m != null ? m.name : null,
                    db_unit = m != null ? m.unit : null,
                    stock_qty = m != null ? (m.stock_qty ?? 0m) : 0m
                }
            ).ToListAsync(ct);

            if (rows.Count == 0)
                return new List<ProductionReadyMaterialDto>();

            var result = rows
                .GroupBy(x => new
                {
                    x.material_id,
                    MaterialCode = !string.IsNullOrWhiteSpace(x.db_material_code) ? x.db_material_code : x.material_code,
                    MaterialName = !string.IsNullOrWhiteSpace(x.db_material_name) ? x.db_material_name : x.material_name,
                    Unit = !string.IsNullOrWhiteSpace(x.db_unit) ? x.db_unit : x.unit,
                    x.material_exists
                })
                .Select(g =>
                {
                    decimal requiredQty = 0m;

                    foreach (var line in g)
                    {
                        decimal lineQty;

                        if (line.qty_total.HasValue && line.qty_total.Value > 0m)
                        {
                            lineQty = line.qty_total.Value;
                        }
                        else
                        {
                            var orderQty = line.order_qty <= 0 ? 1 : line.order_qty;
                            var qtyPerProduct = line.qty_per_product ?? 0m;
                            var wasteFactor = 1m + ((line.wastage_percent ?? 0m) / 100m);

                            lineQty = orderQty * qtyPerProduct * wasteFactor;
                        }

                        if (lineQty < 0m) lineQty = 0m;
                        requiredQty += lineQty;
                    }

                    requiredQty = Math.Round(requiredQty, 4);

                    var mapped = g.Key.material_id.HasValue
                                 && g.Key.material_id.Value > 0
                                 && g.Key.material_exists;

                    var availableQty = mapped
                        ? Math.Round(g.Max(x => x.stock_qty), 4)
                        : 0m;

                    var missingQty = requiredQty - availableQty;
                    if (missingQty < 0m) missingQty = 0m;

                    var isEnough = mapped && missingQty <= 0m;

                    var status = !mapped
                        ? "Unmapped"
                        : isEnough ? "Enough" : "Missing";

                    return new ProductionReadyMaterialDto
                    {
                        material_id = g.Key.material_id,
                        material_code = g.Key.MaterialCode,
                        material_name = g.Key.MaterialName,
                        unit = g.Key.Unit,

                        required_qty = requiredQty,
                        available_qty = availableQty,
                        missing_qty = missingQty,

                        is_enough = isEnough,
                        status = status
                    };
                })
                .OrderBy(x => x.status == "Missing" ? 0 : x.status == "Unmapped" ? 1 : 2)
                .ThenBy(x => x.material_name)
                .ToList();

            return result;
        }

        private async Task<List<ProductionReadyMachineDto>> GetMachineReadinessAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var firstItem = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.product_type_id,
                    x.production_process
                })
                .FirstOrDefaultAsync(ct);

            int? productTypeId = prod?.product_type_id ?? firstItem?.product_type_id;

            if ((!productTypeId.HasValue || productTypeId.Value <= 0) && req != null)
            {
                var productTypeCode = req.product_type?.Trim();

                if (!string.IsNullOrWhiteSpace(productTypeCode))
                {
                    productTypeId = await _db.product_types
                        .AsNoTracking()
                        .Where(x => x.code == productTypeCode)
                        .Select(x => (int?)x.product_type_id)
                        .FirstOrDefaultAsync(ct);
                }
            }

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                return new List<ProductionReadyMachineDto>();

            string? estimateProductionProcesses = null;

            if (req != null)
            {
                var estQuery = _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == req.order_request_id);

                if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
                {
                    estimateProductionProcesses = await estQuery
                        .Where(x => x.estimate_id == req.accepted_estimate_id.Value)
                        .Select(x => x.production_processes)
                        .FirstOrDefaultAsync(ct);
                }

                if (string.IsNullOrWhiteSpace(estimateProductionProcesses))
                {
                    estimateProductionProcesses = await estQuery
                        .OrderByDescending(x => x.is_active)
                        .ThenByDescending(x => x.estimate_id)
                        .Select(x => x.production_processes)
                        .FirstOrDefaultAsync(ct);
                }
            }

            var selectedProcessesCsv = !string.IsNullOrWhiteSpace(firstItem?.production_process)
                ? firstItem!.production_process
                : estimateProductionProcesses;

            var targets = new List<MachineReadinessTarget>();

            // Ưu tiên lấy theo task nếu production đã có task.
            if (prod != null)
            {
                var tasks = await _db.tasks
                    .Include(x => x.process)
                    .AsNoTracking()
                    .Where(x => x.prod_id == prod.prod_id)
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .ToListAsync(ct);

                if (tasks.Count > 0)
                {
                    targets = tasks
                        .Select(t => new MachineReadinessTarget
                        {
                            process_id = t.process_id,
                            seq_num = t.seq_num,
                            process_code = t.process != null ? t.process.process_code : null,
                            process_name = t.process != null ? t.process.process_name : t.name,
                            machine_code = t.machine
                        })
                        .OrderBy(x => x.seq_num ?? int.MaxValue)
                        .ThenBy(x => x.process_id ?? int.MaxValue)
                        .ToList();
                }
            }

            // Nếu chưa có task thì fallback sang product_type_process.
            if (targets.Count == 0)
            {
                var routeSteps = await _db.product_type_processes
                    .AsNoTracking()
                    .Where(x =>
                        x.product_type_id == productTypeId.Value &&
                        (x.is_active ?? true))
                    .OrderBy(x => x.seq_num)
                    .Select(x => new MachineReadinessTarget
                    {
                        process_id = x.process_id,
                        seq_num = x.seq_num,
                        process_code = x.process_code,
                        process_name = x.process_name,
                        machine_code = x.machine
                    })
                    .ToListAsync(ct);

                targets = ResolveFixedMachineTargets(routeSteps, selectedProcessesCsv);
            }

            if (targets.Count == 0)
                return new List<ProductionReadyMachineDto>();

            var allMachines = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.process_code)
                .ThenBy(x => x.machine_code)
                .ToListAsync(ct);

            var result = new List<ProductionReadyMachineDto>();

            foreach (var target in targets)
            {
                var machine = ResolveMachineForTarget(target, allMachines);

                if (machine == null)
                {
                    result.Add(new ProductionReadyMachineDto
                    {
                        process_id = target.process_id,
                        seq_num = target.seq_num,
                        process_code = target.process_code,
                        process_name = target.process_name,
                        machine_code = target.machine_code,
                        machine_found = false,
                        is_available = false,
                        total_quantity = 0,
                        busy_quantity = 0,
                        free_quantity = 0,
                        status = "Unmapped"
                    });

                    continue;
                }

                var totalQty = machine.quantity;
                var busyQty = machine.busy_quantity ?? 0;
                var freeQty = machine.free_quantity ?? (totalQty - busyQty);

                if (freeQty < 0)
                    freeQty = 0;

                var isAvailable = freeQty > 0;

                result.Add(new ProductionReadyMachineDto
                {
                    process_id = target.process_id,
                    seq_num = target.seq_num,
                    process_code = !string.IsNullOrWhiteSpace(target.process_code)
                        ? target.process_code
                        : machine.process_code,
                    process_name = !string.IsNullOrWhiteSpace(target.process_name)
                        ? target.process_name
                        : machine.process_name,

                    machine_code = machine.machine_code,
                    machine_found = true,

                    is_available = isAvailable,
                    total_quantity = totalQty,
                    busy_quantity = busyQty,
                    free_quantity = freeQty,

                    status = isAvailable ? "Available" : "Busy"
                });
            }

            return result
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.process_id ?? int.MaxValue)
                .ToList();
        }

        public async Task<GenerateImportReceiveBatchResponse?> GenerateImportReceiveAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var sources = await _repo.GetImportReceiveSourcesByOrderIdAsync(orderId, ct);

            if (sources == null || sources.Count == 0)
                return null;

            if (sources.Any(x => x.items == null || x.items.Count == 0))
                throw new InvalidOperationException("Đơn hàng không có item để tạo phiếu nhập kho.");

            var generatedFiles = new List<GenerateImportReceiveResponse>();

            var tempFolder = Path.Combine(Path.GetTempPath(), "amms-import-receives");
            Directory.CreateDirectory(tempFolder);

            foreach (var source in sources)
            {
                var safeOrderCode = string.IsNullOrWhiteSpace(source.order_code)
                    ? "NO_CODE"
                    : source.order_code.Trim();

                var fileName = $"phieu_nhap_kho_{safeOrderCode}_{source.prod_id}.pdf";

                var tempFilePath = Path.Combine(tempFolder, fileName);

                ImportReceivePdfHelper.Generate(tempFilePath, source);

                var publicId = $"import-receives/phieu_nhap_kho_{safeOrderCode}_{source.prod_id}";

                string cloudUrl;

                await using (var fs = new FileStream(
                    tempFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    cloudUrl = await _fileStorage.UploadRawWithPublicIdAsync(
                        fs,
                        fileName,
                        "application/pdf",
                        publicId);
                }

                var saved = await _repo.SaveImportReceivePathAsync(
                    source.prod_id,
                    cloudUrl,
                    ct);

                if (!saved)
                    throw new InvalidOperationException(
                        $"Không lưu được đường dẫn phiếu nhập kho vào production {source.prod_id}.");

                try
                {
                    if (File.Exists(tempFilePath))
                        File.Delete(tempFilePath);
                }
                catch
                {
                }

                generatedFiles.Add(new GenerateImportReceiveResponse
                {
                    success = true,
                    prod_id = source.prod_id,
                    order_id = source.order_id,
                    order_code = source.order_code,
                    import_recieve_path = cloudUrl,
                    message = $"Tạo phiếu nhập kho thành công cho production {source.prod_id}"
                });
            }

            var first = sources.FirstOrDefault();

            return new GenerateImportReceiveBatchResponse
            {
                success = true,
                order_id = orderId,
                order_code = first?.order_code,

                total_productions = sources.Count,
                generated_count = generatedFiles.Count,

                files = generatedFiles,

                message = $"Tạo thành công {generatedFiles.Count} phiếu nhập kho cho order {orderId}."
            };
        }

        public async Task<SetProductionMethodResponse?> SetProductionMethodAsync(
    SetProductionMethodRequest req,
    CancellationToken ct = default)
        {
            var response = await _repo.SetProductionMethodAsync(req, ct);

            if (response == null)
                return null;

            if (string.Equals(response.production_method, "SUB", StringComparison.OrdinalIgnoreCase) &&
                response.success &&
                response.prod_id > 0 &&
                string.IsNullOrWhiteSpace(response.sub_product_issue_file))
            {
                var issueFile = await CreateAndUploadSubProductIssueFileByProdIdAsync(
                    response.prod_id,
                    "Manager duyệt phương thức sản xuất SUB.",
                    ct);

                response.sub_product_issue_file = issueFile;

                if (!string.IsNullOrWhiteSpace(issueFile))
                {
                    response.message = "Đã duyệt sản xuất bằng bán thành phẩm và tạo phiếu xuất kho BTP.";
                }
            }

            return response;
        }


        private sealed class ValidSubProductOption
        {
            public sub_product Sub { get; init; } = null!;

            public SubProductStageSignature ExpectedSignature { get; init; } = null!;

            public int route_coverage_count { get; init; }

            public string reason { get; init; } = "";
        }

        private sealed class OrderSubProductMatchContext
        {
            public int order_id { get; init; }

            public int product_type_id { get; init; }

            public int? width { get; init; }

            public int? length { get; init; }

            public string? production_process { get; init; }

            public int order_qty { get; init; }
        }

        private sealed class MachineReadinessTarget
        {
            public int? process_id { get; init; }
            public int? seq_num { get; init; }
            public string? process_code { get; init; }
            public string? process_name { get; init; }
            public string? machine_code { get; init; }
        }

        private static string NormalizeMachineProcessCode(string? value)
            => (value ?? "").Trim().ToUpperInvariant();

        private static string NormalizeMachineText(string? value)
            => (value ?? "").Trim().ToUpperInvariant();

        private static HashSet<string> ParseSelectedMachineProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(',', ';', '|', '/', '\\')
                .Select(NormalizeMachineProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<MachineReadinessTarget> ResolveFixedMachineTargets(
            List<MachineReadinessTarget> allSteps,
            string? selectedProcessesCsv)
        {
            if (allSteps == null || allSteps.Count == 0)
                return new List<MachineReadinessTarget>();

            var selected = ParseSelectedMachineProcessCodes(selectedProcessesCsv);

            if (selected.Count == 0)
            {
                return allSteps
                    .OrderBy(x => x.seq_num ?? int.MaxValue)
                    .ThenBy(x => x.process_id ?? int.MaxValue)
                    .ToList();
            }

            var filtered = allSteps
                .Where(x => selected.Contains(NormalizeMachineProcessCode(x.process_code)))
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.process_id ?? int.MaxValue)
                .ToList();

            return filtered.Count > 0
                ? filtered
                : allSteps
                    .OrderBy(x => x.seq_num ?? int.MaxValue)
                    .ThenBy(x => x.process_id ?? int.MaxValue)
                    .ToList();
        }

        private static machine? ResolveMachineForTarget(
            MachineReadinessTarget target,
            List<machine> allMachines)
        {
            if (allMachines == null || allMachines.Count == 0)
                return null;

            var machineCode = target.machine_code?.Trim();

            // Ưu tiên 1: machine code được cấu hình trong task/product_type_process.
            if (!string.IsNullOrWhiteSpace(machineCode))
            {
                var byMachineCode = allMachines
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.machine_code?.Trim(),
                            machineCode,
                            StringComparison.OrdinalIgnoreCase));

                if (byMachineCode != null)
                    return byMachineCode;
            }

            var processCode = NormalizeMachineProcessCode(target.process_code);

            // Ưu tiên 2: tìm theo process_code.
            if (!string.IsNullOrWhiteSpace(processCode))
            {
                var byProcessCode = allMachines
                    .Where(x =>
                        string.Equals(
                            NormalizeMachineProcessCode(x.process_code),
                            processCode,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.free_quantity ?? (x.quantity - (x.busy_quantity ?? 0)))
                    .ThenBy(x => x.busy_quantity ?? 0)
                    .ThenByDescending(x => x.capacity_per_hour)
                    .ThenBy(x => x.machine_id)
                    .FirstOrDefault();

                if (byProcessCode != null)
                    return byProcessCode;
            }

            var processName = NormalizeMachineText(target.process_name);

            // Ưu tiên 3: tìm theo process_name.
            if (!string.IsNullOrWhiteSpace(processName))
            {
                var byProcessName = allMachines
                    .Where(x =>
                        string.Equals(
                            NormalizeMachineText(x.process_name),
                            processName,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.free_quantity ?? (x.quantity - (x.busy_quantity ?? 0)))
                    .ThenBy(x => x.busy_quantity ?? 0)
                    .ThenByDescending(x => x.capacity_per_hour)
                    .ThenBy(x => x.machine_id)
                    .FirstOrDefault();

                if (byProcessName != null)
                    return byProcessName;
            }

            return null;
        }

        public async Task<int?> ScheduleTasksAfterMethodAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            if (prod == null)
                return null;

            if (string.IsNullOrWhiteSpace(prod.prod_method))
                throw new InvalidOperationException("Production method has not been selected.");

            int? productTypeId = prod.product_type_id;

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
            {
                productTypeId = await _db.order_items
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderBy(x => x.item_id)
                    .Select(x => x.product_type_id)
                    .FirstOrDefaultAsync(ct);
            }

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
            {
                var req = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

                if (req != null && !string.IsNullOrWhiteSpace(req.product_type))
                {
                    productTypeId = await _db.product_types
                        .AsNoTracking()
                        .Where(x => x.code == req.product_type)
                        .Select(x => (int?)x.product_type_id)
                        .FirstOrDefaultAsync(ct);
                }
            }

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                throw new InvalidOperationException("Cannot resolve product_type_id for scheduling.");

            var productionProcessCsv = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => x.production_process)
                .FirstOrDefaultAsync(ct);

            return await _scheduling.ScheduleOrderAsync(
                orderId: orderId,
                productTypeId: productTypeId.Value,
                productionProcessCsv: productionProcessCsv,
                managerId: prod.manager_id ?? 3);
        }

        private async Task<sub_product> ResolveValidSubProductAsync(
    int subProductId,
    production prod,
    order_request orderReq,
    int orderQty,
    bool requireEnoughQty,
    CancellationToken ct)
        {
            if (subProductId <= 0)
                throw new InvalidOperationException("sub_id không hợp lệ.");

            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                throw new InvalidOperationException("Production chưa gắn với order.");

            var orderId = prod.order_id.Value;

            var ctx = await LoadOrderSubProductMatchContextAsync(orderId, ct);
            if (ctx == null)
                throw new InvalidOperationException("Không xác định được thông tin order để check bán thành phẩm.");

            orderQty = orderQty > 0 ? orderQty : ctx.order_qty;

            var sub = await _db.sub_products
                .Include(x => x.product_type)
                .FirstOrDefaultAsync(x => x.id == subProductId, ct);

            if (sub == null)
                throw new InvalidOperationException($"Không tìm thấy bán thành phẩm sub_id={subProductId}.");

            if (sub.is_active != true || sub.is_imported != true)
                throw new InvalidOperationException("Bán thành phẩm chưa active hoặc chưa được nhập kho.");

            if (sub.quantity <= 0)
                throw new InvalidOperationException("Bán thành phẩm không còn tồn kho.");

            if (requireEnoughQty && sub.quantity < orderQty)
            {
                throw new InvalidOperationException(
                    $"Không đủ tồn bán thành phẩm. SubProductId={sub.id}, tồn={sub.quantity}, cần={orderQty}.");
            }

            if (sub.product_type_id != ctx.product_type_id)
            {
                throw new InvalidOperationException(
                    $"Bán thành phẩm khác product_type. Sub={sub.product_type_id}, Order={ctx.product_type_id}.");
            }

            if (sub.width != ctx.width || sub.length != ctx.length)
            {
                throw new InvalidOperationException(
                    $"Bán thành phẩm khác kích thước. " +
                    $"Sub=({sub.width}x{sub.length}), Order=({ctx.width}x{ctx.length}).");
            }

            if (!SubProductCompatibilityHelper.IsSubPathUsableForOrderRoute(
                    sub.product_process,
                    ctx.production_process))
            {
                throw new InvalidOperationException(
                    $"Path bán thành phẩm không phù hợp với route đơn hàng. " +
                    $"SubPath={sub.product_process}, OrderRoute={ctx.production_process}.");
            }

            var expected = await SubProductCompatibilityHelper.BuildSignatureForOrderStageAsync(
                _db,
                orderId,
                sub.product_process,
                orderQty,
                ct);

            if (expected == null)
                throw new InvalidOperationException("Không build được NVL signature kỳ vọng cho đơn hàng.");

            if (!SubProductCompatibilityHelper.IsMaterialMatchedForProductionSub(sub, expected))
            {
                var subStages = SubProductCompatibilityHelper.ParseRoute(sub.product_process);

                var requiredChecks = new List<string> { "giấy" };

                if (subStages.Contains("PHU", StringComparer.OrdinalIgnoreCase))
                    requiredChecks.Add("keo phủ");

                if (subStages.Contains("CAN", StringComparer.OrdinalIgnoreCase) ||
                    subStages.Contains("CAN_MANG", StringComparer.OrdinalIgnoreCase))
                {
                    requiredChecks.Add("màng cán");
                }

                if (subStages.Contains("BOI", StringComparer.OrdinalIgnoreCase))
                    requiredChecks.Add("sóng");

                throw new InvalidOperationException(
                    "Bán thành phẩm không hợp lệ vì khác NVL theo stage đã cover. " +
                    $"Các điều kiện cần check: {string.Join(", ", requiredChecks)}. " +
                    $"SubPath={sub.product_process}, " +
                    $"SubPaper={sub.paper_material_code}, ExpectedPaper={expected.paper_material_code}, " +
                    $"SubCoating={sub.coating_material_code}, ExpectedCoating={expected.coating_material_code}, " +
                    $"SubLamination={sub.lamination_material_code}, ExpectedLamination={expected.lamination_material_code}, " +
                    $"SubWave={sub.wave_material_code}, ExpectedWave={expected.wave_material_code}.");
            }

            return sub;
        }

        private async Task RollbackSubProductFinishedTasksAsync(
            int prodId,
            CancellationToken ct)
        {
            /*
             * Rollback các task đã được tự Finished do dùng bán thành phẩm.
             * Chỉ rollback task có is_taken_sub_product = true,
             * không đụng task do nhân sự scan thật.
             */
            var tasks = await _db.tasks
                .Where(x =>
                    x.prod_id == prodId &&
                    x.is_taken_sub_product == true)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return;

            var taskIds = tasks
                .Select(x => x.task_id)
                .ToList();

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

        private async Task ApplySubProductToExistingTasksAsync(
            production prod,
            sub_product selectedSubProduct,
            int orderQty,
            CancellationToken ct)
        {
            /*
             * SUB:
             * Nếu production đã có task shell rồi,
             * các công đoạn đã được bán thành phẩm cover sẽ được tự Finished.
             *
             * Ví dụ sub_product.product_process = "RALO,CAT,IN"
             * thì các task RALO/CAT/IN trong production sẽ Finished tự động.
             */
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

            /*
             * Tìm công đoạn cuối cùng đã được bán thành phẩm cover.
             */
            var maxCompletedSeq = tasks
                .Where(x => selectedCodes.Contains(
                    NormProcessCodeForSubProduct(x.process?.process_code)))
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

            var qtyCtx = await GetProductionQtyContextAsync(
                prod.order_id.Value,
                ct);

            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];

                if (!t.seq_num.HasValue)
                    continue;

                if (t.seq_num.Value > maxCompletedSeq.Value)
                    continue;

                if (string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    continue;

                var processCode = t.process?.process_code;

                var qtyGood = ResolveQtyGoodForSubProductTask(
                    processCode,
                    i,
                    routeCodes,
                    qtyCtx);

                if (qtyGood <= 0)
                    qtyGood = orderQty;

                t.status = "Finished";
                t.start_time ??= now;
                t.end_time = now;
                t.reason = reason;
                t.is_taken_sub_product = true;

                await _db.task_logs.AddAsync(new task_log
                {
                    task_id = t.task_id,
                    scanned_code = $"SUB_PRODUCT-{selectedSubProduct.id}-TASK-{t.task_id}",
                    action_type = "Finished",
                    qty_good = qtyGood,
                    log_time = now,
                    scanned_by_user_id = null,
                    reason = reason,
                    material_usage_json = null,
                    reference_input_json = null,
                    output_json = null,
                    report_image_url = null
                }, ct);
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

        private static string? NormalizeNote(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            return value.Length <= 1000
                ? value
                : value.Substring(0, 1000);
        }

        private sealed class ProductionMaterialReserveItem
        {
            public int material_id { get; init; }

            public string material_code { get; init; } = "";

            public string material_name { get; init; } = "";

            public string unit { get; init; } = "";

            public decimal qty { get; init; }
        }

        private static string NormalizeMaterialCodeForReserve(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var s = raw.Trim().ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Z0-9]+", "_");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

            return s switch
            {
                "KEM_THO" => "PLATE",
                "BAN_KEM_THO" => "PLATE",
                "BAN_KEM" => "PLATE",
                "BAN_KEM_IN" => "PLATE",
                "PLATE_INPUT" => "PLATE",

                "MUC" => "INK",
                "MUC_IN" => "INK",
                "MUC_TONG_HOP" => "INK",
                "INK_TYPES" => "INK",

                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                "MANG_12_MIC" => "MANG_12MIC",

                "MOUNTING_GLUE" => "KEO_BOI",
                "KEO_BOI" => "KEO_BOI",

                _ => s
            };
        }

        private async Task<material?> ResolveMaterialForReserveAsync(
            IEnumerable<string?> codeCandidates,
            IEnumerable<string?>? nameCandidates,
            CancellationToken ct)
        {
            var aliases = new List<string>();

            aliases.AddRange(
                codeCandidates
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(NormalizeMaterialCodeForReserve));

            if (nameCandidates != null)
            {
                aliases.AddRange(
                    nameCandidates
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(NormalizeMaterialCodeForReserve));
            }

            aliases = aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            var allMaterials = await _db.materials
                .ToListAsync(ct);

            foreach (var alias in aliases)
            {
                var matched = allMaterials.FirstOrDefault(m =>
                    string.Equals(NormalizeMaterialCodeForReserve(m.code), alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeMaterialCodeForReserve(m.name), alias, StringComparison.OrdinalIgnoreCase));

                if (matched != null)
                    return matched;
            }

            return null;
        }

        private static List<string> ParseProductionProcessCodesForReserve(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToUpperInvariant().Replace(" ", "_").Replace("-", "_"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasProcessForReserve(IReadOnlyCollection<string> routeCodes, string code)
        {
            return routeCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
        }

        private static decimal ScaleQtyForBothReserve(
            decimal qty,
            string processCode,
            IReadOnlyList<string> routeCodes,
            HashSet<string> subProcessCodes,
            decimal nvlRatio)
        {
            if (qty <= 0)
                return 0m;

            if (nvlRatio <= 0m)
                return 0m;

            if (nvlRatio >= 1m)
                return qty;

            var currentCode = (processCode ?? "").Trim().ToUpperInvariant();

            var subLastIndex = -1;

            for (var i = 0; i < routeCodes.Count; i++)
            {
                if (subProcessCodes.Contains(routeCodes[i]))
                    subLastIndex = i;
            }

            if (subLastIndex < 0)
                return qty;

            var currentIndex = routeCodes
                .Select((x, idx) => new { code = x, idx })
                .FirstOrDefault(x => string.Equals(x.code, currentCode, StringComparison.OrdinalIgnoreCase))
                ?.idx ?? -1;

            if (currentIndex < 0)
                return qty;

            var isCoveredBySub = currentIndex <= subLastIndex;

            if (!isCoveredBySub)
                return qty;

            /*
             * Giữ tương thích logic cũ:
             * RALO không scale, các công đoạn đầu khác scale theo phần NVL thiếu.
             */
            if (currentCode == "RALO")
                return qty;

            return Math.Round(qty * nvlRatio, 4);
        }

        private async Task<List<ProductionMaterialReserveItem>> BuildProductionMaterialReserveItemsAsync(
            order_request req,
            cost_estimate est,
            string productionMethod,
            int orderQty,
            int nvlQty,
            int? subProductId,
            string? productionProcessCsv,
            CancellationToken ct)
        {
            var method = (productionMethod ?? "").Trim().ToUpperInvariant();
            var routeCodes = ParseProductionProcessCodesForReserve(productionProcessCsv);

            if (routeCodes.Count == 0)
                routeCodes = new List<string> { "RALO", "CAT", "IN", "PHU", "CAN", "BOI", "BE", "DUT", "DAN" };

            var result = new List<ProductionMaterialReserveItem>();

            HashSet<string> subCodes = new(StringComparer.OrdinalIgnoreCase);
            decimal nvlRatio = 1m;

            if (method == "SUB")
                return result;

            if (method == "BOTH")
            {
                if (orderQty <= 0)
                    throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ khi reserve BOTH.");

                if (nvlQty <= 0)
                    throw new InvalidOperationException("nvl_qty không hợp lệ khi reserve BOTH.");

                nvlRatio = Math.Clamp(nvlQty / (decimal)orderQty, 0m, 1m);

                if (subProductId.HasValue && subProductId.Value > 0)
                {
                    var subProcess = await _db.sub_products
                        .AsNoTracking()
                        .Where(x => x.id == subProductId.Value)
                        .Select(x => x.product_process)
                        .FirstOrDefaultAsync(ct);

                    subCodes = ParseProductionProcessCodesForReserve(subProcess)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }

            async Task AddReserveAsync(
                string processCode,
                decimal qty,
                IEnumerable<string?> codes,
                IEnumerable<string?>? names,
                string fallbackUnit)
            {
                if (qty <= 0)
                    return;

                if (method == "BOTH")
                {
                    qty = ScaleQtyForBothReserve(
                        qty,
                        processCode,
                        routeCodes,
                        subCodes,
                        nvlRatio);
                }

                if (qty <= 0)
                    return;

                var mat = await ResolveMaterialForReserveAsync(
                    codes,
                    names,
                    ct);

                if (mat == null)
                {
                    var showCode = string.Join("/", codes.Where(x => !string.IsNullOrWhiteSpace(x)));
                    var showName = names == null
                        ? ""
                        : string.Join("/", names.Where(x => !string.IsNullOrWhiteSpace(x)));

                    throw new InvalidOperationException(
                        $"Không tìm thấy NVL để reserve. Process={processCode}, Code={showCode}, Name={showName}");
                }

                result.Add(new ProductionMaterialReserveItem
                {
                    material_id = mat.material_id,
                    material_code = mat.code ?? "",
                    material_name = mat.name ?? "",
                    unit = mat.unit ?? fallbackUnit,
                    qty = Math.Round(qty, 4)
                });
            }

            if (HasProcessForReserve(routeCodes, "RALO"))
            {
                await AddReserveAsync(
                    processCode: "RALO",
                    qty: Math.Max(req.number_of_plates ?? 0, 1),
                    codes: new[] { "PLATE", "PLATE_INPUT" },
                    names: new[] { "Kẽm thô", "Bản kẽm thô" },
                    fallbackUnit: "bản");
            }

            if (HasProcessForReserve(routeCodes, "CAT"))
            {
                await AddReserveAsync(
                    processCode: "CAT",
                    qty: est.sheets_total > 0 ? est.sheets_total : est.sheets_required,
                    codes: new[] { est.paper_code, est.paper_alternative, "PAPER" },
                    names: new[] { est.paper_name, est.paper_alternative },
                    fallbackUnit: "tờ");
            }

            if (HasProcessForReserve(routeCodes, "IN"))
            {
                await AddReserveAsync(
                    processCode: "IN",
                    qty: est.sheets_total > 0 ? est.sheets_total : est.sheets_required,
                    codes: new[] { est.paper_code, est.paper_alternative, "PAPER" },
                    names: new[] { est.paper_name, est.paper_alternative },
                    fallbackUnit: "tờ");

                await AddReserveAsync(
                    processCode: "IN",
                    qty: est.ink_weight_kg,
                    codes: new[] { "INK", "INK_TYPES" },
                    names: new[] { "Mực tổng hợp", "Mực in" },
                    fallbackUnit: "kg");
            }

            if (HasProcessForReserve(routeCodes, "PHU"))
            {
                await AddReserveAsync(
                    processCode: "PHU",
                    qty: est.coating_glue_weight_kg,
                    codes: new[] { est.coating_type },
                    names: new[] { ProductionFlowHelper.ResolveCoatingDisplayName(est.coating_type) },
                    fallbackUnit: "kg");
            }

            if (HasProcessForReserve(routeCodes, "CAN") || HasProcessForReserve(routeCodes, "CAN_MANG"))
            {
                await AddReserveAsync(
                    processCode: "CAN",
                    qty: est.lamination_weight_kg,
                    codes: new[] { est.lamination_material_code },
                    names: new[] { est.lamination_material_name },
                    fallbackUnit: "kg");
            }

            if (HasProcessForReserve(routeCodes, "BOI"))
            {
                await AddReserveAsync(
                    processCode: "BOI",
                    qty: est.wave_sheets_used ?? est.wave_sheets_required ?? 0,
                    codes: new[] { est.wave_type, est.wave_alternative, "WAVE" },
                    names: new[] { est.wave_type, est.wave_alternative, "Sóng carton" },
                    fallbackUnit: "tờ");

                await AddReserveAsync(
                    processCode: "BOI",
                    qty: est.mounting_glue_weight_kg,
                    codes: new[] { "KEO_BOI", "MOUNTING_GLUE" },
                    names: new[] { "Keo bồi" },
                    fallbackUnit: "kg");
            }

            return result
                .GroupBy(x => x.material_id)
                .Select(g =>
                {
                    var first = g.First();

                    return new ProductionMaterialReserveItem
                    {
                        material_id = first.material_id,
                        material_code = first.material_code,
                        material_name = first.material_name,
                        unit = first.unit,
                        qty = Math.Round(g.Sum(x => x.qty), 4)
                    };
                })
                .Where(x => x.qty > 0)
                .ToList();
        }

        private async Task<OrderSubProductMatchContext?> LoadOrderSubProductMatchContextAsync(
    int orderId,
    CancellationToken ct)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.product_type_id,
                    x.production_process,
                    x.quantity
                })
                .FirstOrDefaultAsync(ct);

            var productTypeId = prod?.product_type_id ?? item?.product_type_id;

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                return null;

            var orderQty = req?.quantity ?? 0;

            if (orderQty <= 0)
                orderQty = item?.quantity ?? 0;

            if (orderQty <= 0)
                orderQty = 1;

            return new OrderSubProductMatchContext
            {
                order_id = orderId,
                product_type_id = productTypeId.Value,

                /*
                 * Code hiện tại của bạn đang match sub_product.width/length
                 * theo req.print_width_mm / req.print_length_mm.
                 */
                width = req?.print_width_mm,
                length = req?.print_length_mm,

                production_process = item?.production_process,
                order_qty = orderQty
            };
        }

        private async Task<List<ValidSubProductOption>> FindValidSubProductOptionsAsync(
    int orderId,
    int orderQty,
    bool requireEnoughQty,
    CancellationToken ct)
        {
            var ctx = await LoadOrderSubProductMatchContextAsync(orderId, ct);
            if (ctx == null)
                return new List<ValidSubProductOption>();

            orderQty = orderQty > 0 ? orderQty : ctx.order_qty;

            /*
             * Lọc trước bằng những điều kiện chắc chắn dịch được xuống SQL:
             * - sub_product đã active
             * - đã imported
             * - còn quantity
             * - cùng product_type
             * - cùng width/length theo order_request.print_width_mm / print_length_mm
             *
             * Không check NVL ở query này vì logic NVL phụ thuộc stage của sub_product.
             */
            var candidates = await _db.sub_products
                .AsNoTracking()
                .Include(x => x.product_type)
                .Where(x =>
                    x.is_active == true &&
                    x.is_imported == true &&
                    x.quantity > 0 &&
                    x.product_type_id == ctx.product_type_id &&
                    x.width == ctx.width &&
                    x.length == ctx.length)
                .OrderByDescending(x => x.quantity)
                .ThenByDescending(x => x.updated_at)
                .ThenByDescending(x => x.id)
                .ToListAsync(ct);

            var result = new List<ValidSubProductOption>();

            foreach (var sub in candidates)
            {
                /*
                 * Nếu đang xét method SUB thuần thì requireEnoughQty = true:
                 * sub phải đủ toàn bộ orderQty.
                 *
                 * Nếu đang xét để hiển thị option/BOTH thì requireEnoughQty = false:
                 * sub chỉ cần có quantity > 0.
                 */
                if (requireEnoughQty && sub.quantity < orderQty)
                    continue;

                /*
                 * Check path:
                 * Ví dụ order route = RALO,CAT,IN,PHU,CAN,BOI,BE,DUT,DAN
                 * sub path = RALO,CAT,IN => hợp lệ.
                 * sub path = RALO,IN nếu order route không có CAT cũng có thể hợp lệ,
                 * tùy logic IsSubPathUsableForOrderRoute hiện tại của bạn.
                 */
                if (!SubProductCompatibilityHelper.IsSubPathUsableForOrderRoute(
                        sub.product_process,
                        ctx.production_process))
                {
                    continue;
                }

                /*
                 * Build expected material signature theo stage mà sub đã cover.
                 * Nhưng không dùng để check full signature nữa.
                 * Hàm IsMaterialMatchedForProductionSub sẽ chỉ check:
                 * - luôn check giấy
                 * - nếu sub có PHU thì check coating
                 * - nếu sub có CAN/CAN_MANG thì check lamination
                 * - nếu sub có BOI thì check wave
                 */
                var expected = await SubProductCompatibilityHelper.BuildSignatureForOrderStageAsync(
                    _db,
                    orderId,
                    sub.product_process,
                    orderQty,
                    ct);

                if (expected == null)
                    continue;

                /*
                 * không cần check unit_cost_to_stage,
                 * không check full NVL cho những stage mà sub chưa cover.
                 */
                if (!SubProductCompatibilityHelper.IsMaterialMatchedForProductionSub(
                        sub,
                        expected))
                {
                    continue;
                }

                var subStages = SubProductCompatibilityHelper.ParseRoute(sub.product_process);

                var checkedRules = new List<string>
                {
                    "product_type",
                    "kích thước",
                    "path",
                    "giấy"
                };

                if (subStages.Contains("PHU", StringComparer.OrdinalIgnoreCase))
                    checkedRules.Add("keo phủ");

                if (subStages.Contains("CAN", StringComparer.OrdinalIgnoreCase) ||
                    subStages.Contains("CAN_MANG", StringComparer.OrdinalIgnoreCase))
                {
                    checkedRules.Add("màng cán");
                }

                if (subStages.Contains("BOI", StringComparer.OrdinalIgnoreCase))
                    checkedRules.Add("sóng");

                result.Add(new ValidSubProductOption
                {
                    Sub = sub,
                    ExpectedSignature = expected,
                    route_coverage_count = subStages.Count,
                    reason = $"Sub product hợp lệ: cùng {string.Join(", ", checkedRules)}."
                });
            }

            return result
                .OrderByDescending(x => x.route_coverage_count)
                .ThenByDescending(x => x.Sub.quantity)
                .ThenByDescending(x => x.Sub.updated_at)
                .ThenByDescending(x => x.Sub.id)
                .ToList();
        }

        private static MatchedSubProductDto ToMatchedSubProductDto(
            sub_product sub,
            string? reason = null)
        {
            return new MatchedSubProductDto
            {
                id = sub.id,
                product_type_id = sub.product_type_id,
                product_type_name = sub.product_type?.name,
                width = sub.width,
                length = sub.length,
                product_process = sub.product_process,
                quantity = sub.quantity,
                is_active = sub.is_active,
                is_imported = sub.is_imported,
                import_file = sub.import_file,

                paper_material_code = sub.paper_material_code,
                wave_material_code = sub.wave_material_code,
                coating_material_code = sub.coating_material_code,
                lamination_material_code = sub.lamination_material_code,
                material_signature = sub.material_signature,

                cost_estimate_id = sub.cost_estimate_id,
                unit_cost_to_stage = sub.unit_cost_to_stage,
                total_cost_to_stage = sub.total_cost_to_stage,

                description = sub.description,
                updated_at = sub.updated_at,
                match_reason = reason
            };
        }

        private async Task<List<ProductionMethodCostOptionDto>> BuildMethodCostOptionsAsync(
    int orderId,
    int orderQty,
    string? orderRouteCsv,
    ValidSubProductOption? subOptionForSub,
    ValidSubProductOption? subOptionForBoth,
    CancellationToken ct)
        {
            var result = new List<ProductionMethodCostOptionDto>();

            var est = await SubProductCompatibilityHelper.LoadAcceptedEstimateByOrderIdAsync(
                _db,
                orderId,
                ct);

            if (est == null)
            {
                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "NVL",
                    is_available = false,
                    reason = "Không tìm thấy cost_estimate đã chốt để tính chi phí."
                });

                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "SUB",
                    is_available = false,
                    reason = "Không tìm thấy cost_estimate đã chốt để tính chi phí."
                });

                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "BOTH",
                    is_available = false,
                    reason = "Không tìm thấy cost_estimate đã chốt để tính chi phí."
                });

                return result;
            }

            orderQty = orderQty > 0 ? orderQty : 1;

            var orderRoute = SubProductCompatibilityHelper.ParseRoute(
                !string.IsNullOrWhiteSpace(orderRouteCsv)
                    ? orderRouteCsv
                    : est.production_processes);

            var processCosts = await _db.cost_estimate_processes
                .AsNoTracking()
                .Where(x => x.estimate_id == est.estimate_id)
                .Select(x => new EstimateProcessCostRow
                {
                    process_code = x.process_code,
                    total_cost = x.total_cost
                })
                .ToListAsync(ct);

            /*
             * NVL:
             * Theo yêu cầu: lấy thẳng final_total_cost từ báo giá đã chốt.
             */
            var nvlTotalCost = Money(est.final_total_cost);

            if (nvlTotalCost <= 0m)
            {
                /*
                 * Fallback cho dữ liệu cũ chưa có final_total_cost.
                 * Fallback này cũng không cộng overhead/rush/discount.
                 */
                var nvlFallback = BuildMethodCostWithoutOverheadRushDiscount(
                    est,
                    materialCost: GetFullNvlMaterialCost(est),
                    processCost: GetFullProcessCost(processCosts),
                    orderQty: orderQty);

                nvlTotalCost = nvlFallback.total_cost;
            }

            var nvlUnitCost = SafeUnitCost(nvlTotalCost, orderQty);

            result.Add(new ProductionMethodCostOptionDto
            {
                method = "NVL",
                is_available = true,
                unit_cost = nvlUnitCost,
                total_cost = nvlTotalCost,
                nvl_qty = orderQty,
                reason = "Sản xuất toàn bộ từ NVL. Total cost lấy từ final_total_cost của báo giá đã chốt."
            });

            /*
             * SUB:
             * Chỉ available nếu có BTP phù hợp và đủ quantity.
             */
            if (subOptionForSub == null)
            {
                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "SUB",
                    is_available = false,
                    reason = "Không có bán thành phẩm phù hợp và đủ số lượng để dùng SUB."
                });
            }
            else
            {
                var sub = subOptionForSub.Sub;

                var subCost = BuildSubOrBothMethodCostWithoutOverheadRushDiscount(
                    est,
                    processCosts,
                    orderRoute,
                    sub,
                    subUsedQty: orderQty,
                    nvlQty: 0,
                    orderQty: orderQty);

                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "SUB",
                    is_available = true,

                    sub_product_id = sub.id,
                    sub_available_qty = sub.quantity,
                    sub_used_qty = orderQty,
                    nvl_qty = 0,

                    unit_cost = subCost.unit_cost,
                    total_cost = subCost.total_cost,

                    saving_vs_nvl_unit = Math.Round(nvlUnitCost - subCost.unit_cost, 4),
                    saving_vs_nvl_total = Math.Round(nvlTotalCost - subCost.total_cost, 2),

                    reason =
                        $"SUB = giá trị BTP đã có + vật liệu/công đoạn còn thiếu + design_cost. " +
                        $"Không tính overhead, rush, discount. " +
                        $"SubPath={sub.product_process}. " +
                        $"CoveredStages={string.Join(",", subCost.covered_stages)}. " +
                        $"ExtraStages={string.Join(",", subCost.extra_stages)}."
                });
            }

            /*
             * BOTH:
             * Dùng một phần BTP, phần thiếu sản xuất bằng NVL.
             * Cũng không cộng overhead/rush/discount trong cost đề xuất.
             */
            if (subOptionForBoth == null)
            {
                result.Add(new ProductionMethodCostOptionDto
                {
                    method = "BOTH",
                    is_available = false,
                    reason = "Không có bán thành phẩm phù hợp nhưng thiếu số lượng để kết hợp với NVL."
                });
            }
            else
            {
                var sub = subOptionForBoth.Sub;

                var subUseQty = Math.Min(sub.quantity, orderQty);
                var nvlQty = orderQty - subUseQty;

                if (subUseQty <= 0 || nvlQty <= 0)
                {
                    result.Add(new ProductionMethodCostOptionDto
                    {
                        method = "BOTH",
                        is_available = false,

                        sub_product_id = sub.id,
                        sub_available_qty = sub.quantity,

                        reason = nvlQty <= 0
                            ? "BTP đã đủ số lượng, nên chọn SUB thay vì BOTH."
                            : "BTP không có số lượng để kết hợp."
                    });
                }
                else
                {
                    var bothCost = BuildSubOrBothMethodCostWithoutOverheadRushDiscount(
                        est,
                        processCosts,
                        orderRoute,
                        sub,
                        subUsedQty: subUseQty,
                        nvlQty: nvlQty,
                        orderQty: orderQty);

                    result.Add(new ProductionMethodCostOptionDto
                    {
                        method = "BOTH",
                        is_available = true,

                        sub_product_id = sub.id,
                        sub_available_qty = sub.quantity,
                        sub_used_qty = subUseQty,
                        nvl_qty = nvlQty,

                        unit_cost = bothCost.unit_cost,
                        total_cost = bothCost.total_cost,

                        saving_vs_nvl_unit = Math.Round(nvlUnitCost - bothCost.unit_cost, 4),
                        saving_vs_nvl_total = Math.Round(nvlTotalCost - bothCost.total_cost, 2),

                        reason =
                            $"BOTH dùng {subUseQty} BTP và sản xuất thêm {nvlQty} bằng NVL. " +
                            $"Không tính overhead, rush, discount trong cost đề xuất. " +
                            $"SubPath={sub.product_process}. " +
                            $"CoveredStages={string.Join(",", bothCost.covered_stages)}. " +
                            $"ExtraStagesForSubPart={string.Join(",", bothCost.extra_stages)}."
                    });
                }
            }

            return result;
        }

        private async Task ReleasePreviousProductionMaterialReserveAsync(
            int prodId,
            CancellationToken ct)
        {
            var refPrefix = $"PROD-RESERVE-{prodId}-";

            var oldMoves = await _db.stock_moves
                .Where(x =>
                    x.type == "OUT" &&
                    x.ref_doc != null &&
                    x.ref_doc.StartsWith(refPrefix))
                .ToListAsync(ct);

            if (oldMoves.Count == 0)
                return;

            var materialIds = oldMoves
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            var now = AppTime.NowVnUnspecified();

            foreach (var move in oldMoves)
            {
                if (!materials.TryGetValue((int)move.material_id, out var mat))
                    continue;

                mat.stock_qty = (mat.stock_qty ?? 0m) + move.qty;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = move.material_id,
                    type = "IN",
                    qty = move.qty,
                    ref_doc = $"PROD-RESERVE-ROLLBACK-{prodId}-{move.move_id}",
                    user_id = null,
                    move_date = now,
                    note = $"Rollback reserve NVL cho production {prodId}. Ref old move={move.ref_doc}"
                }, ct);
            }
        }

        private async Task ReserveMaterialsForConfirmedProductionMethodAsync(
            production prod,
            order_request req,
            cost_estimate est,
            string method,
            int orderQty,
            CancellationToken ct)
        {
            if (prod.prod_id <= 0)
                throw new InvalidOperationException("Production chưa có prod_id.");

            var normalizedMethod = (method ?? "").Trim().ToUpperInvariant();

            if (normalizedMethod is not ("NVL" or "SUB" or "BOTH"))
                throw new InvalidOperationException($"Method sản xuất không hợp lệ: {method}");

            /*
             * Tránh reserve trùng khi manager đổi method hoặc confirm lại.
             */
            await ReleasePreviousProductionMaterialReserveAsync(prod.prod_id, ct);

            if (normalizedMethod == "SUB")
                return;

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == prod.order_id)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.production_process
                })
                .FirstOrDefaultAsync(ct);

            var reserveItems = await BuildProductionMaterialReserveItemsAsync(
                req,
                est,
                normalizedMethod,
                orderQty,
                prod.nvl_qty,
                prod.sub_product_id,
                item?.production_process,
                ct);

            if (reserveItems.Count == 0)
                return;

            var materialIds = reserveItems
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            foreach (var itemReserve in reserveItems)
            {
                if (!materials.TryGetValue(itemReserve.material_id, out var mat))
                {
                    throw new InvalidOperationException(
                        $"Không tìm thấy NVL material_id={itemReserve.material_id} để reserve.");
                }

                var currentStock = mat.stock_qty ?? 0m;

                if (currentStock < itemReserve.qty)
                {
                    throw new InvalidOperationException(
                        $"Không đủ tồn kho NVL {mat.code} - {mat.name}. " +
                        $"Tồn={currentStock}, cần reserve={itemReserve.qty} {mat.unit}.");
                }
            }

            var now = AppTime.NowVnUnspecified();

            foreach (var itemReserve in reserveItems)
            {
                var mat = materials[itemReserve.material_id];

                mat.stock_qty = (mat.stock_qty ?? 0m) - itemReserve.qty;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = itemReserve.material_id,
                    type = "OUT",
                    qty = itemReserve.qty,
                    ref_doc = $"PROD-RESERVE-{prod.prod_id}-{normalizedMethod}",
                    user_id = null,
                    move_date = now,
                    note = $"Reserve NVL khi confirm method {normalizedMethod}. order_id={prod.order_id}, prod_id={prod.prod_id}"
                }, ct);
            }
        }

        public async Task<ForceProductionImportingResponseDto?> ForceSetProductionImportingByProdIdAsync(
    int prodId,
    CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var prod = await _db.productions
                    .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

                if (prod == null)
                    return null;

                if (string.Equals(prod.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prod.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prod.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Không thể chuyển Importing vì production đang ở trạng thái {prod.status}.");
                }

                var tasks = await _db.tasks
                    .Where(x => x.prod_id == prodId)
                    .Select(x => new
                    {
                        x.status,
                        x.start_time,
                        x.end_time
                    })
                    .ToListAsync(ct);

                if (tasks.Count == 0)
                    throw new InvalidOperationException("Production chưa có task.");

                var allFinished = tasks.All(x =>
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    x.end_time != null);

                if (!allFinished)
                {
                    throw new InvalidOperationException(
                        "Chỉ được chuyển Importing khi tất cả task của production đã Finished.");
                }

                var now = AppTime.NowVnUnspecified();

                var finishedAt = tasks
                    .Where(x => x.end_time != null)
                    .Select(x => x.end_time!.Value)
                    .DefaultIfEmpty(now)
                    .Max();

                prod.status = "Importing";
                prod.end_date ??= finishedAt;
                prod.actual_start_date ??= tasks
                    .Where(x => x.start_time != null)
                    .Select(x => x.start_time!.Value)
                    .DefaultIfEmpty(finishedAt)
                    .Min();

                prod.created_at ??= now;
                prod.planned_start_date ??= now;

                var orderIds = new List<int>();

                var isGroup = string.Equals(
                    prod.prod_kind,
                    "GROUP",
                    StringComparison.OrdinalIgnoreCase);

                if (isGroup)
                {
                    orderIds = await _db.prod_orders
                        .AsNoTracking()
                        .Where(x =>
                            x.prod_id == prod.prod_id &&
                            x.status == "Active")
                        .Select(x => x.order_id)
                        .Distinct()
                        .ToListAsync(ct);
                }
                else if (prod.order_id.HasValue)
                {
                    orderIds.Add(prod.order_id.Value);
                }

                orderIds = orderIds
                    .Where(x => x > 0)
                    .Distinct()
                    .ToList();

                var updatedOrderCount = 0;
                var updatedRequestCount = 0;

                if (orderIds.Count > 0)
                {
                    var orders = await _db.orders
                        .Where(x => orderIds.Contains(x.order_id))
                        .ToListAsync(ct);

                    foreach (var order in orders)
                    {
                        if (string.Equals(order.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(order.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(order.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        order.status = "Importing";
                        updatedOrderCount++;
                    }

                    var requests = await _db.order_requests
                        .Where(x =>
                            x.order_id.HasValue &&
                            orderIds.Contains(x.order_id.Value))
                        .ToListAsync(ct);

                    foreach (var req in requests)
                    {
                        if (string.Equals(req.process_status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(req.process_status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(req.process_status, "Cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        req.process_status = "Importing";
                        updatedRequestCount++;
                    }
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return new ForceProductionImportingResponseDto
                {
                    success = true,
                    prod_id = prod.prod_id,
                    production_code = prod.code,
                    production_status = "Importing",
                    order_ids = orderIds,
                    updated_order_count = updatedOrderCount,
                    updated_request_count = updatedRequestCount,
                    importing_at = finishedAt,
                    message = "Đã hoàn thành lệnh sản xuất."
                };
            });
        }

        private async Task<string?> CreateAndUploadSubProductIssueFileByProdIdAsync(
    int prodId,
    string reason,
    CancellationToken ct)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null)
                throw new InvalidOperationException("Production not found.");

            if (!string.Equals(prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase))
                return prod.sub_product_issue_file;

            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                throw new InvalidOperationException("Production SUB chưa gắn với order.");

            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                throw new InvalidOperationException("Production SUB chưa gắn với sub_product.");

            if (prod.sub_product_used_qty <= 0)
                throw new InvalidOperationException("Production SUB chưa có số lượng BTP đã xuất.");

            var ord = await _db.orders
                .FirstOrDefaultAsync(x => x.order_id == prod.order_id.Value, ct);

            if (ord == null)
                throw new InvalidOperationException("Order not found.");

            var orderReq = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == ord.order_id)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var selectedSubProduct = await _db.sub_products
                .FirstOrDefaultAsync(x => x.id == prod.sub_product_id.Value, ct);

            if (selectedSubProduct == null)
                throw new InvalidOperationException("Sub product not found.");

            return await CreateAndUploadSubProductIssueFileAsync(
                prod,
                ord,
                orderReq,
                selectedSubProduct,
                prod.sub_product_used_qty,
                reason,
                ct);
        }

        private async Task<string?> CreateAndUploadSubProductIssueFileAsync(
            production prod,
            order ord,
            order_request? orderReq,
            sub_product selectedSubProduct,
            int quantityIssued,
            string reason,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(prod.sub_product_issue_file))
                return prod.sub_product_issue_file;

            if (quantityIssued <= 0)
                throw new InvalidOperationException("Số lượng xuất bán thành phẩm phải lớn hơn 0.");

            var now = AppTime.NowVnUnspecified();

            var productTypeName = await _db.product_types
                .AsNoTracking()
                .Where(x => x.product_type_id == selectedSubProduct.product_type_id)
                .Select(x => x.name)
                .FirstOrDefaultAsync(ct);

            var receiptNo = $"PXBTP-{now:yyyyMMddHHmmss}-{prod.prod_id}";
            var fileName = $"{receiptNo}.pdf";

            var totalValue = selectedSubProduct.unit_cost_to_stage > 0
                ? selectedSubProduct.unit_cost_to_stage * quantityIssued
                : 0m;

            var pdfBytes = SubProductIssueReceiptPdfHelper.GeneratePdf(
                new SubProductIssueReceiptPdfModel
                {
                    receipt_no = receiptNo,
                    created_at = now,

                    prod_id = prod.prod_id,
                    production_code = prod.code,

                    order_id = ord.order_id,
                    order_code = ord.code,
                    customer_name = orderReq?.customer_name,

                    sub_product_id = selectedSubProduct.id,
                    product_type_name = productTypeName,

                    width = selectedSubProduct.width,
                    length = selectedSubProduct.length,

                    product_process = selectedSubProduct.product_process,

                    paper_material_code = selectedSubProduct.paper_material_code,
                    coating_material_code = selectedSubProduct.coating_material_code,
                    lamination_material_code = selectedSubProduct.lamination_material_code,
                    wave_material_code = selectedSubProduct.wave_material_code,
                    material_signature = selectedSubProduct.material_signature,

                    quantity_issued = quantityIssued,
                    quantity_after_issue = selectedSubProduct.quantity,

                    unit_cost_to_stage = selectedSubProduct.unit_cost_to_stage,
                    total_cost_to_stage = totalValue,

                    reason = reason
                });

            await using var ms = new MemoryStream(pdfBytes);

            var url = await _fileStorage.UploadAsync(
                ms,
                fileName,
                "application/pdf",
                "sub-products/issue-receipts");

            prod.sub_product_issue_file = url;

            await _db.SaveChangesAsync(ct);

            return url;
        }

        private async Task<cost_estimate> LoadAcceptedEstimateOrThrowAsync(
    order_request orderReq,
    CancellationToken ct)
        {
            cost_estimate? estimate = null;

            if (orderReq.accepted_estimate_id.HasValue &&
                orderReq.accepted_estimate_id.Value > 0)
            {
                estimate = await _db.cost_estimates
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == orderReq.accepted_estimate_id.Value &&
                        x.order_request_id == orderReq.order_request_id,
                        ct);
            }

            estimate ??= await _db.cost_estimates
                .Where(x => x.order_request_id == orderReq.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            if (estimate == null)
                throw new InvalidOperationException("Không tìm thấy cost_estimate để reserve NVL.");

            return estimate;
        }

        private sealed class SubOptionSelectionForMethod
        {
            public ValidSubProductOption? DisplayOption { get; init; }

            public ValidSubProductOption? EnoughForSubOption { get; init; }

            public ValidSubProductOption? PartialForBothOption { get; init; }
        }

        private static SubOptionSelectionForMethod SelectSubOptionForMethod(
            List<ValidSubProductOption> validSubOptions,
            int orderQty)
        {
            validSubOptions ??= new List<ValidSubProductOption>();

            var enoughForSub = validSubOptions
                .FirstOrDefault(x =>
                    x.Sub.quantity >= orderQty);

            var partialForBoth = validSubOptions
                .FirstOrDefault(x =>
                    x.Sub.quantity > 0 &&
                    x.Sub.quantity < orderQty);

            return new SubOptionSelectionForMethod
            {
                DisplayOption =
                    enoughForSub ??
                    partialForBoth ??
                    validSubOptions.FirstOrDefault(),

                EnoughForSubOption = enoughForSub,
                PartialForBothOption = partialForBoth
            };
        }

        private sealed class EstimateProcessCostRow
        {
            public string? process_code { get; set; }

            public decimal total_cost { get; set; }
        }

        private sealed class MethodCostBuildResult
        {
            public decimal unit_cost { get; set; }

            public decimal total_cost { get; set; }

            public decimal material_cost { get; set; }

            public decimal process_cost { get; set; }

            public decimal design_cost { get; set; }

            public decimal overhead_cost { get; set; }

            public decimal rush_amount { get; set; }

            public decimal discount_amount { get; set; }

            public List<string> covered_stages { get; set; } = new();

            public List<string> extra_stages { get; set; } = new();
        }

        private static MethodCostBuildResult BuildSubOrBothMethodCostWithoutOverheadRushDiscount(
            cost_estimate est,
            List<EstimateProcessCostRow> processCosts,
            List<string> orderRoute,
            sub_product sub,
            int subUsedQty,
            int nvlQty,
            int orderQty)
        {
            orderQty = orderQty > 0 ? orderQty : 1;
            subUsedQty = Math.Max(subUsedQty, 0);
            nvlQty = Math.Max(nvlQty, 0);

            var subPath = SubProductCompatibilityHelper.ParseRoute(sub.product_process);

            /*
             * Các công đoạn order còn thiếu sau phần BTP.
             */
            var extraStagesForSubPart = orderRoute
                .Where(x => !IsStageCoveredBySub(x, subPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            /*
             * 1. Giá trị BTP đã có.
             *
             * unit_cost_to_stage là giá 1 sản phẩm BTP tới stage sub.product_process.
             * Ví dụ sub RALO,CAT,IN có unit_cost_to_stage = 3300
             * => dùng 10.000 BTP thì giá trị BTP = 3300 * 10.000.
             */
            var subStageUnitCost = Money(sub.unit_cost_to_stage);

            if (subStageUnitCost <= 0m)
            {
                /*
                 * Fallback cho dữ liệu cũ chưa có unit_cost_to_stage.
                 * Fallback chỉ tính material/process đã cover, không overhead/rush/discount.
                 */
                subStageUnitCost = EstimateCoveredUnitCostFromOrderWithoutOverheadRushDiscount(
                    est,
                    processCosts,
                    orderRoute,
                    subPath,
                    orderQty);
            }

            var subProductValue = subStageUnitCost * subUsedQty;

            /*
             * 2. Chi phí hoàn thiện phần BTP.
             *
             * Nếu sub đã có RALO,CAT,IN thì không tính lại:
             * - paper_cost
             * - ink_cost
             * - process cost RALO/CAT/IN
             *
             * Chỉ tính phần order có nhưng sub chưa có.
             */
            var subRatio = orderQty <= 0
                ? 0m
                : subUsedQty / (decimal)orderQty;

            var remainingMaterialForSubPart =
                GetRemainingMaterialCostAfterSub(est, orderRoute, subPath) * subRatio;

            var remainingProcessForSubPart =
                GetRemainingProcessCostAfterSub(processCosts, subPath) * subRatio;

            /*
             * 3. Phần NVL trong BOTH.
             *
             * Phần này sản xuất từ đầu nên lấy full material/process theo tỷ lệ nvlQty/orderQty.
             * Vẫn không cộng overhead/rush/discount theo yêu cầu mới.
             */
            var nvlRatio = orderQty <= 0
                ? 0m
                : nvlQty / (decimal)orderQty;

            var nvlMaterialPart =
                GetFullNvlMaterialCost(est) * nvlRatio;

            var nvlProcessPart =
                GetFullProcessCost(processCosts) * nvlRatio;

            /*
             * materialCost ở đây bao gồm:
             * - giá trị BTP đã có
             * - vật liệu còn phải dùng để hoàn thiện BTP
             * - vật liệu để sản xuất phần NVL nếu là BOTH
             */
            var methodMaterialCost =
                subProductValue +
                remainingMaterialForSubPart +
                nvlMaterialPart;

            var methodProcessCost =
                remainingProcessForSubPart +
                nvlProcessPart;

            var built = BuildMethodCostWithoutOverheadRushDiscount(
                est,
                methodMaterialCost,
                methodProcessCost,
                orderQty);

            built.covered_stages = subPath;
            built.extra_stages = extraStagesForSubPart;

            return built;
        }

        private static MethodCostBuildResult BuildMethodCostWithoutOverheadRushDiscount(
            cost_estimate est,
            decimal materialCost,
            decimal processCost,
            int orderQty)
        {
            orderQty = orderQty > 0 ? orderQty : 1;

            materialCost = Money(materialCost);
            processCost = Money(processCost);

            /*
             * Theo yêu cầu mới:
             * Không tính:
             * - overhead
             * - rush
             * - discount
             *
             * Chỉ cộng design_cost nếu báo giá có.
             */
            var designCost = Money(est.design_cost);

            var total =
                materialCost +
                processCost +
                designCost;

            total = Math.Round(total, 2);

            return new MethodCostBuildResult
            {
                material_cost = Math.Round(materialCost, 2),
                process_cost = Math.Round(processCost, 2),
                design_cost = Math.Round(designCost, 2),

                overhead_cost = 0m,
                rush_amount = 0m,
                discount_amount = 0m,

                total_cost = total,
                unit_cost = SafeUnitCost(total, orderQty)
            };
        }

        private static bool OrderNeedsAny(
            List<string> orderRoute,
            params string[] codes)
        {
            if (orderRoute == null || orderRoute.Count == 0)
                return false;

            var set = codes
                .Select(SubProductCompatibilityHelper.Norm)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return orderRoute.Any(x => set.Contains(SubProductCompatibilityHelper.Norm(x)));
        }

        private static decimal SafeUnitCost(decimal totalCost, int qty)
        {
            qty = qty > 0 ? qty : 1;
            return Math.Round(totalCost / qty, 4);
        }

        private static decimal Money(decimal value)
        {
            return value < 0m ? 0m : value;
        }

        private static decimal GetFullNvlMaterialCost(cost_estimate est)
        {
            /*
             * Theo file tính giá:
             * material_cost = paper + ink + coating + mounting glue + lamination.
             */
            var explicitSum =
                Money(est.paper_cost) +
                Money(est.ink_cost) +
                Money(est.coating_glue_cost) +
                Money(est.mounting_glue_cost) +
                Money(est.lamination_cost);

            var materialCost = Money(est.material_cost);

            /*
             * Ưu tiên material_cost nếu DB đã lưu tổng lớn hơn explicitSum.
             */
            return Math.Round(Math.Max(materialCost, explicitSum), 2);
        }

        private static decimal GetRemainingMaterialCostAfterSub(
            cost_estimate est,
            List<string> orderRoute,
            List<string> subPath)
        {
            var total = 0m;

            /*
             * Paper:
             * Nếu sub đã có từ CAT/IN trở đi thì không tính lại giấy.
             */
            if (OrderNeedsAny(orderRoute, "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN") &&
                !SubAlreadyContainsPaper(subPath))
            {
                total += Money(est.paper_cost);
            }

            /*
             * Ink:
             * Nếu sub đã có IN thì không tính lại mực.
             */
            if (OrderNeedsAny(orderRoute, "IN") &&
                !IsStageCoveredBySub("IN", subPath))
            {
                total += Money(est.ink_cost);
            }

            /*
             * Coating:
             * Chỉ tính nếu order có PHU và sub chưa có PHU.
             */
            if (OrderNeedsAny(orderRoute, "PHU") &&
                !IsStageCoveredBySub("PHU", subPath))
            {
                total += Money(est.coating_glue_cost);
            }

            /*
             * Lamination:
             * Chỉ tính nếu order có CAN/CAN_MANG và sub chưa có.
             */
            if (OrderNeedsAny(orderRoute, "CAN", "CAN_MANG") &&
                !IsStageCoveredBySub("CAN", subPath) &&
                !IsStageCoveredBySub("CAN_MANG", subPath))
            {
                total += Money(est.lamination_cost);
            }

            /*
             * Mounting:
             * Chỉ tính nếu order có BOI và sub chưa có BOI.
             * Theo file giá bạn gửi, chi phí vật liệu BOI là mounting_glue_cost.
             */
            if (OrderNeedsAny(orderRoute, "BOI") &&
                !IsStageCoveredBySub("BOI", subPath))
            {
                total += Money(est.mounting_glue_cost);
            }

            return Math.Round(total, 2);
        }

        private static decimal GetFullProcessCost(List<EstimateProcessCostRow> processCosts)
        {
            if (processCosts == null || processCosts.Count == 0)
                return 0m;

            return Math.Round(
                processCosts.Sum(x => Money(x.total_cost)),
                2);
        }

        private static decimal GetRemainingProcessCostAfterSub(
            List<EstimateProcessCostRow> processCosts,
            List<string> subPath)
        {
            if (processCosts == null || processCosts.Count == 0)
                return 0m;

            return Math.Round(
                processCosts
                    .Where(x => !IsProcessCostCoveredBySub(x.process_code, subPath))
                    .Sum(x => Money(x.total_cost)),
                2);
        }

        private static decimal EstimateCoveredUnitCostFromOrderWithoutOverheadRushDiscount(
            cost_estimate est,
            List<EstimateProcessCostRow> processCosts,
            List<string> orderRoute,
            List<string> subPath,
            int orderQty)
        {
            orderQty = orderQty > 0 ? orderQty : 1;

            var coveredMaterial =
                GetFullNvlMaterialCost(est) -
                GetRemainingMaterialCostAfterSub(est, orderRoute, subPath);

            var coveredProcess =
                GetFullProcessCost(processCosts) -
                GetRemainingProcessCostAfterSub(processCosts, subPath);

            var coveredTotal =
                Math.Max(coveredMaterial, 0m) +
                Math.Max(coveredProcess, 0m);

            return SafeUnitCost(coveredTotal, orderQty);
        }

        private static bool IsProcessCostCoveredBySub(
            string? processCode,
            List<string> subPath)
        {
            var code = SubProductCompatibilityHelper.Norm(processCode);

            if (string.IsNullOrWhiteSpace(code))
                return false;

            /*
             * CAN/CAN_MANG có thể lệch mã giữa estimate và process.
             */
            if (code is "CAN" or "CAN_MANG")
            {
                return subPath.Any(x =>
                    string.Equals(x, "CAN", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x, "CAN_MANG", StringComparison.OrdinalIgnoreCase));
            }

            return subPath.Contains(code, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsStageCoveredBySub(
            string processCode,
            List<string> subPath)
        {
            var code = SubProductCompatibilityHelper.Norm(processCode);

            if (string.IsNullOrWhiteSpace(code))
                return false;

            if (code is "CAN" or "CAN_MANG")
            {
                return subPath.Any(x =>
                    string.Equals(x, "CAN", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x, "CAN_MANG", StringComparison.OrdinalIgnoreCase));
            }

            return subPath.Contains(code, StringComparer.OrdinalIgnoreCase);
        }

        private static bool SubAlreadyContainsPaper(List<string> subPath)
        {
            /*
             * RALO là bản/kẽm, chưa tính là BTP trên giấy.
             * Từ CAT/IN trở đi thì BTP đã chứa giấy.
             */
            return subPath.Any(x =>
                x is "CAT" or "IN" or "PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN");
        }

        private static decimal RoundTotalMoneyDownToThousands(decimal value)
        {
            /*
             * Làm tròn xuống hàng nghìn.
             * Ví dụ:
             * 7,654,321 => 7,654,000
             * 7,654,999 => 7,654,000
             */
            if (value <= 0m)
                return 0m;

            return Math.Floor(value / 1000m) * 1000m;
        }

        private static decimal? RoundTotalMoneyDownToThousands(decimal? value)
        {
            if (!value.HasValue)
                return null;

            return RoundTotalMoneyDownToThousands(value.Value);
        }

        private static decimal CalculateUnitCostFromRoundedTotal(
            decimal roundedTotalCost,
            int quantity)
        {
            quantity = quantity > 0 ? quantity : 1;

            /*
             * Unit cũng là VND nên trả số nguyên.
             * Nếu bạn muốn giữ số lẻ unit cost thì bỏ Math.Round ở đây.
             */
            return Math.Round(
                roundedTotalCost / quantity,
                0,
                MidpointRounding.AwayFromZero);
        }

        private static void NormalizeStartReadyCostOptionsByRoundedTotal(
            List<ProductionMethodCostOptionDto> options,
            int orderQty)
        {
            if (options == null || options.Count == 0)
                return;

            orderQty = orderQty > 0 ? orderQty : 1;

            /*
             * 1. Làm tròn total_cost của từng method xuống hàng nghìn.
             * 2. Tính lại unit_cost từ total_cost đã làm tròn.
             */
            foreach (var option in options)
            {
                if (option.total_cost > 0m)
                {
                    option.total_cost = RoundTotalMoneyDownToThousands(option.total_cost);
                    option.unit_cost = CalculateUnitCostFromRoundedTotal(
                        option.total_cost,
                        orderQty);
                }
                else
                {
                    option.total_cost = 0m;
                    option.unit_cost = 0m;
                }
            }

            /*
             * 3. Tính lại saving dựa trên total_cost đã làm tròn.
             * Không dùng saving cũ vì saving cũ đang tính từ số chưa làm tròn.
             */
            var nvl = options.FirstOrDefault(x =>
                string.Equals(x.method, "NVL", StringComparison.OrdinalIgnoreCase));

            if (nvl == null || nvl.total_cost <= 0m)
                return;

            foreach (var option in options)
            {
                if (string.Equals(option.method, "NVL", StringComparison.OrdinalIgnoreCase))
                {
                    option.saving_vs_nvl_unit = null;
                    option.saving_vs_nvl_total = null;
                    continue;
                }

                if (!option.is_available || option.total_cost <= 0m)
                {
                    option.saving_vs_nvl_unit = null;
                    option.saving_vs_nvl_total = null;
                    continue;
                }

                var savingTotal = nvl.total_cost - option.total_cost;

                option.saving_vs_nvl_total = savingTotal;
                option.saving_vs_nvl_unit = CalculateUnitCostFromRoundedTotal(
                    savingTotal,
                    orderQty);
            }
        }

        private readonly IWebHostEnvironment _env;
    }
}