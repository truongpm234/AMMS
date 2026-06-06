using AMMS.API.Jobs;
using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.PayOS;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AMMS.Shared.DTOs.Auth.Auth;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RequestsController : ControllerBase
    {
        private readonly IRequestService _service;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly IDealService _dealService;
        private readonly IPaymentsService _paymentService;
        private readonly IProductionSchedulingService _schedulingService;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly AppDbContext _db;
        private readonly ISmsOtpService _smsOtp;
        private readonly IConfiguration _config;
        private readonly IPayOsService _payos;
        private readonly ILogger<RequestsController> _logger;
        private readonly IHubContext<RealtimeHub> _rt;
        private readonly NotificationService _notiService;
        private readonly IServiceScopeFactory _scopeFactory;
        private static readonly ConcurrentDictionary<long, byte> _payosProcessingLocks = new();

        public RequestsController(
            IHubContext<RealtimeHub> hub,
    NotificationService notiService,
    IHubContext<RealtimeHub> rt,
    IRequestService service,
    IDealService dealService,
    IPaymentsService paymentService,
    AppDbContext db,
    IProductionSchedulingService schedulingService,
    ISmsOtpService smsOtp,
    IConfiguration config,
    IPayOsService payos,
    ILogger<RequestsController> logger,
    IServiceScopeFactory scopeFactory,
    IBackgroundJobClient backgroundJobClient)
        {
            _hub = hub;
            _notiService = notiService;
            _rt = rt;
            _service = service;
            _dealService = dealService;
            _paymentService = paymentService;
            _db = db;
            _schedulingService = schedulingService;
            _smsOtp = smsOtp;
            _config = config;
            _payos = payos;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _backgroundJobClient = backgroundJobClient;
        }

        [HttpPost("create-request-by-consultant")]
        [ProducesResponseType(typeof(CreateRequestResponse), StatusCodes.Status201Created)]
        public async Task<IActionResult> CreateOrderRequest([FromBody] CreateResquestConsultant dto)
        {
            var result = await _service.CreateRequestByConsultantAsync(dto);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpPost]
        [ProducesResponseType(typeof(CreateRequestResponse), StatusCodes.Status201Created)]
        public async Task<IActionResult> Create([FromBody] CreateResquest req)
        {
            var result = await _service.CreateAsync(req);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpPost("clone-request")]
        public async Task<IActionResult> CloneRequest([FromBody] CloneRequestDto dto, CancellationToken ct)
        {
            try
            {
                if (dto == null || dto.request_id <= 0)
                    return BadRequest(new { message = "request_id is required" });

                var result = await _service.CloneRequestAsync(dto.request_id, ct);

                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "An unexpected error occurred",
                    detail = ex.Message
                });
            }
        }

        [HttpPut("{id}")]
        [ProducesResponseType(typeof(UpdateRequestResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(UpdateRequestResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<UpdateRequestResponse>> UpdateAsync(int id, [FromBody] UpdateOrderRequest request)
        {
            var update = await _service.UpdateAsync(id, request);
            return StatusCode(StatusCodes.Status200OK, update);
        }

        [HttpPut("cancel-request")]
        public async Task<IActionResult> Delete([FromBody] CancelRequestDto dto, CancellationToken ct)
        {
            await _service.CancelAsync(dto.id, dto.reason, ct);
            return Ok(new { message = "Cancelled", order_request_id = dto.id });
        }

        [HttpGet("{id:int}")]
        [ProducesResponseType(typeof(RequestWithCostDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRequestById(int id, CancellationToken ct)
        {
            var requestDto = await _service.GetByIdWithCostAsync(id);

            if (requestDto == null)
                return NotFound(new { message = "Order request not found" });
            if (!requestDto.estimate_finish_date.HasValue)
            {
                requestDto.estimate_finish_date = await _service.RecalculateAndPersistAsync(id, ct);
            }
            return Ok(requestDto);
        }
        [HttpGet("get-cost-estimate/{order_id}")]
        public async Task<ActionResult<cost_estimate>> GetCostEstimateByOrderId(int order_id)
        {
            var order_req_id = await _db.order_requests.FirstOrDefaultAsync(o => o.order_id == order_id);
            if (order_req_id == null)
            {
                return NotFound("Không tìm thấy đơn hàng");
            }
            var res = await _db.cost_estimates.FirstOrDefaultAsync(c => c.order_request_id == order_req_id.order_request_id && c.is_active == true);
            return Ok(res);
        }

        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged([FromQuery] int page, [FromQuery] int pageSize)
        {
            var result = await _service.GetPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpGet("get-by-order-id/{orderId}")]
        public async Task<IActionResult> GetByOrderId(int orderId)
        {
            var result = await _service.GetByOrderIdAsync(orderId);
            if (result == null) return NotFound();

            return Ok(result);
        }

        [HttpPut("approval")]
        public async Task<IActionResult> Approval([FromBody] RequestApprovalUpdateDto dto, CancellationToken ct)
        {
            try
            {
                if (dto == null)
                    return BadRequest(new { message = "Request body is required" });

                if (dto.request_id <= 0)
                    return BadRequest(new { message = "request_id must be greater than 0" });

                if (string.IsNullOrWhiteSpace(dto.status))
                    return BadRequest(new { message = "status is required" });

                var validStatuses = new[] { "Verified", "Declined" };
                if (!validStatuses.Contains(dto.status, StringComparer.OrdinalIgnoreCase))
                    return BadRequest(new
                    {
                        message = $"Invalid status. Allowed values: {string.Join(", ", validStatuses)}"
                    });

                await _service.UpdateApprovalAsync(dto, ct);

                return Ok(new
                {
                    message = "Updated approval",
                    status = dto.status,
                    request_id = dto.request_id
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "An unexpected error occurred",
                    detail = ex.Message
                });
            }
        }

        [HttpPost("send-deal")]
        public async Task<IActionResult> SendDealEmail([FromBody] SendDealEmailRequest req)
        {
            try
            {
                await _dealService.SendDealAndEmailAsync(req.request_id);
                return Ok(new { message = "Báo giá đã được hệ thống gửi.", request_id = req.request_id });
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ SendDealEmail failed:");
                Console.WriteLine(ex);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Send email failed",
                    detail = ex.Message,
                    request_id = req.request_id
                });
            }
        }

        [HttpGet("accept-pay")]
        public async Task<IActionResult> AcceptPay([FromQuery] int orderRequestId, [FromQuery] string token)
        {
            var req = await _service.GetByIdAsync(orderRequestId);
            if (req == null)
                return NotFound(new { message = "Order request not found" });

            if (string.Equals(req.process_status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                var fe = "https://sep490-fe.vercel.app";
                return Redirect($"{fe}/deal-invalid?orderRequestId={orderRequestId}&reason=rejected");
            }

            var now = AppTime.NowVnUnspecified();
            var validation = ValidateQuotePaymentWindow(req, now);
            if (!validation.ok)
                return BadRequest(new { message = validation.message, orderRequestId });

            var checkoutUrl = await _dealService.AcceptAndCreatePayOsLinkAsync(orderRequestId);
            return Redirect(checkoutUrl);
        }

        [HttpPost("confirm-webhook")]
        public async Task<IActionResult> ConfirmWebhook(CancellationToken ct)
        {
            await _payos.ConfirmWebhookAsync(ct);
            return Ok(new { ok = true });
        }

        [AllowAnonymous]
        [HttpGet("payos/status-by-request-id")]
        public async Task<IActionResult> CheckPayOsStatus(
    [FromQuery] int request_id,
    [FromQuery] int estimate_id,
    [FromQuery] int? quote_id,
    CancellationToken ct)
        {
            if (request_id <= 0)
                return BadRequest(new { message = "request_id is required" });

            if (estimate_id <= 0)
                return BadRequest(new { message = "estimate_id is required" });

            var latest = await _paymentService.GetLatestByRequestIdAndEstimateIdAsync(request_id, estimate_id, ct);

            if (latest == null)
            {
                return Ok(new
                {
                    paid = false,
                    processed = false,
                    status = "NOT_FOUND",
                    order_code = 0L,
                    estimate_id = estimate_id,
                    quote_id = quote_id
                });
            }

            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == request_id, ct);

            var resolvedEstimateId = latest.estimate_id ?? estimate_id;
            var resolvedQuoteId = latest.quote_id ?? quote_id;
            var localStatus = NormalizePaymentStatus(latest.status);

            var finalized =
                req != null &&
                (
                    string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Paid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Completed", StringComparison.OrdinalIgnoreCase)
                );

            if (IsPaidLikeStatus(localStatus) && finalized)
            {
                return Ok(new
                {
                    paid = true,
                    processed = true,
                    status = "PAID",
                    order_code = latest.order_code,
                    estimate_id = resolvedEstimateId,
                    quote_id = resolvedQuoteId
                });
            }

            PayOsResultDto? info = null;
            string providerStatus = "UNKNOWN";

            try
            {
                info = await _payos.GetPaymentLinkInformationAsync(latest.order_code, ct);
                providerStatus = NormalizePaymentStatus(info?.status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CheckPayOsStatus failed to call PayOS. RequestId={RequestId}, OrderCode={OrderCode}",
                    request_id, latest.order_code);
            }

            var paid = IsPaidLikeStatus(localStatus) || IsPaidLikeStatus(providerStatus);

            if (paid)
            {
                await TryMarkRequestAcceptedLightweightAsync(
                    request_id,
                    resolvedEstimateId,
                    resolvedQuoteId,
                    ct);

                QueueProcessPaidInBackground(
                    orderRequestId: request_id,
                    orderCode: latest.order_code,
                    amount: info?.amount ?? Convert.ToInt64(latest.amount),
                    paymentLinkId: info?.payment_link_id ?? latest.payos_payment_link_id,
                    transactionId: info?.transaction_id ?? latest.payos_transaction_id,
                    rawJson: info?.raw_json ?? latest.payos_raw ?? "{}",
                    estimateId: resolvedEstimateId,
                    quoteId: resolvedQuoteId);

                return Ok(new
                {
                    paid = true,
                    processed = finalized,
                    status = "PAID",
                    order_code = latest.order_code,
                    estimate_id = resolvedEstimateId,
                    quote_id = resolvedQuoteId
                });
            }

            return Ok(new
            {
                paid = false,
                processed = false,
                status = providerStatus != "UNKNOWN" ? providerStatus : localStatus,
                order_code = latest.order_code,
                estimate_id = resolvedEstimateId,
                quote_id = resolvedQuoteId
            });
        }

        private static string NormalizePaymentStatus(string? status)
        {
            return string.IsNullOrWhiteSpace(status)
                ? "PENDING"
                : status.Trim().ToUpperInvariant();
        }

        private static bool IsPaidLikeStatus(string? status)
        {
            var normalized = NormalizePaymentStatus(status);
            return normalized == "PAID" || normalized == "SUCCESS";
        }
        private async Task TryMarkRequestAcceptedLightweightAsync(
    int requestId,
    int? estimateId,
    int? quoteId,
    CancellationToken ct)
        {
            var req = await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == requestId, ct);

            if (req == null)
                return;

            if (string.Equals(req.process_status, "Rejected", StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(req.process_status, "Paid", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(req.process_status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                req.process_status = "Accepted";
            }

            if (!req.accepted_estimate_id.HasValue && estimateId.GetValueOrDefault() > 0)
                req.accepted_estimate_id = estimateId!.Value;

            if (!req.quote_id.HasValue && quoteId.GetValueOrDefault() > 0)
                req.quote_id = quoteId!.Value;

            await _hub.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("accepted", new { message = $"Yêu cầu #{req.order_request_id} đã được đặt cọc", id = req.order_request_id });
            await _notiService.CreateNotfi(2, $"Yêu cầu #{req.order_request_id} đã được đặt cọc", req.assigned_consultant, req.order_request_id, "accepted");
            await _db.SaveChangesAsync(ct);

        }

        private void QueueProcessPaidInBackground(
            int orderRequestId,
            long orderCode,
            long amount,
            string? paymentLinkId,
            string? transactionId,
            string rawJson,
            int? estimateId,
            int? quoteId)
        {
            if (!_payosProcessingLocks.TryAdd(orderCode, 0))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var finalizeService = scope.ServiceProvider.GetRequiredService<IPaymentsService>();

                    var (ok, message) = await finalizeService.ProcessPaidAsync(
                        orderRequestId: orderRequestId,
                        orderCode: orderCode,
                        amount: amount,
                        paymentLinkId: paymentLinkId,
                        transactionId: transactionId,
                        rawJson: rawJson,
                        estimateIdFromQuery: estimateId,
                        quoteIdFromQuery: quoteId,
                        ct: CancellationToken.None);

                    if (!ok)
                    {
                        _logger.LogError(
                            "Background ProcessPaidAsync failed. RequestId={RequestId}, OrderCode={OrderCode}, Message={Message}",
                            orderRequestId, orderCode, message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Background ProcessPaidAsync crashed. RequestId={RequestId}, OrderCode={OrderCode}",
                        orderRequestId, orderCode);
                }
                finally
                {
                    _payosProcessingLocks.TryRemove(orderCode, out _);
                }
            });
        }

        [HttpPost("reject")]
        public async Task<IActionResult> RejectDeal([FromBody] RejectDealRequest dto, CancellationToken ct)
        {
            // 1) Lấy order_request
            var req = await _service.GetByIdAsync(dto.order_request_id);
            if (req == null)
                return NotFound(new { message = "Order request not found" });

            // 2) chanự reject nếu accept rồi
            if (string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Order has already been accepted, cannot reject." });

            // 3) Validate OTP
            if (string.IsNullOrWhiteSpace(dto.otp))
                return BadRequest(new { message = "OTP is required to reject this deal." });

            // 4) Xác định phone để verify OTP
            var phone = dto.phone;
            if (string.IsNullOrWhiteSpace(phone))
                phone = req.customer_phone ?? "";

            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { message = "Customer phone is missing, cannot verify OTP." });

            // 5) Verify OTP qua SMS
            var verifyReq = new VerifyOtpSmsRequest(phone, dto.otp);

            var verifyRes = await _smsOtp.VerifyOtpAsync(verifyReq, ct);
            if (!verifyRes.success || !verifyRes.valid)
                return BadRequest(new { message = verifyRes.message ?? "Invalid or expired OTP" });

            // 6) Update status + gửi mail consultant
            await _dealService.RejectDealAsync(dto.order_request_id, dto.reason ?? "Customer rejected");

            return Ok(new { ok = true });
        }


        [HttpGet("payos/return")]
        public async Task<IActionResult> PayOsReturn(
    [FromQuery(Name = "order_code")] long? orderCode,
    [FromQuery] string? status,
    [FromServices] IPaymentRepository paymentRepo,
    CancellationToken ct)
        {
            var fe = _config["Deal:BaseUrlFe"];

            try
            {
                long oc = orderCode ?? 0;

                if (oc <= 0 &&
                    long.TryParse(Request.Query["orderCode"], out var legacyOrderCode))
                {
                    oc = legacyOrderCode;
                }

                if (oc <= 0)
                    return Redirect($"{fe}/payment-result?payos=invalid_order_code");

                var payment = await paymentRepo.GetByOrderCodeAsync(oc, ct);
                if (payment == null)
                    return Redirect($"{fe}/payment-result?payos=payment_not_found&orderCode={oc}");

                var req = await _service.GetByIdAsync(payment.order_request_id);
                if (req == null)
                    return Redirect($"{fe}/payment-result?payos=request_not_found&orderCode={oc}");

                var paidByQuery =
                    string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

                PayOsResultDto? info = null;
                try
                {
                    info = await HttpContext.RequestServices
                        .GetRequiredService<IPayOsService>()
                        .GetPaymentLinkInformationAsync(oc, ct);
                }
                catch
                {
                }

                var paidByApi =
                    string.Equals(info?.status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info?.status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

                var isPaid = paidByApi || paidByQuery;

                if (isPaid)
                {
                    await _paymentService.ProcessPaidAsync(
                        orderRequestId: payment.order_request_id,
                        orderCode: oc,
                        amount: info?.amount ?? (long)payment.amount,
                        paymentLinkId: info?.payment_link_id ?? payment.payos_payment_link_id,
                        transactionId: info?.transaction_id ?? payment.payos_transaction_id,
                        rawJson: info?.raw_json ?? payment.payos_raw ?? "{}",
                        estimateIdFromQuery: payment.estimate_id,
                        quoteIdFromQuery: payment.quote_id,
                        ct: ct);
                }

                if (string.Equals(payment.payment_type, "Remaining", StringComparison.OrdinalIgnoreCase) && req.order_id.HasValue)
                {
                    return Redirect($"{fe}/request-detail/{payment.order_request_id}?payos={(isPaid ? "paid" : "pending")}&orderCode={oc}");
                }

                return Redirect($"{fe}/request-detail/{payment.order_request_id}?payos={(isPaid ? "paid" : "pending")}&orderCode={oc}");
            }
            catch (Exception ex)
            {
                var msg = Uri.EscapeDataString(ex.Message ?? "unknown");
                return Redirect($"{fe}/payment-result?payos=error&message={msg}");
            }
        }

        [HttpGet("payos/cancel")]
        public IActionResult PayOsCancel([FromQuery] int orderRequestId, [FromQuery] long orderCode)
        {
            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}");
        }

        [AllowAnonymous]
        [HttpPost("/api/payos/webhook")]
        public async Task<IActionResult> PayOsWebhook(
    [FromBody] JsonElement raw,
    [FromServices] IPaymentRepository paymentRepo,
    CancellationToken ct)
        {
            try
            {
                if (!raw.TryGetProperty("data", out var dataNode))
                    return Ok(new { ok = true, ignored = true, reason = "missing_data" });

                var rootCode = raw.TryGetProperty("code", out var rc) ? (rc.GetString() ?? "") : "";
                var rootSuccess = raw.TryGetProperty("success", out var rs) && rs.ValueKind == JsonValueKind.True;

                var dataCode = dataNode.TryGetProperty("code", out var dc) ? (dc.GetString() ?? "") : "";
                var dataStatus = dataNode.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
                var dataDesc = dataNode.TryGetProperty("desc", out var dd) ? (dd.GetString() ?? "") : "";

                var isPaid =
                    rootSuccess &&
                    string.Equals(rootCode, "00", StringComparison.OrdinalIgnoreCase) &&
                    (
                        string.Equals(dataCode, "00", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dataStatus, "PAID", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(dataStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                        dataDesc.Contains("thành công", StringComparison.OrdinalIgnoreCase)
                    );

                if (!isPaid)
                    return Ok(new { ok = true, ignored = true, rootCode, dataCode, dataStatus, dataDesc });

                var checksumKey = _config["PayOS:ChecksumKey"];
                if (!string.IsNullOrWhiteSpace(checksumKey))
                {
                    var signature = raw.TryGetProperty("signature", out var sig) ? (sig.GetString() ?? "") : "";
                    if (!IsValidPayOsSignature(dataNode, signature, checksumKey))
                        return Ok(new { ok = true, ignored = true, reason = "invalid_signature" });
                }

                long orderCode =
                    dataNode.TryGetProperty("orderCode", out var oc) && oc.ValueKind == JsonValueKind.Number
                        ? oc.GetInt64()
                        : 0;

                long amount =
                    dataNode.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number
                        ? am.GetInt64()
                        : 0;

                var paymentLinkId = dataNode.TryGetProperty("paymentLinkId", out var pl) ? pl.GetString() : null;
                var transactionId = dataNode.TryGetProperty("reference", out var rf) ? rf.GetString() : null;

                if (orderCode <= 0)
                    return Ok(new { ok = true, ignored = true, reason = "invalid_orderCode" });

                var payment = await paymentRepo.GetByOrderCodeAsync(orderCode, ct);
                if (payment == null)
                    return Ok(new { ok = true, ignored = true, reason = "payment_not_found" });

                var (processed, message) = await _paymentService.ProcessPaidAsync(
                    payment.order_request_id,
                    orderCode,
                    amount,
                    paymentLinkId,
                    transactionId,
                    raw.ToString(),
                    payment.estimate_id,
                    payment.quote_id,
                    ct);

                if (!processed)
                {
                    _logger.LogError(
                        "PayOsWebhook processed=false. RequestId={RequestId}, OrderCode={OrderCode}, Message={Message}, Raw={Raw}",
                        payment.order_request_id, orderCode, message, raw.ToString());
                }

                return Ok(new
                {
                    ok = true,
                    processed,
                    message,
                    orderRequestId = payment.order_request_id,
                    orderCode
                });
            }
            catch (Exception ex)
            {
                return Ok(new { ok = true, processed = false, error = ex.Message });
            }
        }

        private static bool IsValidPayOsSignature(JsonElement dataNode, string signature, string checksumKey)
        {
            var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var p in dataNode.EnumerateObject())
            {
                string val = p.Value.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.Undefined => "",
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText()
                };

                if (val is "null" or "undefined") val = "";
                dict[p.Name] = val;
            }

            var dataStr = string.Join("&", dict.Select(kv => $"{kv.Key}={kv.Value}"));
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataStr));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            return string.Equals(hex, signature ?? "", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet("stats/email/accepted")]
        public async Task<IActionResult> GetEmailStatsByAccepted(
            [FromQuery] int page,
            [FromQuery] int pageSize,
            CancellationToken ct)
        {
            var result = await _service.GetEmailsByAcceptedCountPagedAsync(page, pageSize, ct);
            return Ok(result);
        }

        [HttpGet("design-file/{id:int}")]
        public async Task<IActionResult> GetDesignFile(int id, CancellationToken ct)
        {
            var result = await _service.GetDesignFileAsync(id, ct);
            if (result == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(result);
        }

        [AllowAnonymous]
        [HttpGet("payos-deposit/{request_id:int}")]
        public async Task<IActionResult> GetPayOsDeposit(
    [FromRoute] int request_id,
    [FromQuery] int? quote_id,
    [FromQuery] int estimate_id,
    CancellationToken ct)
        {
            try
            {
                if (request_id <= 0)
                    return BadRequest(new { message = "request_id is required" });

                if (estimate_id <= 0)
                    return BadRequest(new { message = "estimate_id is required" });

                var req = await _db.order_requests
                    .FirstOrDefaultAsync(x => x.order_request_id == request_id, ct);

                if (req == null)
                    return NotFound(new { message = "Request not found" });

                if (string.Equals(req.process_status, "Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        message = "Request has been rejected. Payment link is not available.",
                        request_id
                    });
                }

                if (string.Equals(req.process_status, "Cancel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        message = "Request has been cancelled. Payment link is not available.",
                        request_id
                    });
                }

                var est = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == estimate_id &&
                        x.order_request_id == request_id &&
                        x.is_active,
                        ct);

                if (est == null)
                    return BadRequest(new { message = "Active estimate not found for this request" });

                var prepared = await PrepareDepositPaymentDataAsync(
                    req,
                    est,
                    quote_id,
                    ct);

                if (!prepared.ok)
                {
                    return BadRequest(new
                    {
                        message = prepared.message,
                        request_id,
                        estimate_id,
                        quote_id,
                        request_quote_id = req.quote_id,
                        request_accepted_estimate_id = req.accepted_estimate_id,
                        verified_at = req.verified_at,
                        quote_expires_at = req.quote_expires_at
                    });
                }

                var dto = await _dealService.CreateOrReuseDepositLinkAsync(
                    requestId: request_id,
                    estimateId: estimate_id,
                    quoteId: prepared.quote_id,
                    ct: ct);

                dto.expired_at = req.quote_expires_at;
                dto.status ??= "PENDING";

                return Ok(dto);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    request_id,
                    estimate_id,
                    quote_id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetPayOsDeposit failed. RequestId={RequestId}, EstimateId={EstimateId}, QuoteId={QuoteId}",
                    request_id,
                    estimate_id,
                    quote_id);

                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Create PayOS deposit link failed",
                    detail = ex.Message,
                    request_id,
                    estimate_id,
                    quote_id
                });
            }
        }

        [HttpGet("full-data-by-request_id/{request_id:int}")]
        [ProducesResponseType(typeof(RequestDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetFullDataByRequestId(int request_id, CancellationToken ct)
        {
            var result = await _service.GetInformationRequestById(request_id, ct);
            if (result == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(result);
        }

        [HttpPut("submit-estimate-for-approval")]
        public async Task<IActionResult> SubmitEstimateForApproval([FromBody] SubmitForApprovalRequestDto dto)
        {
            try
            {
                if (dto == null || dto.request_id <= 0)
                    return BadRequest(new { message = "request_id is required" });

                await _service.SubmitEstimateForApprovalAsync(dto);

                return Ok(new
                {
                    message = "Submitted for approval",
                    request_id = dto.request_id,
                    new_status = "Processing"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("get-deal-price-inProcess/{request_id:int}")]
        [ProducesResponseType(typeof(RequestWithTwoEstimatesDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCompareQuotes(int request_id, CancellationToken ct)
        {
            var dto = await _service.GetCompareQuotesAsync(request_id, ct);
            if (dto == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(dto);
        }

        [HttpPut("consultant-message-to-customer")]
        public async Task<IActionResult> UpdateConsultantMessageToCustomer([FromBody] UpdateConsultantMessageToCustomerDto dto, CancellationToken ct)
        {
            try
            {
                if (dto == null)
                    return BadRequest(new { message = "Request body is required" });

                if (dto.request_id <= 0)
                    return BadRequest(new { message = "request_id must be greater than 0" });

                await _service.UpdateConsultantMessageToCustomerAsync(dto.request_id, dto.message, ct);

                return Ok(new
                {
                    message = "Updated consultant message to customer successfully",
                    request_id = dto.request_id
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "An unexpected error occurred",
                    detail = ex.Message
                });
            }
        }

        private static bool IsPayableStatus(string? status)
        {
            return string.Equals(status, "Verified", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Waiting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase);
        }

        private static (bool ok, string message) ValidateQuotePaymentWindow(order_request req, DateTime now)
        {
            if (!IsPayableStatus(req.process_status))
                return (false, "Only request with process_status is verified or waiting can start payment");

            if (!req.quote_expires_at.HasValue)
                return (false, "Quote expiry time has not been initialized");

            if (now > req.quote_expires_at.Value)
                return (false, $"Quote expired at {req.quote_expires_at:yyyy-MM-dd HH:mm:ss}");

            return (true, "");
        }

        [HttpPut("{id:int}/delivery-note")]
        public async Task<IActionResult> UpdateDeliveryNote(int id, [FromBody] UpdateDeliveryNoteRequest req, CancellationToken ct)
        {
            var ok = await _service.UpdateDeliveryNoteAsync(id, req.delivery_note, ct);

            if (!ok)
                return NotFound(new { message = "OrderRequest not found" });

            return NoContent();
        }
        [HttpPut("designer-confirm-layout")]
        public async Task<IActionResult> DesignerConfirmLayout(
    [FromBody] ConfirmLayoutRequestDto dto,
    CancellationToken ct)
        {
            try
            {
                if (dto == null || dto.request_id <= 0)
                    return BadRequest(new { message = "request_id is required" });

                var current = await _service.GetByIdAsync(dto.request_id);
                if (current == null)
                    return NotFound(new { message = "Order request not found" });

                if (!current.order_id.HasValue)
                {
                    var convert = await _service.ConvertToOrderAsync(dto.request_id);
                    if (!convert.Success || !convert.OrderId.HasValue)
                    {
                        return BadRequest(new
                        {
                            message = convert.Message ?? "Order has not been created yet"
                        });
                    }
                }

                var strategy = _db.Database.CreateExecutionStrategy();
                int orderId = 0;

                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await _db.Database.BeginTransactionAsync(ct);

                    var req = await _service.GetRequestForUpdateAsync(dto.request_id, ct);

                    if (req == null)
                        throw new InvalidOperationException("Order request not found");

                    if (!req.order_id.HasValue || req.order_id.Value <= 0)
                        throw new InvalidOperationException("Order has not been created yet");

                    if (!string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Only paid/accepted requests can be layout-confirmed");

                    orderId = req.order_id.Value;

                    var ord = await _db.orders.FirstOrDefaultAsync(x => x.order_id == orderId, ct);
                    if (ord == null)
                        throw new InvalidOperationException("Order not found");

                    ord.layout_confirmed = true;
                    ord.status = "Pending";
                    ord.is_production_ready = false;

                    await _hub.Clients.Group(RealtimeGroups.ByRole("general manager")).SendAsync("confirm-layout", new { message = $"Có đơn {orderId} cần được kiểm duyệt sản xuất" });
                    await _notiService.CreateNotfi(18, $"Có đơn {orderId} cần được kiểm duyệt sản xuất", null, req.order_request_id, "Scheduled");
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                });

                _service.QueueRelease(orderId, autoApproveSingleMethod: true);
                return Accepted(new
                {
                    ok = true,
                    request_id = dto.request_id,
                    order_id = orderId,
                    message = "Designer confirmed layout. Auto method approval will run after production scheduling."
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    ok = false,
                    message = ex.Message
                });
            }
        }

        [HttpPost("upload-print-ready-file/{requestId:int}")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(200_000_000)]
        public async Task<IActionResult> UploadPrintReadyFile(
    [FromRoute] int requestId,
    [FromForm] UploadPrintReadyFileRequest request,
    CancellationToken ct)
        {
            try
            {
                if (request.File == null || request.File.Length <= 0)
                    return BadRequest(new { message = "File is required" });

                await using var stream = request.File.OpenReadStream();

                var url = await _service.UploadPrintReadyFileAsync(
                    requestId: requestId,
                    estimateId: request.estimate_id,
                    fileStream: stream,
                    fileName: request.File.FileName,
                    contentType: request.File.ContentType,
                    ct: ct);

                return Ok(new
                {
                    message = "Upload print_ready_file successfully",
                    request_id = requestId,
                    estimate_id = request.estimate_id,
                    file_name = request.File.FileName,
                    file_size = request.File.Length,
                    print_ready_file = url
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Upload print_ready_file failed",
                    detail = ex.Message
                });
            }
        }
        [HttpGet("notify-customer-pay")]
        public async Task<IActionResult> Notification(int id)
        {
            var res = await _db.order_requests.FirstOrDefaultAsync(o => o.order_request_id == id);
            if (res != null)
            {
                if (res?.process_status == "Accepted")
                {
                    //Khánh sửa signalr
                    return Ok(new { action = "Deposited" });
                }
                else if (res?.process_status == "Paid")
                {
                    //Khánh sửa signalr
                    await _rt.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("paid", new { message = $"Yêu cầu {id} đã được thanh toán " });
                    await _rt.Clients.Group(RealtimeGroups.ByRole("warehouse")).SendAsync("paid", new { message = $"Yêu cầu {id} đã được thanh toán " });
                    return Ok(new { action = "Full paid" });
                }
            }
            return NotFound("Not payment yet");
        }

        [HttpPut("confirm-importing")]
        public async Task<IActionResult> ConfirmImportProduction([FromBody] int order_id)
        {
            try
            {
                var ord = await _db.orders.FirstOrDefaultAsync(o => o.order_id == order_id);
                var req = await _db.order_requests.FirstOrDefaultAsync(r => r.order_id == order_id);
                var prod = await _db.productions.FirstOrDefaultAsync(p => p.order_id == order_id);
                if (ord != null && req != null && prod != null)
                {
                    ord.status = "Finished";
                    req.process_status = "Finished";
                    prod.status = "Finished";
                    await _db.SaveChangesAsync();
                    await _rt.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync("importing", new { message = $"Đơn hàng {order_id} đã được nhập kho, sẵn sàng giao" });
                    await _notiService.CreateNotfi(2, $"Đơn hàng {order_id} đã được nhập kho, sẵn sàng giao", req.assigned_consultant, req.order_request_id, "Finished");
                    await _dealService.SendRemainingPaymentEmailAsync(order_id);
                    return Ok("Success");
                }
            }
            catch (Exception e)
            {
                return BadRequest(e);
            }
            return BadRequest();
        }

        [HttpPut("customer-receive")]
        public async Task<IActionResult> ReceiveOrder(int request_id)
        {
            var res = await _db.order_requests.FirstOrDefaultAsync(o => o.order_request_id == request_id);
            if (res != null)
            {
                var ord = await _db.orders.FirstOrDefaultAsync(o => o.order_id == res.order_id);
                var pro = await _db.productions.FirstOrDefaultAsync(p => p.order_id == res.order_id);
                res.process_status = "Completed";
                if (ord != null && pro != null)
                {
                    ord.status = "Completed";
                    pro.status = "Completed";
                    await _db.SaveChangesAsync();
                }
                return Ok("Customer confirm receive order");
            }
            return BadRequest();
        }

        [Authorize(Policy = "customer")]
        [HttpGet("get-requests-and-order-by-login")]
        public async Task<IActionResult> GetMyRequests(
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 10,
    CancellationToken ct = default)
        {
            try
            {
                var request = await _service.GetMyRequestsByJwtAsync(page, pageSize, ct);
                var order = await _service.GetMyOrdersByJwtAsync(page, pageSize, ct);

                return Ok(new { requests = request, orders = order });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("fake-payment-success/{orderRequestId:int}")]
        public async Task<IActionResult> FakePaymentSuccess(
            int orderRequestId,
            CancellationToken ct)
        {
            try
            {

                var req = await _db.order_requests
                    .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

                if (req == null)
                    return NotFound(new { message = "Request not found" });

                if (string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        message = "Request already accepted"
                    });
                }

                // Lấy payment pending mới nhất
                var payment = await _db.payments
                    .Where(x =>
                        x.order_request_id == orderRequestId &&
                        x.provider == "PAYOS" &&
                        x.status == "PENDING")
                    .OrderByDescending(x => x.payment_id)
                    .FirstOrDefaultAsync(ct);

                if (payment == null)
                {
                    return BadRequest(new
                    {
                        message = "No pending payment found"
                    });
                }

                // Fake raw webhook json
                var fakeRaw = JsonSerializer.Serialize(new
                {
                    code = "00",
                    success = true,
                    data = new
                    {
                        orderCode = payment.order_code,
                        amount = payment.amount,
                        status = "PAID",
                        code_internal = "00",
                        desc = "Thanh toán thành công"
                    }
                });

                var result = await _paymentService.ProcessPaidAsync(
                    orderRequestId: orderRequestId,
                    orderCode: payment.order_code,
                    amount: (long)payment.amount,
                    paymentLinkId: payment.payos_payment_link_id ?? $"FAKE_LINK_{payment.order_code}",
                    transactionId: $"FAKE_TXN_{Guid.NewGuid():N}",
                    rawJson: fakeRaw,
                    estimateIdFromQuery: payment.estimate_id,
                    quoteIdFromQuery: payment.quote_id,
                    ct: ct);

                return Ok(new
                {
                    success = result.ok,
                    message = result.message,
                    request_id = orderRequestId,
                    order_code = payment.order_code
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = ex.Message
                });
            }
        }

        private async Task<(bool ok, string message, int? quote_id)> PrepareDepositPaymentDataAsync(
    order_request req,
    cost_estimate est,
    int? inputQuoteId,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            if (!IsPayableStatus(req.process_status))
            {
                return (false,
                    $"Only request with process_status Verified, Waiting or Accepted can create payment link. Current={req.process_status}",
                    null);
            }

            if (!req.verified_at.HasValue)
                req.verified_at = now;

            if (!req.quote_expires_at.HasValue)
                req.quote_expires_at = req.verified_at.Value.AddDays(7);

            if (now > req.quote_expires_at.Value)
            {
                return (false,
                    $"Quote expired at {req.quote_expires_at:yyyy-MM-dd HH:mm:ss}",
                    null);
            }

            var quote = await ResolveOrCreateQuoteForDepositAsync(
                req,
                est,
                inputQuoteId,
                ct);

            if (quote == null)
            {
                return (false,
                    "Cannot resolve or create quote for this payment",
                    null);
            }

            req.quote_id = quote.quote_id;
            req.accepted_estimate_id = est.estimate_id;

            if (string.Equals(req.process_status, "Verified", StringComparison.OrdinalIgnoreCase))
                req.process_status = "Waiting";

            await _db.SaveChangesAsync(ct);

            if (!req.quote_id.HasValue || req.quote_id.Value <= 0)
                return (false, "order_request.quote_id is missing after sync", null);

            if (!req.accepted_estimate_id.HasValue || req.accepted_estimate_id.Value <= 0)
                return (false, "order_request.accepted_estimate_id is missing after sync", null);

            if (!req.verified_at.HasValue)
                return (false, "order_request.verified_at is missing after sync", null);

            if (!req.quote_expires_at.HasValue)
                return (false, "order_request.quote_expires_at is missing after sync", null);

            return (true, "", quote.quote_id);
        }

        private async Task<quote?> ResolveOrCreateQuoteForDepositAsync(
            order_request req,
            cost_estimate est,
            int? inputQuoteId,
            CancellationToken ct)
        {
            quote? q = null;

            /*
             * 1. Ưu tiên quote_id.
             */
            if (inputQuoteId.HasValue && inputQuoteId.Value > 0)
            {
                q = await _db.quotes
                    .FirstOrDefaultAsync(x =>
                        x.quote_id == inputQuoteId.Value &&
                        x.order_request_id == req.order_request_id &&
                        x.estimate_id == est.estimate_id,
                        ct);
            }

            /*
             * 2. Nếu order_request.quote_id có data thì dùng, nhưng phải match estimate.
             */
            if (q == null &&
                req.quote_id.HasValue &&
                req.quote_id.Value > 0)
            {
                q = await _db.quotes
                    .FirstOrDefaultAsync(x =>
                        x.quote_id == req.quote_id.Value &&
                        x.order_request_id == req.order_request_id &&
                        x.estimate_id == est.estimate_id,
                        ct);
            }

            /*
             * 3. Tìm quote theo request_id + estimate_id.
             */
            q ??= await _db.quotes
                .Where(x =>
                    x.order_request_id == req.order_request_id &&
                    x.estimate_id == est.estimate_id)
                .OrderByDescending(x => x.quote_id)
                .FirstOrDefaultAsync(ct);

            if (q != null)
                return q;

            /*
             * 4. Nếu chưa có quote, tạo quote mới.
             */
            q = new quote
            {
                order_request_id = req.order_request_id,
                estimate_id = est.estimate_id,
                total_amount = est.final_total_cost,
                status = "Sent",
                created_at = AppTime.NowVnUnspecified()
            };

            await _db.quotes.AddAsync(q, ct);
            await _db.SaveChangesAsync(ct);

            return q;
        }
    }
}
