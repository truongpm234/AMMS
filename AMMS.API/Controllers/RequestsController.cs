using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.PayOS;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.DTOs.Requests.AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        private readonly IDealService _dealService;
        private readonly IPaymentsService _paymentService;
        private readonly IProductionSchedulingService _schedulingService;
        private readonly AppDbContext _db;
        private readonly ISmsOtpService _smsOtp;
        private readonly IConfiguration _config;
        private readonly IPaymentsService _payment;
        public RequestsController(
            IRequestService service,
            IDealService dealService,
            IPaymentsService paymentService,
            AppDbContext db,
            IProductionSchedulingService schedulingService,
            ISmsOtpService smsOtp,
            IConfiguration config, IPaymentsService payment)
        {
            _service = service;
            _dealService = dealService;
            _paymentService = paymentService;
            _db = db;
            _schedulingService = schedulingService;
            _smsOtp = smsOtp;
            _config = config;
            _payment = payment;
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
        public async Task<IActionResult> GetRequestById(int id)
        {
            var requestDto = await _service.GetByIdWithCostAsync(id);

            if (requestDto == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(requestDto);
        }

        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged([FromQuery] int page, [FromQuery] int pageSize)
        {
            var result = await _service.GetPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpPost("send-deal")]
        public async Task<IActionResult> SendDealEmail([FromBody] SendDealEmailRequest req)
        {
            try
            {
                await _dealService.SendDealAndEmailAsync(req.RequestId);
                return Ok(new { message = "Sent deal email", orderRequestId = req.RequestId });
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ SendDealEmail failed:");
                Console.WriteLine(ex);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Send email failed",
                    detail = ex.Message,
                    orderRequestId = req.RequestId
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

            var checkoutUrl = await _dealService.AcceptAndCreatePayOsLinkAsync(orderRequestId);
            return Redirect(checkoutUrl);
        }

        [HttpGet("payos/status-by-request-id")]
        public async Task<IActionResult> GetPayOsStatusByRequest([FromQuery] int order_request_id, [FromServices] IPaymentRepository paymentRepo, CancellationToken ct)
        {
            if (order_request_id <= 0)
                return BadRequest(new { message = "order_request_id is required" });

            var latest = await paymentRepo.GetLatestByRequestIdAsync(order_request_id, ct);

            if (latest == null)
            {
                return Ok(new
                {
                    paid = false,
                    status = "PENDING",
                    order_request_id,
                    order_code = (long?)null,
                    paid_at = AppTime.NowVnUnspecified()
                });
            }

            var isPaid = string.Equals(latest.status, "PAID", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(latest.status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            return Ok(new
            {
                paid = isPaid,
                status = latest.status,
                order_request_id = latest.order_request_id,
                order_code = latest.order_code,
                paid_at = latest.paid_at
            });
        }

        //[HttpGet("reject-form")]
        //public async Task<IActionResult> RejectForm([FromQuery] int orderRequestId, [FromQuery] string token)
        //{
        //    var fe = "https://sep490-fe.vercel.app";

        //    var req = await _service.GetByIdAsync(orderRequestId);
        //    if (req == null)
        //    {
        //        return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}&error=not_found");
        //    }

        //    if (string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
        //    {
        //        return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}&error=accepted");
        //    }

        //    return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}");
        //}

        [HttpPost("reject")]
        public async Task<IActionResult> RejectDeal([FromBody] RejectDealRequest dto, CancellationToken ct)
        {
            // 1) Lấy order_request
            var req = await _service.GetByIdAsync(dto.order_request_id);
            if (req == null)
                return NotFound(new { message = "Order request not found" });

            // 2) Không cho reject nếu đã Accepted
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

            // 5) Verify OTP qua SMS service
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
    [FromQuery] int request_id,
    [FromQuery] long order_code,
    [FromQuery] string? status,
    [FromQuery] long? orderCode,
    [FromServices] IPaymentRepository paymentRepo,
    CancellationToken ct)
        {
            var fe = _config["Deal:BaseUrlFe"] ?? "https://sep490-fe.vercel.app";

            try
            {
                var oc = order_code > 0 ? order_code : (orderCode ?? 0);
                if (oc <= 0)
                    return Redirect($"{fe}/request-detail/{request_id}?payos=invalid_order_code");

                // paid from query
                var paidByQuery =
                    string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

                PayOsResultDto? info = null;
                try
                {
                    info = await HttpContext.RequestServices.GetRequiredService<IPayOsService>()
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
                    long amount = info?.amount ?? 0;
                    string rawJson = info?.raw_json ?? "{}";
                    string? paymentLinkId = info?.payment_link_id;
                    string? transactionId = info?.transaction_id;

                    if (amount <= 0 || rawJson == "{}")
                    {
                        var pending = await _paymentService.GetLatestPendingByRequestIdAsync(request_id, ct);
                        if (pending != null && !string.IsNullOrWhiteSpace(pending.payos_raw))
                        {
                            var dto = PayOsRawMapper.FromPayment(pending);
                            amount = dto.amount ?? (long)(pending.amount);
                            rawJson = pending.payos_raw!;
                            paymentLinkId ??= pending.payos_payment_link_id;
                            transactionId ??= pending.payos_transaction_id;
                        }
                    }

                    await ProcessPaidAsync(request_id, oc, amount, paymentLinkId, transactionId, rawJson, paymentRepo, ct);
                }

                return Redirect($"{fe}/request-detail/{request_id}?payos={(isPaid ? "paid" : "pending")}&orderCode={oc}");
            }
            catch (Exception ex)
            {
                var msg = Uri.EscapeDataString(ex.Message ?? "unknown");
                return Redirect($"{fe}/request-detail/{request_id}?payos=error&message={msg}");
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

                var orderRequestId = (int)(orderCode / 10);

                var (processed, message) = await ProcessPaidAsync(
                    orderRequestId,
                    orderCode,
                    amount,
                    paymentLinkId,
                    transactionId,
                    raw.ToString(),
                    paymentRepo,
                    ct);

                return Ok(new { ok = true, processed, message, orderRequestId, orderCode });
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
        private async Task<(bool ok, string message)> ProcessPaidAsync(
    int orderRequestId,
    long orderCode,
    long amount,
    string? paymentLinkId,
    string? transactionId,
    string rawJson,
    IPaymentRepository paymentRepo,
    CancellationToken ct)
        {
            // 1) load request
            var req = await _db.order_requests
                .AsTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

            if (req == null)
                return (false, $"order_request_id={orderRequestId} not found");

            await UpsertPaidPaymentRowAsync(orderRequestId, orderCode, amount, paymentLinkId, transactionId, rawJson, paymentRepo, ct);

            // 3) nếu đã có order_id thì coi như done
            if (req.order_id != null)
                return (true, $"Already processed: order_id={req.order_id}");

            // 4) check quote expiry
            var est = await _db.cost_estimates.AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId)
                .OrderByDescending(x => x.created_at)
                .FirstOrDefaultAsync(ct);

            if (est == null)
                return (false, "Cost estimate not found");

            var expiredAt = est.created_at.AddHours(24);
            if (AppTime.NowVnUnspecified() > expiredAt.ToUniversalTime())
                return (false, $"Quote expired at {expiredAt:o}, ignore payment.");

            var now = AppTime.NowVnUnspecified();

            // 5) mark accepted
            await _dealService.MarkAcceptedAsync(orderRequestId);

            // 6) convert (BOM, is_enough, link req.order_id) — cần row lock ở ConvertToOrderAsync (phần D)
            var convert = await _service.ConvertToOrderAsync(orderRequestId);
            if (!convert.Success || convert.OrderId == null)
                return (false, "ConvertToOrder failed: " + convert.Message);

            // 7) schedule production
            var productTypeId = await _db.product_types.AsNoTracking()
                .Where(x => x.code == req.product_type)
                .Select(x => x.product_type_id)
                .FirstOrDefaultAsync(ct);

            if (productTypeId <= 0)
                return (false, "product_type invalid (cannot map product_type_id)");

            var item = await _db.order_items.AsNoTracking()
                .FirstOrDefaultAsync(x => x.item_id == convert.OrderItemId, ct);

            var prodId = await _schedulingService.ScheduleOrderAsync(
                orderId: convert.OrderId.Value,
                productTypeId: productTypeId,
                productionProcessCsv: item?.production_process,
                managerId: 3
            );

            try
            {
                await _dealService.NotifyConsultantPaidAsync(orderRequestId, (decimal)amount, now);
                await _dealService.NotifyCustomerPaidAsync(orderRequestId, (decimal)amount, now);
            }
            catch
            {
            }

            return (true, $"Processed OK: order_id={convert.OrderId}, prod_id={prodId}");
        }
        private async Task UpsertPaidPaymentRowAsync(
    int orderRequestId,
    long orderCode,
    long amount,
    string? paymentLinkId,
    string? transactionId,
    string rawJson,
    IPaymentRepository paymentRepo,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var existing = await _db.payments
                .AsTracking()
                .FirstOrDefaultAsync(p => p.provider == "PAYOS" && p.order_code == orderCode, ct);

            if (existing != null)
            {
                existing.status = "PAID";
                existing.paid_at ??= now;

                if (amount > 0) existing.amount = (decimal)amount;

                existing.payos_payment_link_id ??= paymentLinkId;
                existing.payos_transaction_id ??= transactionId;

                if (!string.IsNullOrWhiteSpace(rawJson))
                    existing.payos_raw = rawJson;

                existing.updated_at = AppTime.NowVnUnspecified();

                await _db.SaveChangesAsync(ct);
                return;
            }

            // ✅ insert new
            await paymentRepo.AddAsync(new payment
            {
                order_request_id = orderRequestId,
                provider = "PAYOS",
                order_code = orderCode,
                amount = (decimal)amount,
                currency = "VND",
                status = "PAID",
                paid_at = now,
                payos_payment_link_id = paymentLinkId,
                payos_transaction_id = transactionId,
                payos_raw = rawJson,
                created_at = now,
                updated_at = AppTime.NowVnUnspecified()
            }, ct);

            await paymentRepo.SaveChangesAsync(ct);
        }


        [HttpGet("payos-deposit/{request_id:int}")]
        public async Task<IActionResult> GetPayOsDeposit(int request_id, CancellationToken ct)
        {
            try
            {
                var req = await _service.GetByIdAsync(request_id);
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

                var est = await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == request_id)
                    .OrderByDescending(x => x.created_at)
                    .FirstOrDefaultAsync(ct);

                if (est == null)
                    return BadRequest(new { message = "Cost estimate not found" });

                var expiredAt = est.created_at.AddHours(24);
                if (AppTime.NowVnUnspecified() > expiredAt.ToUniversalTime())
                    return BadRequest(new { message = "Quote expired" });

                var dto = await _dealService.CreateOrReuseDepositLinkAsync(request_id, ct);

                dto.expired_at = expiredAt;

                dto.status ??= "PENDING";

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("full-data-by-request_id/{request_id:int}")]
        public async Task<IActionResult> GetFullDataByRequestId(int request_id, CancellationToken ct)
        {
            var result = await _service.GetInformationRequestById(request_id, ct);
            if (result == null)
                return NotFound(new { message = "Order request not found" });
            return Ok(result);
        }
    }
}
