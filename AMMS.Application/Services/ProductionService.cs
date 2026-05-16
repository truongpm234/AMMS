using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
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

        public async Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(
    int page,
    int pageSize,
    int? roleId,
    CancellationToken ct = default)
        {
            var result = await _repo.GetProducingOrdersAsync(
                page,
                pageSize,
                roleId,
                ct);

            await FillCanStartForGroupProductionsAsync(result.Data, ct);

            return result;
        }

        private async Task FillCanStartForGroupProductionsAsync(
    List<ProducingOrderCardDto> items,
    CancellationToken ct)
        {
            if (items == null || items.Count == 0)
                return;

            foreach (var item in items)
            {
                var isGroupOrSplit =
                    string.Equals(item.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase);

                if (!isGroupOrSplit)
                {
                    item.can_start = null;
                    item.can_start_message = null;
                    continue;
                }

                var status = item.production_status ?? item.status;

                var isWaitingToStart =
                    string.Equals(status, "Scheduled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Unassigned", StringComparison.OrdinalIgnoreCase);

                if (!isWaitingToStart)
                {
                    item.can_start = false;
                    item.can_start_message = status switch
                    {
                        "InProcessing" => "Production đã bắt đầu.",
                        "Importing" => "Production đã hoàn tất sản xuất, đang chờ nhập kho.",
                        "Delivery" => "Production đã chuyển giao hàng.",
                        "Completed" => "Production đã hoàn thành.",
                        _ => $"Production không ở trạng thái có thể bắt đầu. Trạng thái hiện tại: {status ?? "null"}."
                    };

                    continue;
                }

                var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                    _db,
                    item.prod_id,
                    ct);

                item.can_start = dep.can_start;
                item.can_start_message = dep.can_start
                    ? "Có thể bắt đầu production."
                    : dep.message;
            }
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

        public async Task<bool> StartProductionByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var ord = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
                throw new InvalidOperationException("Order not found");

            if (!ord.is_production_ready)
                throw new InvalidOperationException("General manager has not confirmed production readiness.");

            var now = AppTime.NowVnUnspecified();
            var prodId = await _repo.StartProductionByOrderIdOnlyAsync(orderId, now, ct);
            return prodId.HasValue;
        }

        public async Task<bool> SetProductionDeliveryAsync(int orderId, CancellationToken ct = default)
        {
            return await _repo.SetProductionDeliveryByOrderIdAsync(orderId, ct);
        }

        public async Task<bool> SetCompletedAsync(int orderId, CancellationToken ct = default)
        {
            return await _repo.SetCompletedByOrderIdAsync(orderId, ct);
        }

        public async Task<int?> StartProductionAndPromoteFirstTaskAsync(int orderId, CancellationToken ct = default)
        {
            var ord = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
                throw new InvalidOperationException("Order not found");

            if (!ord.is_production_ready)
                throw new InvalidOperationException("General manager has not confirmed production readiness.");

            return await _repo.StartProductionByOrderIdOnlyAsync(
                orderId,
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

        public async Task<ProductionReadyCheckResponse?> GetProductionReadyAsync(int orderId, CancellationToken ct = default)
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

            var orderQty = req?.quantity ?? 0;

            var fullMaterials = await GetMaterialReadinessAsync(orderId, null, ct);
            var machines = await GetMachineReadinessAsync(orderId, ct);

            var hasEnoughMaterial =
                fullMaterials.Count > 0 &&
                fullMaterials.All(x => x.is_enough);

            var hasFreeMachine =
                machines.Count > 0 &&
                machines.All(x => x.machine_found && x.is_available);

            var matchedSubProduct = await FindMatchedSubProductAsync(orderId, ct);
            var hasMatchedSubProduct = matchedSubProduct != null;

            var subQty = matchedSubProduct?.quantity ?? 0;

            var canUseNvl = hasEnoughMaterial && hasFreeMachine;

            var canUseSub =
                matchedSubProduct != null &&
                orderQty > 0 &&
                subQty >= orderQty &&
                hasFreeMachine;

            var nvlQtyForBoth =
                matchedSubProduct != null &&
                orderQty > 0 &&
                subQty > 0 &&
                subQty < orderQty
                    ? orderQty - subQty
                    : 0;

            var remainingMaterialsForBoth = nvlQtyForBoth > 0
                ? await GetMaterialReadinessAsync(orderId, nvlQtyForBoth, ct)
                : new List<ProductionReadyMaterialDto>();

            var canUseBoth =
                matchedSubProduct != null &&
                subQty > 0 &&
                orderQty > subQty &&
                remainingMaterialsForBoth.Count > 0 &&
                remainingMaterialsForBoth.All(x => x.is_enough) &&
                hasFreeMachine;

            var optionCount = 0;
            if (canUseNvl) optionCount++;
            if (canUseSub) optionCount++;
            if (canUseBoth) optionCount++;

            var needManagerApproval =
                optionCount >= 2 ||
                canUseSub ||
                canUseBoth;

            string? subMessage;

            if (!hasMatchedSubProduct)
            {
                subMessage = "Không có bán thành phẩm phù hợp với đơn hàng.";
            }
            else if (subQty >= orderQty)
            {
                subMessage = "Có bán thành phẩm phù hợp và đủ số lượng.";
            }
            else
            {
                subMessage = $"Có bán thành phẩm phù hợp nhưng chưa đủ số lượng. Có {subQty}, cần {orderQty}, còn thiếu {Math.Max(orderQty - subQty, 0)}.";
            }

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

                product_type_id = prod?.product_type_id,
                request_print_width_mm = req?.print_width_mm,
                request_print_length_mm = req?.print_length_mm,
                order_quantity = orderQty,

                is_full_process = prod?.is_full_process,
                production_method = prod?.prod_method,

                gm_proposed_method = prod?.gm_proposed_method,
                proposed_production_method = prod?.gm_proposed_method,

                gm_note = prod?.gm_note,
                mgr_note = prod?.mgr_note,

                can_use_nvl = canUseNvl,
                can_use_sub = canUseSub,
                can_use_both = canUseBoth,
                need_manager_approval = needManagerApproval,
                nvl_qty = prod?.nvl_qty ?? nvlQtyForBoth,

                selected_sub_product_id = prod?.sub_product_id,
                sub_product_used_qty = prod?.sub_product_used_qty ?? 0,

                has_matched_sub_product = hasMatchedSubProduct,
                sub_product_message = subMessage,
                matched_sub_product = matchedSubProduct
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

                    prod.prod_method = null;
                    prod.gm_proposed_method = null;
                    prod.is_full_process = null;
                    prod.nvl_qty = 0;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

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
                    throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ.");

                var fullMaterials = await GetMaterialReadinessAsync(orderId, null, ct);
                var machines = await GetMachineReadinessAsync(orderId, ct);

                var hasEnoughMaterial =
                    fullMaterials.Count > 0 &&
                    fullMaterials.All(x => x.is_enough);

                var hasFreeMachine =
                    machines.Count > 0 &&
                    machines.All(x => x.machine_found && x.is_available);

                var matchedSubProduct = await FindMatchedSubProductAsync(orderId, ct);
                var subQty = matchedSubProduct?.quantity ?? 0;

                var canUseNvl = hasEnoughMaterial && hasFreeMachine;

                var canUseSub =
                    matchedSubProduct != null &&
                    subQty >= orderQty &&
                    hasFreeMachine;

                var nvlQtyForBoth =
                    matchedSubProduct != null &&
                    subQty > 0 &&
                    subQty < orderQty
                        ? orderQty - subQty
                        : 0;

                var remainingMaterialsForBoth = nvlQtyForBoth > 0
                    ? await GetMaterialReadinessAsync(orderId, nvlQtyForBoth, ct)
                    : new List<ProductionReadyMaterialDto>();

                var canUseBoth =
                    matchedSubProduct != null &&
                    subQty > 0 &&
                    subQty < orderQty &&
                    remainingMaterialsForBoth.Count > 0 &&
                    remainingMaterialsForBoth.All(x => x.is_enough) &&
                    hasFreeMachine;

                var optionCount = 0;
                if (canUseNvl) optionCount++;
                if (canUseSub) optionCount++;
                if (canUseBoth) optionCount++;

                if (optionCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Order chưa đủ điều kiện sản xuất: thiếu NVL, thiếu bán thành phẩm hoặc chưa có đủ máy rảnh.");
                }

                // Validate method GM đề xuất, nếu có gửi.
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

                // CASE 1: GM đề xuất NVL.
                // Nếu NVL hợp lệ thì auto duyệt như flow cũ.
                if (proposedMethod == "NVL")
                {
                    ord.is_enough = true;
                    ord.is_production_ready = true;

                    prod.prod_method = "NVL";
                    prod.is_full_process = true;
                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                    prod.nvl_qty = orderQty;

                    shouldAutoSchedule = true;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                // CASE 2: GM đề xuất SUB/BOTH.
                // Chỉ lưu đề xuất, gửi manager duyệt.
                if (proposedMethod is "SUB" or "BOTH")
                {
                    ord.is_production_ready = false;

                    // Method thật chưa được manager duyệt.
                    prod.prod_method = null;
                    prod.is_full_process = null;
                    prod.nvl_qty = 0;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                // CASE 3: FE cũ không gửi gm_proposed_method.
                // Giữ logic cũ: nếu chỉ có NVL thì auto duyệt.
                if (proposedMethod == null && optionCount == 1 && canUseNvl)
                {
                    ord.is_enough = true;
                    ord.is_production_ready = true;

                    prod.prod_method = "NVL";
                    prod.is_full_process = true;
                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                    prod.nvl_qty = orderQty;

                    shouldAutoSchedule = true;

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                // CASE 4: Không gửi proposed method hoặc có nhiều option.
                // Chờ manager duyệt method.
                ord.is_production_ready = false;

                prod.prod_method = null;
                prod.is_full_process = null;
                prod.nvl_qty = 0;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return true;
            });

            if (result && shouldAutoSchedule)
            {
                await ScheduleTasksAfterMethodAsync(orderId, ct);
                await SendProductionReadyNotificationAsync(orderId);
            }

            return result;
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

        private async Task<MatchedSubProductDto?> FindMatchedSubProductAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);

            if (prod == null || !prod.product_type_id.HasValue)
                return null;

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            if (req == null)
                return null;

            var printWidth = req.print_width_mm;
            var printLength = req.print_length_mm;

            if (!printWidth.HasValue || !printLength.HasValue)
                return null;

            return await _db.sub_products
                .AsNoTracking()
                .Include(x => x.product_type)
                .Where(x =>
                    x.is_active == true &&
                    x.product_type_id == prod.product_type_id.Value &&
                    x.width == printWidth.Value &&
                    x.length == printLength.Value &&
                    x.quantity > 0)
                .OrderByDescending(x => x.quantity)
                .ThenByDescending(x => x.updated_at)
                .ThenByDescending(x => x.id)
                .Select(x => new MatchedSubProductDto
                {
                    id = x.id,
                    product_type_id = x.product_type_id,
                    product_type_name = x.product_type != null ? x.product_type.name : null,
                    width = x.width,
                    length = x.length,
                    product_process = x.product_process,
                    quantity = x.quantity,
                    is_active = x.is_active,
                    description = x.description,
                    updated_at = x.updated_at
                })
                .FirstOrDefaultAsync(ct);
        }

        public async Task<GenerateImportReceiveResponse?> GenerateImportReceiveAsync(int orderId, CancellationToken ct = default)
        {
            var source = await _repo.GetImportReceiveSourceByOrderIdAsync(orderId, ct);
            if (source == null)
                return null;

            if (source.items == null || source.items.Count == 0)
                throw new InvalidOperationException("Đơn hàng không có item để tạo phiếu nhập kho.");

            var safeOrderCode = string.IsNullOrWhiteSpace(source.order_code)
                ? "NO_CODE"
                : source.order_code.Trim();

            var fileName = $"phieu_nhap_kho_{safeOrderCode}_{source.prod_id}.pdf";

            var tempFolder = Path.Combine(Path.GetTempPath(), "amms-import-receives");
            Directory.CreateDirectory(tempFolder);

            var tempFilePath = Path.Combine(tempFolder, fileName);

            ImportReceivePdfHelper.Generate(tempFilePath, source);

            var publicId = $"import-receives/phieu_nhap_kho_{safeOrderCode}_{source.prod_id}";

            await using var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var cloudUrl = await _fileStorage.UploadRawWithPublicIdAsync(
                fs,
                fileName,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                publicId);

            var saved = await _repo.SaveImportReceivePathAsync(source.prod_id, cloudUrl, ct);
            if (!saved)
                throw new InvalidOperationException("Không lưu được đường dẫn phiếu nhập kho vào production.");

            try
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
            catch
            {
            }

            return new GenerateImportReceiveResponse
            {
                success = true,
                prod_id = source.prod_id,
                order_id = source.order_id,
                order_code = source.order_code,
                import_recieve_path = cloudUrl,
                message = "Tạo phiếu nhập kho thành công"
            };
        }

        public Task<SetProductionMethodResponse?> SetProductionMethodAsync(SetProductionMethodRequest req, CancellationToken ct = default)
        {
            return _repo.SetProductionMethodAsync(req, ct);
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

        private static string? NormalizeNote(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            return value.Length <= 1000
                ? value
                : value.Substring(0, 1000);
        }

        private readonly IWebHostEnvironment _env;
    }
}