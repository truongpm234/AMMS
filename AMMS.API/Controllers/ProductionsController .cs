using AMMS.API.Jobs;
using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Exceptions;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    //[Authorize]
    public class ProductionsController : ControllerBase
    {
        private readonly IProductionService _service;
        private readonly IProductionSchedulingService _svc;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<ProductionsController> _logger;
        private readonly IMaterialService _materialService;
        private readonly IDealService _dealService;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly NotificationService _noti;
        private readonly DeliveryHandoverEmailJob _deliveryHandoverEmailJob;
        public ProductionsController(
    NotificationService noti,
    IHubContext<RealtimeHub> hub,
    IProductionService service,
    IProductionSchedulingService svc,
    IBackgroundJobClient backgroundJobClient,
    ILogger<ProductionsController> logger,
    IMaterialService materialService,
    IDealService dealService,
    DeliveryHandoverEmailJob deliveryHandoverEmailJob)
        {
            _noti = noti;
            _hub = hub;
            _service = service;
            _svc = svc;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _materialService = materialService;
            _dealService = dealService;
            _deliveryHandoverEmailJob = deliveryHandoverEmailJob;
        }

        private int? GetUserId()
        {
            var userIdValue =
                User.FindFirst("userid")?.Value ??
                User.FindFirst("user_id")?.Value ??
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (int.TryParse(userIdValue, out var userId))
                return userId;

            return null;
        }

        [HttpPost("schedule")]
        public async Task<IActionResult> Schedule(
    [FromBody] ScheduleRequest req,
    CancellationToken ct)
        {
            if (req == null)
                return BadRequest(new { message = "Request body is required." });

            if (req.order_id <= 0)
                return BadRequest(new { message = "order_id không hợp lệ." });

            if (req.product_type_id <= 0)
                return BadRequest(new { message = "product_type_id không hợp lệ." });

            var prodId = await _svc.ScheduleOrderAsync(
                orderId: req.order_id,
                productTypeId: req.product_type_id,
                productionProcessCsv: req.production_processes,
                managerId: req.manager_id,
                isPriority: req.is_priority ?? false,
                ct: ct);

            return Ok(new
            {
                message = "Scheduled production successfully",
                prod_id = prodId,
                is_priority = req.is_priority ?? false
            });
        }

        [HttpPost("confirm-schedule/{prodId:int}")]
        public async Task<IActionResult> ConfirmSchedule(
    int prodId,
    [FromBody] ConfirmProductionScheduleRequest? req,
    CancellationToken ct)
        {
            try
            {
                int? userId = null;

                var rawUserId =
                    User.FindFirst("userid")?.Value ??
                    User.FindFirst("user_id")?.Value ??
                    User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (int.TryParse(rawUserId, out var parsed))
                    userId = parsed;

                var result = await _service.ConfirmScheduleAsync(
                    prodId,
                    userId,
                    req?.is_priority,
                    ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    prod_id = prodId
                });
            }
        }

        [HttpGet("nearest-delivery")]
        public async Task<IActionResult> GetNearestDelivery()
        {
            var result = await _service.GetNearestDeliveryAsync();
            return Ok(result);
        }

        [HttpGet("get-all-process-type")]
        public async Task<ActionResult<List<string>>> GetAllProcessTypeAsync()
        {
            var data = await _service.GetAllProcessTypeAsync();
            return Ok(data);
        }

        [HttpGet("get-all-production")]
        public async Task<IActionResult> GetProducingOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
        {
            var result = await _service.GetProducingOrdersAsync(page, pageSize, ct);
            return Ok(result);
        }

        [Authorize]
        [HttpGet("detail/{orderId:int}")]
        public async Task<IActionResult> GetProductionDetail(int orderId, CancellationToken ct)
        {
            var result = await _service.GetProductionDetailByOrderIdAsync(orderId, ct);
            if (result == null) return NotFound();

            return Ok(result);
        }

        [HttpGet("detail/production/{prodId:int}")]
        public async Task<IActionResult> GetProductionDetailByProdId(
    int prodId,
    CancellationToken ct)
        {
            var result = await _service.GetProductionDetailByProdIdAsync(prodId, ct);

            if (result == null)
            {
                return NotFound(new
                {
                    message = "Production not found.",
                    prod_id = prodId
                });
            }

            return Ok(result);
        }

        [HttpGet("progress/{prodId:int}")]
        public async Task<ActionResult<ProductionProgressResponse>> Progress(int prodId)
        {
            return Ok(await _service.GetProgressAsync(prodId));
        }

        [HttpGet("waste/{prodId:int}")]
        public async Task<IActionResult> GetWaste(int prodId, CancellationToken ct)
        {
            var result = await _service.GetProductionWasteAsync(prodId, ct);
            if (result == null) return NotFound();
            return Ok(result);
        }

        [HttpGet("information/{orderId:int}")]
        [ProducesResponseType(typeof(OrderMaterialsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<OrderMaterialsResponse>> Get(int orderId, CancellationToken ct)
        {
            var res = await _materialService.GetMaterialsByOrderIdAsync(orderId, ct);
            if (res == null) return NotFound();
            return Ok(res);
        }

        [HttpGet("start-ready/{orderId:int}")]
        [ProducesResponseType(typeof(ProductionReadyCheckResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProductionReady(int orderId, CancellationToken ct)
        {
            var result = await _service.GetProductionReadyAsync(orderId, ct);
            if (result == null)
                return NotFound(new { message = "Order not found" });

            return Ok(result);
        }

        [HttpPut("start-ready/{orderId:int}")]
        public async Task<IActionResult> SetProductionReady(
    int orderId,
    [FromBody] ConfirmProductionReadyRequest req,
    CancellationToken ct)
        {
            if (req == null)
            {
                return BadRequest(new
                {
                    message = "Request body is required.",
                    order_id = orderId
                });
            }

            try
            {
                var ok = await _service.SetProductionReadyAsync(
                    orderId,
                    req.is_production_ready,
                    req.gm_note,
                    req.GetProposedMethod(),
                    ct);

                if (!ok)
                    return NotFound(new { message = "Order not found" });

                var state = await _service.GetProductionReadyAsync(orderId, ct);

                /*
                 * Case 4:
                 * GM hủy trạng thái ready.
                 */
                if (!req.is_production_ready)
                {
                    await PublishProductionReadyCancelledAsync(
                        orderId,
                        state,
                        ct);

                    return Ok(new
                    {
                        order_id = orderId,
                        is_production_ready = false,
                        need_manager_approval = false,
                        event_type = "READY_CANCELLED",
                        message = "Production ready was cancelled."
                    });
                }

                var approvedMethod = NormalizeMethodForNotify(state?.production_method);

                /*
                 * Case 1:
                 * Chỉ có 1 method khả dụng.
                 * Service đã auto duyệt NVL/SUB/BOTH.
                 */
                if (state?.is_production_ready == true &&
                    approvedMethod is "NVL" or "SUB" or "BOTH")
                {
                    await PublishAutoApprovedProductionAsync(
                        orderId,
                        state,
                        ct);

                    return Ok(new
                    {
                        order_id = orderId,
                        production_id = state.production_id,
                        prod_id = state.production_id,

                        is_production_ready = true,
                        production_method = approvedMethod,
                        need_manager_approval = false,

                        event_type = "AUTO_APPROVED",
                        approval_flow = "AUTO_SINGLE_OPTION",

                        gm_note = req.gm_note,
                        gm_proposed_method = state.gm_proposed_method,
                        proposed_production_method = state.proposed_production_method,
                        sub_product_issue_file = state.sub_product_issue_file,
                        selected_sub_product_id = state.selected_sub_product_id,
                        sub_product_used_qty = state.sub_product_used_qty,
                        nvl_qty = state.nvl_qty,

                        can_use_nvl = state.can_use_nvl,
                        can_use_sub = state.can_use_sub,
                        can_use_both = state.can_use_both,

                        method_cost_options = state.method_cost_options,

                        nvl_estimated_unit_cost = state.nvl_estimated_unit_cost,
                        sub_estimated_unit_cost = state.sub_estimated_unit_cost,
                        both_estimated_unit_cost = state.both_estimated_unit_cost,

                        nvl_estimated_total_cost = state.nvl_estimated_total_cost,
                        sub_estimated_total_cost = state.sub_estimated_total_cost,
                        both_estimated_total_cost = state.both_estimated_total_cost,

                        message = $"Auto confirmed production by {approvedMethod} and scheduled tasks."
                    });
                }

                /*
                 * Case 2:
                 * Có từ 2 method khả dụng.
                 * GM có thể đã gợi ý, nhưng vẫn phải chờ manager duyệt.
                 */
                await PublishWaitingManagerApprovalAsync(
                    orderId,
                    state,
                    req,
                    ct);

                return Ok(new
                {
                    order_id = orderId,
                    production_id = state?.production_id,
                    prod_id = state?.production_id,
                    is_production_ready = false,
                    need_manager_approval = true,
                    event_type = "WAITING_MANAGER_APPROVAL",
                    approval_flow = "MANUAL_MULTI_OPTION",
                    gm_note = req.gm_note,
                    gm_proposed_method = state?.gm_proposed_method,
                    proposed_production_method = state?.proposed_production_method,
                    can_use_nvl = state?.can_use_nvl,
                    can_use_sub = state?.can_use_sub,
                    can_use_both = state?.can_use_both,
                    selected_sub_product_id = state?.selected_sub_product_id,
                    sub_product_used_qty = state?.sub_product_used_qty,
                    nvl_qty = state?.nvl_qty,
                    method_cost_options = state?.method_cost_options,
                    nvl_estimated_unit_cost = state?.nvl_estimated_unit_cost,
                    sub_estimated_unit_cost = state?.sub_estimated_unit_cost,
                    both_estimated_unit_cost = state?.both_estimated_unit_cost,
                    nvl_estimated_total_cost = state?.nvl_estimated_total_cost,
                    sub_estimated_total_cost = state?.sub_estimated_total_cost,
                    both_estimated_total_cost = state?.both_estimated_total_cost,
                    message = "Sent production method approval request to manager."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = orderId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Set production ready failed",
                    detail = ex.Message,
                    order_id = orderId
                });
            }
        }

        [HttpPost("production-method")]
        public async Task<IActionResult> SetProductionMethod(
    [FromBody] SetProductionMethodRequest req,
    CancellationToken ct)
        {
            if (req == null || req.order_id <= 0)
            {
                return BadRequest(new
                {
                    message = "order_id is required"
                });
            }

            try
            {
                var result = await _service.SetProductionMethodAsync(req, ct);

                if (result == null)
                {
                    return NotFound(new
                    {
                        message = "Order not found",
                        order_id = req.order_id
                    });
                }

                var prodId = result.prod_id;

                /*
                 * Case 3:
                 * Manager đã duyệt method khi có nhiều option.
                 */
                await PublishManagerApprovedProductionAsync(
                    req.order_id,
                    prodId,
                    result,
                    ct);

                return Ok(new
                {
                    result.success,
                    result.order_id,
                    result.prod_id,

                    scheduled_prod_id = (int?)null,

                    event_type = "MANAGER_APPROVED_METHOD",
                    approval_flow = "MANUAL_MULTI_OPTION",

                    production_approval_flow = result.production_approval_flow,
                    is_auto_production_approval = result.is_auto_production_approval,
                    production_approval_label = result.production_approval_label,

                    result.is_full_process,
                    result.production_method,
                    result.sub_product_id,
                    result.sub_product_used_qty,
                    result.nvl_qty,
                    result.order_quantity,
                    result.gm_note,
                    result.mgr_note,

                    message = "Manager approved production method. Waiting for GM to confirm scheduling."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = req.order_id,
                    is_full_process = req.is_full_process,
                    production_method = req.production_method,
                    sub_id = req.sub_id
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Set production method failed",
                    detail = ex.Message,
                    order_id = req.order_id
                });
            }
        }

        [HttpPost("start/{prodId:int}")]
        public async Task<IActionResult> StartProductionByProdId(
    int prodId,
    CancellationToken ct)
        {
            try
            {
                var startedProdId = await _service.StartProductionAndPromoteFirstTaskByProdIdAsync(
                    prodId,
                    ct);

                if (!startedProdId.HasValue)
                {
                    return NotFound(new
                    {
                        message = "Production not found for this prodId.",
                        prod_id = prodId
                    });
                }

                return Ok(new
                {
                    message = "Production started successfully.",
                    prod_id = startedProdId.Value,
                    production_id = startedProdId.Value,
                    production_status = "InProcessing",
                    first_task_status = "Unassigned",
                    start_mode = "ManualReadyByProdId"
                });
            }
            catch (BomValidationException ex)
            {
                return BadRequest(new
                {
                    message = "Không thể bắt đầu sản xuất vì BOM còn dòng chưa map với id của NVL.",
                    prod_id = prodId,
                    missing_bom_lines = ex.Items
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    prod_id = prodId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Start production failed",
                    detail = ex.Message,
                    prod_id = prodId
                });
            }
        }

        [HttpPut("mark-importing/{prodId:int}")]
        public async Task<IActionResult> MarkProductionImportingByProdId(
    int prodId,
    CancellationToken ct)
        {
            try
            {
                var result = await _service.ForceSetProductionImportingByProdIdAsync(
                    prodId,
                    ct);

                if (result == null)
                {
                    return NotFound(new
                    {
                        message = "Production not found.",
                        prod_id = prodId
                    });
                }

                await _hub.Clients.All.SendAsync(
                    "update-ui",
                    new { message = "update UI" },
                    ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    prod_id = prodId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Mark production importing failed",
                    detail = ex.Message,
                    prod_id = prodId
                });
            }
        }

        [HttpPost("generate-import-receive")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(GenerateImportReceiveUrlResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GenerateImportReceive(
    [FromBody] GenerateImportReceiveRequest req,
    CancellationToken ct)
        {
            if (req == null || req.order_id <= 0)
            {
                return BadRequest(new
                {
                    message = "order_id is required"
                });
            }

            try
            {
                /*
                 * Service hiện tại đã:
                 * - lấy sources theo order_id
                 * - tạo PDF
                 * - upload Cloudinary
                 * - lưu import_recieve_path vào productions liên quan
                 * - trả import_recieve_path = cloudUrl
                 */
                var result = await _service.GenerateImportReceiveAsync(
                    req.order_id,
                    ct);

                if (result == null)
                {
                    return NotFound(new
                    {
                        message = "Production or order not found",
                        order_id = req.order_id
                    });
                }

                var fileUrl = result.import_recieve_path;

                if (string.IsNullOrWhiteSpace(fileUrl))
                {
                    return BadRequest(new
                    {
                        message = "Tạo phiếu nhập kho thành công nhưng không lấy được link Cloudinary.",
                        order_id = req.order_id
                    });
                }

                var notifyMessage =
                    $"Đơn hàng {req.order_id} đã tạo 1 phiếu nhập kho chung cho {result.total_productions} production, chờ nhập kho";

                await _hub.Clients.Group(RealtimeGroups.ByRole("warehouse manager")).SendAsync(
                    "Importing",
                    new
                    {
                        message = notifyMessage
                    },
                    ct);

                await _noti.CreateNotfi(
                    4,
                    notifyMessage,
                    null,
                    req.order_id,
                    "Importing");

                /*
                 * FIX CHÍNH:
                 * Trả JSON chứa link.
                 * Tuyệt đối không return File(...).
                 */
                return Ok(new GenerateImportReceiveUrlResponse
                {
                    success = result.success,

                    order_id = result.order_id,
                    order_code = result.order_code,

                    total_productions = result.total_productions,
                    generated_count = result.generated_count,

                    prod_ids = result.prod_ids ?? new List<int>(),

                    file_url = fileUrl,
                    import_recieve_path = fileUrl,
                    import_file = fileUrl,

                    files = (result.files ?? new List<GenerateImportReceiveResponse>())
                        .Select(x => new GenerateImportReceiveUrlFileItem
                        {
                            success = x.success,

                            prod_id = x.prod_id,
                            prod_ids = x.prod_ids ?? new List<int>(),

                            order_id = x.order_id,
                            order_code = x.order_code,

                            file_url = x.import_recieve_path,
                            import_recieve_path = x.import_recieve_path,
                            import_file = x.import_recieve_path,

                            total_productions_in_file = x.total_productions_in_file,
                            message = x.message
                        })
                        .ToList(),

                    message = result.message
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = req.order_id
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Generate import receive failed",
                    detail = ex.Message,
                    order_id = req.order_id
                });
            }
        }

        [HttpPost("upload-import-receive-file")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadImportReceiveFile(
    [FromForm] UploadImportReceiveFileRequest req,
    CancellationToken ct)
        {
            if (req == null || req.order_id <= 0)
            {
                return BadRequest(new
                {
                    message = "order_id is required"
                });
            }

            if (req.file == null || req.file.Length == 0)
            {
                return BadRequest(new
                {
                    message = "File không hợp lệ.",
                    order_id = req.order_id
                });
            }

            try
            {
                using var stream = req.file.OpenReadStream();

                var result = await _service.UploadImportReceiveFileAsync(
                    req.order_id,
                    stream,
                    req.file.FileName,
                    req.file.ContentType,
                    ct);

                if (result == null)
                {
                    return NotFound(new
                    {
                        message = "Production or order not found",
                        order_id = req.order_id
                    });
                }

                var notifyMessage =
                    $"Đơn hàng {req.order_id} đã upload phiếu nhập kho thành phẩm, chờ nhập kho.";

                await _hub.Clients.Group(RealtimeGroups.ByRole("warehouse manager")).SendAsync(
                    "Importing",
                    new
                    {
                        message = notifyMessage
                    },
                    ct);

                await _noti.CreateNotfi(
                    4,
                    notifyMessage,
                    null,
                    req.order_id,
                    "Importing");

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = req.order_id
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Upload import receive file failed",
                    detail = ex.Message,
                    order_id = req.order_id
                });
            }
        }

        [HttpPut("delivery/{orderId:int}")]
        public async Task<IActionResult> SetDelivery(int orderId, CancellationToken ct)
        {
            var deliveryEmailSent = false;
            string? deliveryEmailError = null;

            try
            {
                var ok = await _service.SetProductionDeliveryAsync(orderId, ct);

                if (!ok)
                {
                    return NotFound(new
                    {
                        message = "Production not found for this orderId",
                        order_id = orderId
                    });
                }

                try
                {
                    await _deliveryHandoverEmailJob.RunAsync(orderId, ct);
                    deliveryEmailSent = true;
                }
                catch (Exception ex)
                {
                    deliveryEmailError = ex.Message;

                    _logger.LogError(
                        ex,
                        "Failed to send delivery handover email. orderId={OrderId}",
                        orderId);
                }

                return Ok(new
                {
                    message = "Delivery confirmed successfully",
                    order_id = orderId,
                    delivery_handover_email_sent = deliveryEmailSent,
                    delivery_handover_email_error = deliveryEmailError
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = orderId,
                    delivery_handover_email_sent = deliveryEmailSent,
                    delivery_handover_email_error = deliveryEmailError
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Set delivery failed",
                    detail = ex.Message,
                    order_id = orderId,
                    delivery_handover_email_sent = deliveryEmailSent,
                    delivery_handover_email_error = deliveryEmailError
                });
            }
        }

        [HttpPut("competed/{orderId:int}")]
        public async Task<IActionResult> SetCompleted(int orderId, CancellationToken ct)
        {
            var ok = await _service.SetCompletedAsync(orderId, ct);

            if (!ok)
                return NotFound(new { message = "Production not found for this orderId" });

            return NoContent();
        }

        [HttpGet("machine-schedule")]
        public async Task<IActionResult> GetMachineSchedule(
    [FromQuery] DateTime? from,
    [FromQuery] DateTime? to,
    CancellationToken ct = default)
        {
            var now = AppTime.NowVnUnspecified();

            var rangeFrom = from ?? now.Date.AddDays(-7);
            var rangeTo = to ?? now.Date.AddDays(7);

            if (rangeTo <= rangeFrom)
                rangeTo = rangeFrom.AddDays(14);

            var data = await _service.GetMachineScheduleBoardAsync(rangeFrom, rangeTo, ct);

            return Ok(new
            {
                from = rangeFrom,
                to = rangeTo,
                machines = data
            });
        }

        private static string? NormalizeMethodForNotify(string? method)
        {
            if (string.IsNullOrWhiteSpace(method))
                return null;

            var value = method.Trim().ToUpperInvariant();

            return value is "NVL" or "SUB" or "BOTH"
                ? value
                : null;
        }

        private static string BuildMethodDisplayName(string? method)
        {
            var value = NormalizeMethodForNotify(method);

            return value switch
            {
                "NVL" => "NVL",
                "SUB" => "bán thành phẩm",
                "BOTH" => "kết hợp bán thành phẩm và NVL",
                _ => "không xác định"
            };
        }

        private async Task PublishAutoApprovedProductionAsync(
            int orderId,
            ProductionReadyCheckResponse state,
            CancellationToken ct)
        {
            var method = NormalizeMethodForNotify(state.production_method)
                ?? "UNKNOWN";

            var methodName = BuildMethodDisplayName(method);

            var message =
                $"Đơn {orderId} đã được tự động duyệt sản xuất bằng {methodName}.";

            var payload = new
            {
                event_type = "AUTO_APPROVED",
                approval_flow = "AUTO_SINGLE_OPTION",

                order_id = orderId,
                production_id = state.production_id,
                prod_id = state.production_id,

                production_method = method,
                is_production_ready = true,
                need_manager_approval = false,

                can_use_nvl = state.can_use_nvl,
                can_use_sub = state.can_use_sub,
                can_use_both = state.can_use_both,
                sub_product_issue_file = state.sub_product_issue_file,

                selected_sub_product_id = state.selected_sub_product_id,
                sub_product_used_qty = state.sub_product_used_qty,
                nvl_qty = state.nvl_qty,

                message
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager"))
                .SendAsync("approved-production", payload, ct);

            await _hub.Clients.All.SendAsync(
                "update-ui",
                new
                {
                    event_type = "AUTO_APPROVED",
                    order_id = orderId,
                    production_id = state.production_id,
                    message
                },
                ct);

            await _noti.CreateNotfi(
                6,
                message,
                null,
                orderId,
                "Scheduled");
        }

        private async Task PublishWaitingManagerApprovalAsync(
            int orderId,
            ProductionReadyCheckResponse? state,
            ConfirmProductionReadyRequest req,
            CancellationToken ct)
        {
            var proposedMethod = NormalizeMethodForNotify(
                state?.gm_proposed_method
                ?? state?.proposed_production_method
                ?? req.GetProposedMethod());

            var message = proposedMethod == null
                ? $"Có đơn {orderId} cần duyệt phương thức sản xuất."
                : $"Có đơn {orderId} cần duyệt phương thức sản xuất. GM đề xuất: {proposedMethod}.";

            var payload = new
            {
                event_type = "WAITING_MANAGER_APPROVAL",
                approval_flow = "MANUAL_MULTI_OPTION",

                order_id = orderId,
                production_id = state?.production_id,
                prod_id = state?.production_id,

                is_production_ready = false,
                need_manager_approval = true,

                gm_note = req.gm_note,
                gm_proposed_method = proposedMethod,
                proposed_production_method = proposedMethod,

                can_use_nvl = state?.can_use_nvl,
                can_use_sub = state?.can_use_sub,
                can_use_both = state?.can_use_both,

                order_quantity = state?.order_quantity,
                selected_sub_product_id = state?.selected_sub_product_id,
                sub_product_used_qty = state?.sub_product_used_qty,
                nvl_qty = state?.nvl_qty,

                message
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("manager"))
                .SendAsync("approve-production", payload, ct);

            await _hub.Clients.All.SendAsync(
                "update-ui",
                new
                {
                    event_type = "WAITING_MANAGER_APPROVAL",
                    order_id = orderId,
                    production_id = state?.production_id,
                    message
                },
                ct);

            await _noti.CreateNotfi(
                3,
                message,
                null,
                orderId,
                "Scheduled");
        }

        private async Task PublishProductionReadyCancelledAsync(
            int orderId,
            ProductionReadyCheckResponse? state,
            CancellationToken ct)
        {
            var message =
                $"Đơn {orderId} đã bị hủy trạng thái sẵn sàng sản xuất.";

            var payload = new
            {
                event_type = "READY_CANCELLED",
                approval_flow = "CANCELLED",

                order_id = orderId,
                production_id = state?.production_id,
                prod_id = state?.production_id,

                is_production_ready = false,
                need_manager_approval = false,

                message
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager"))
                .SendAsync("production-ready-cancelled", payload, ct);

            await _hub.Clients.Group(RealtimeGroups.ByRole("manager"))
                .SendAsync("production-ready-cancelled", payload, ct);

            await _hub.Clients.All.SendAsync(
                "update-ui",
                new
                {
                    event_type = "READY_CANCELLED",
                    order_id = orderId,
                    production_id = state?.production_id,
                    message
                },
                ct);
        }

        private async Task PublishManagerApprovedProductionAsync(
    int orderId,
    int? scheduledProdId,
    SetProductionMethodResponse result,
    CancellationToken ct)
        {
            var method = NormalizeMethodForNotify(result.production_method)
                ?? "UNKNOWN";

            var methodName = BuildMethodDisplayName(method);

            var productionId = scheduledProdId ?? result.prod_id;

            var message =
                $"Đơn {orderId} đã được manager duyệt sản xuất bằng {methodName}.";

            var payload = new
            {
                event_type = "MANAGER_APPROVED",
                approval_flow = "MANUAL_MULTI_OPTION",

                order_id = orderId,
                production_id = productionId,
                prod_id = productionId,

                production_method = method,
                is_production_ready = true,
                need_manager_approval = false,
                sub_product_issue_file = result.sub_product_issue_file,
                is_full_process = result.is_full_process,
                sub_product_id = result.sub_product_id,
                sub_product_used_qty = result.sub_product_used_qty,
                nvl_qty = result.nvl_qty,
                order_quantity = result.order_quantity,

                gm_note = result.gm_note,
                mgr_note = result.mgr_note,

                message
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager"))
                .SendAsync("approved-production", payload, ct);

            await _hub.Clients.Group(RealtimeGroups.ByRole("general manager"))
                .SendAsync("production-method-approved", payload, ct);

            await _hub.Clients.All.SendAsync(
                "update-ui",
                new
                {
                    event_type = "MANAGER_APPROVED",
                    order_id = orderId,
                    production_id = productionId,
                    message
                },
                ct);

            await _noti.CreateNotfi(
                6,
                message,
                null,
                orderId,
                "Scheduled");
        }

        [HttpGet("by-task/{taskId:int}")]
        public async Task<IActionResult> GetProductionsByTaskId(int taskId, CancellationToken ct = default)
        {
            if (taskId <= 0)
            {
                return BadRequest(new
                {
                    message = "task_id không hợp lệ.",
                    task_id = taskId
                });
            }

            try
            {
                var productions = await _service.GetProductionsByTaskIdAsync(
                    taskId,
                    ct);

                if (productions == null || productions.Count == 0)
                {
                    return NotFound(new
                    {
                        message = "Không tìm thấy production nào theo task_id này.",
                        task_id = taskId
                    });
                }

                return Ok(new
                {
                    task_id = taskId,
                    total_productions = productions.Count,
                    productions = productions
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    task_id = taskId
                });
            }
        }

        [HttpGet("customer-contract/order/{orderId:int}")]
        public async Task<IActionResult> GetCustomerContractByOrderId(
    int orderId,
    CancellationToken ct)
        {
            if (orderId <= 0)
            {
                return BadRequest(new
                {
                    message = "order_id không hợp lệ.",
                    order_id = orderId
                });
            }

            try
            {
                var result = await _service.GetCustomerContractByOrderIdAsync(
                    orderId,
                    ct);

                if (result == null)
                {
                    return NotFound(new
                    {
                        message = "Không tìm thấy order.",
                        order_id = orderId
                    });
                }

                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    order_id = orderId
                });
            }
        }

        public class UploadImportReceiveFileRequest
        {
            [Required]
            public int order_id { get; set; }

            [Required]
            public IFormFile file { get; set; } = null!;
        }
    }
}