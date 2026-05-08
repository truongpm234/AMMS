using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.Constants;
using AMMS.Shared.DTOs.PayOS;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderService _service;
        private readonly IMaterialPurchaseRequestService _materialPurchaseService;
        private readonly IDealService _dealService;
        private readonly ILogger<OrdersController> _logger;
        private readonly IPaymentsService _paymentsService;
        private readonly AppDbContext _db;
        private readonly IPayOsService _payos;
        private readonly IConfiguration _config;
        private readonly IHubContext<RealtimeHub> _hub;
        public OrdersController(IHubContext<RealtimeHub> hub, IOrderService service, IMaterialPurchaseRequestService materialPurchaseService, IDealService dealService, ILogger<OrdersController> logger,
            IPaymentsService paymentsService, AppDbContext appDbContext, IPayOsService payos, IConfiguration config)
        {
            _hub = hub;
            _service = service;
            _materialPurchaseService = materialPurchaseService;
            _dealService = dealService;
            _logger = logger;
            _paymentsService = paymentsService;
            _db = appDbContext;
            _payos = payos;
            _config = config;
        }

        [HttpGet("get-by-{code}")]
        public async Task<IActionResult> GetByCodeAsync(string code)
        {
            var order = await _service.GetOrderByCodeAsync(code);
            if (order == null)
            {
                return NotFound();
            }
            return Ok(order);
        }


        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            var result = await _service.GetPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpGet("detail/{id:int}")]
        public async Task<IActionResult> GetDetail(int id, CancellationToken ct)
        {
            var dto = await _service.GetDetailAsync(id);
            if (dto == null)
                return NotFound(new { message = "Order not found" });

            return Ok(dto);
        }

        [HttpPost("{orderId:int}/auto-purchase")]
        public async Task<IActionResult> CreateAutoPurchase(
            int orderId,
            [FromBody] AutoPurchaseFromOrderRequest req,
            CancellationToken ct)
        {
            try
            {
                var result = await _materialPurchaseService.CreateFromOrderAsync(
                    orderId,
                    req.ManagerId,
                    ct);

                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("missing-materials")]
        public async Task<IActionResult> GetAllMissingMaterials(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetAllMissingMaterialsAsync(page, pageSize, ct);
            return Ok(result);
        }

        [HttpDelete("delete-design-file-path")]
        public async Task<string> Delete(int orderRequestId)
        {
            return await _service.DeleteDesignFilePath(orderRequestId);
        }

        [HttpGet("/get-all-order-with-status-Inprocess")]
        public async Task<List<order>> GetAllOrderWithInProcess()
        {
            return await _service.GetAllOrderWithStatusInProcess();
        }

        [HttpPost("send-remaining-payment-email/{orderId:int}")]
        public async Task<IActionResult> SendRemainingPaymentEmail(int orderId, CancellationToken ct)
        {
            try
            {
                await _dealService.SendRemainingPaymentEmailAsync(orderId, ct);
                return Ok(new
                {
                    ok = true,
                    order_id = orderId,
                    message = "Đã gửi email yêu cầu thanh toán phần còn lại cho khách hàng."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { ok = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    ok = false,
                    message = ex.Message
                });
            }
        }

        [HttpGet("create-payos-remaining-link/{orderId:int}")]
        public async Task<IActionResult> GetRemainingPaymentLink(int orderId, CancellationToken ct)
        {
            try
            {
                var dto = await _dealService.CreateOrReuseRemainingPaymentLinkAsync(orderId, ct);
                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { ok = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    ok = false,
                    message = ex.Message
                });
            }
        }

        [AllowAnonymous]
        [HttpGet("payos/remaining-status-by-order-id")]
        public async Task<IActionResult> CheckRemainingPayOsStatusByOrderId(
[FromQuery] int order_id,
CancellationToken ct)
        {
            if (order_id <= 0)
                return BadRequest(new { message = "order_id is required" });

            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == order_id, ct);

            if (req == null)
                return NotFound(new { message = "Order request not found for order_id" });

            var pending = await _paymentsService.GetLatestPendingByRequestIdAndTypeAsync(
                req.order_request_id,
                PaymentTypes.Remaining,
                ct);

            var latest = pending ?? await _paymentsService.GetLatestByRequestIdAndTypeAsync(
                req.order_request_id,
                PaymentTypes.Remaining,
                ct);

            if (latest == null)
            {
                return Ok(new
                {
                    found = false,
                    paid = false,
                    should_call_return = false,
                    should_redirect_frontend = false,
                    message = "Remaining payment not found"
                });
            }

            var fe = GetFrontendBaseUrl();
            var backend = GetBackendBaseUrl();

            var frontendPaidUrl =
                $"{fe}/payment-success/{req.order_request_id}?payos=paid&orderCode={latest.order_code}";

            var returnApiUrl =
                $"{backend}/api/requests/payos/return?order_code={latest.order_code}&status=PAID";

            if (_dealService.IsPaidStatus(latest.status))
            {
                await _hub.Clients.All.SendAsync("Paid", new { message = "Paid" });
                return Ok(new
                {
                    found = true,
                    paid = true,
                    status = latest.status,
                    order_code = latest.order_code,
                    should_call_return = false,
                    should_redirect_frontend = true,
                    redirect_url = frontendPaidUrl,
                    message = "Payment already recorded in database"
                });
            }

            // Lấy thông tin từ PayOS
            PayOsResultDto? info = null;
            try
            {
                info = await _payos.GetPaymentLinkInformationAsync(latest.order_code, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Cannot get PayOS remaining payment info. OrderId={OrderId}, OrderCode={OrderCode}",
                    order_id, latest.order_code);
            }

            PayOsResultDto? saved = null;
            if (!string.IsNullOrWhiteSpace(latest.payos_raw))
            {
                try
                {
                    saved = PayOsRawMapper.FromPayment(latest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cannot parse saved payos_raw for remaining payment. OrderId={OrderId}, OrderCode={OrderCode}",
                        order_id, latest.order_code);
                }
            }

            var currentStatus = info?.status ?? saved?.status ?? latest.status ?? "PENDING";

            var isPaid =
                string.Equals(currentStatus, "PAID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (!isPaid)
            {
                return Ok(new
                {
                    found = true,
                    paid = false,
                    processed = false,
                    status = currentStatus,
                    order_code = latest.order_code,
                    checkout_url = info?.check_out_url ?? saved?.check_out_url,
                    qr_code = info?.qr_code ?? saved?.qr_code,
                    should_call_return = false,
                    should_redirect_frontend = false,
                    message = "Payment is not completed yet"
                });
            }

            return Ok(new
            {
                found = true,
                paid = true,
                processed = false,
                status = currentStatus,
                order_code = latest.order_code,
                should_call_return = true,
                should_redirect_frontend = false,
                return_api_url = returnApiUrl,
                message = "PayOS shows PAID. FE should navigate to return_api_url once."
            });
        }

        private string NormalizeBaseUrl(string? url, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            return value.TrimEnd('/');
        }

        private string GetFrontendBaseUrl()
            => NormalizeBaseUrl(_config["Deal:BaseUrlFe"], "https://sep490-fe.vercel.app");

        private string GetBackendBaseUrl()
            => NormalizeBaseUrl(_config["Deal:BaseUrl"], "https://amms-juaa.onrender.com");
    }
}
