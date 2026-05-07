using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
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

        public ProductionService(
            IHubContext<RealtimeHub> rt,
            IProductionRepository repo,
            IRealtimePublisher hub,
            AppDbContext db,
            IOrderRepository orderRepository,
            IRequestRepository requestRepository,
            NotificationService notiService,
            IWebHostEnvironment env,
            ICloudinaryFileStorageService fileStorage)
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

        public Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(
            int page,
            int pageSize,
            int? roleId,
            CancellationToken ct = default)
            => _repo.GetProducingOrdersAsync(page, pageSize, roleId, ct);

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

            var materials = await GetMaterialReadinessAsync(orderId, ct);

            var machines = await GetMachineReadinessAsync(orderId, ct);

            var hasEnoughMaterial =
                materials.Count > 0 &&
                materials.All(x => x.is_enough);


            var hasFreeMachine =
                machines.Count > 0 &&
                machines.All(x => x.machine_found && x.is_available);

            var matchedSubProduct = await FindMatchedSubProductAsync(orderId, ct);

            var hasMatchedSubProduct = matchedSubProduct != null;

            var subProductMessage = hasMatchedSubProduct
                ? "Có bán thành phẩm phù hợp với đơn hàng."
                : "Không có bán thành phẩm phù hợp với đơn hàng.";

            return new ProductionReadyCheckResponse
            {
                order_id = orderId,
                production_id = prod?.prod_id,

                is_production_ready = ord.is_production_ready,
                has_enough_material = hasEnoughMaterial,
                has_free_machine = hasFreeMachine,

                materials = materials,
                machines = machines,

                product_type_id = prod?.product_type_id,
                request_print_width_mm = req?.print_width_mm,
                request_print_length_mm = req?.print_length_mm,
                order_quantity = req?.quantity,

                is_full_process = (bool)prod.is_full_process,
                selected_sub_product_id = prod?.sub_product_id,
                sub_product_used_qty = prod?.sub_product_used_qty ?? 0,

                has_matched_sub_product = hasMatchedSubProduct,
                sub_product_message = subProductMessage,
                matched_sub_product = matchedSubProduct
            };
        }

        public async Task<bool> SetProductionReadyAsync(int orderId, bool isProductionReady, CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
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

                if (!isProductionReady)
                {
                    ord.is_production_ready = false;
                    ord.is_enough = false;


                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    return true;
                }

                var materials = await GetMaterialReadinessAsync(orderId, ct);
                var machines = await GetMachineReadinessAsync(orderId, ct);

                var hasEnoughMaterial =
                    materials.Count > 0 &&
                    materials.All(x => x.is_enough);

                var hasFreeMachine =
                    machines.Count > 0 &&
                    machines.All(x => x.machine_found && x.is_available);

                if (!hasEnoughMaterial || !hasFreeMachine)
                {
                    throw new InvalidOperationException(
                        "Order chưa đủ điều kiện sản xuất: thiếu nguyên vật liệu hoặc chưa có đủ máy rảnh cho toàn bộ quy trình.");
                }

                ord.is_enough = true;
                ord.is_production_ready = true;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                await SendProductionReadyNotificationAsync(orderId);

                return true;
            });
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
                    order_qty = oi.quantity,

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

            if (prod == null)
                return new List<ProductionReadyMachineDto>();

            var tasks = await _db.tasks
                .Include(x => x.process)
                .AsNoTracking()
                .Where(x => x.prod_id == prod.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return new List<ProductionReadyMachineDto>();

            var targetTasks = tasks
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToList();

            var machineCodes = targetTasks
                .Where(x => !string.IsNullOrWhiteSpace(x.machine))
                .Select(x => x.machine!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var machineMap = await _db.machines
                .AsNoTracking()
                .Where(x => machineCodes.Contains(x.machine_code))
                .ToDictionaryAsync(x => x.machine_code, StringComparer.OrdinalIgnoreCase, ct);

            var result = new List<ProductionReadyMachineDto>();

            foreach (var t in targetTasks)
            {
                var machineCode = t.machine?.Trim();
                var processCode = t.process?.process_code;
                var processName = t.process?.process_name ?? t.name;

                if (string.IsNullOrWhiteSpace(machineCode))
                {
                    result.Add(new ProductionReadyMachineDto
                    {
                        process_id = t.process_id,
                        seq_num = t.seq_num,
                        process_code = processCode,
                        process_name = processName,
                        machine_code = null,
                        machine_found = false,
                        is_available = false,
                        total_quantity = 0,
                        busy_quantity = 0,
                        free_quantity = 0,
                        status = "Unmapped"
                    });

                    continue;
                }

                if (!machineMap.TryGetValue(machineCode, out var machine) || !machine.is_active)
                {
                    result.Add(new ProductionReadyMachineDto
                    {
                        process_id = t.process_id,
                        seq_num = t.seq_num,
                        process_code = processCode,
                        process_name = processName,
                        machine_code = machineCode,
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
                    process_id = t.process_id,
                    seq_num = t.seq_num,
                    process_code = processCode,
                    process_name = processName,

                    machine_code = machineCode,
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

        private async Task<MatchedSubProductDto?> FindMatchedSubProductAsync(
    int orderId,
    CancellationToken ct = default)
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
            var orderQty = req.quantity ?? 0;

            if (!printWidth.HasValue || !printLength.HasValue || orderQty <= 0)
                return null;

            return await _db.sub_products
                .AsNoTracking()
                .Include(x => x.product_type)
                .Where(x =>
                    x.is_active == true &&
                    x.product_type_id == prod.product_type_id.Value &&
                    x.width == printWidth.Value &&
                    x.length == printLength.Value &&
                    x.quantity >= orderQty)
                .OrderBy(x => x.quantity)
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

            var fileName = $"phieu_nhap_kho_{safeOrderCode}_{source.prod_id}.docx";

            var tempFolder = Path.Combine(Path.GetTempPath(), "amms-import-receives");
            Directory.CreateDirectory(tempFolder);

            var tempFilePath = Path.Combine(tempFolder, fileName);

            ImportReceiveDocxHelper.Generate(tempFilePath, source);

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

        public Task<SetProductionMethodResponse?> SetProductionMethodAsync(
    SetProductionMethodRequest req,
    CancellationToken ct = default)
        {
            return _repo.SetProductionMethodAsync(req, ct);
        }
        private readonly IWebHostEnvironment _env;
    }
}