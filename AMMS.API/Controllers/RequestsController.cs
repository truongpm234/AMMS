using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.Requests;
using AMMS.Shared.DTOs.Requests.AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        public RequestsController(
            IRequestService service,
            IDealService dealService,
            IPaymentsService paymentService,
            AppDbContext db,
            IProductionSchedulingService schedulingService,
            ISmsOtpService smsOtp,
            IConfiguration config)
        {
            _service = service;
            _dealService = dealService;
            _paymentService = paymentService;
            _db = db;
            _schedulingService = schedulingService;
            _smsOtp = smsOtp;
            _config = config;
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

        [HttpGet("reject-form")]
        public async Task<IActionResult> RejectForm([FromQuery] int orderRequestId, [FromQuery] string token)
        {
            var fe = "https://sep490-fe.vercel.app";

            var req = await _service.GetByIdAsync(orderRequestId);
            if (req == null)
            {
                return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}&error=not_found");
            }

            if (string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}&error=accepted");
            }

            return Redirect($"{fe}/reject-deal?orderRequestId={orderRequestId}&token={token}");
        }

        [HttpPost("reject")]
        public async Task<IActionResult> RejectDeal([FromBody] RejectDealRequest dto, CancellationToken ct)
        {
            // 1. Lấy order_request
            var req = await _service.GetByIdAsync(dto.order_request_id);
            if (req == null)
                return NotFound(new { message = "Order request not found" });

            if (string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Order has already been accepted, cannot reject." });
            }

            if (string.IsNullOrWhiteSpace(dto.otp))
                return BadRequest(new { message = "OTP is required to reject this deal." });

            var phone = dto.phone;
            if (string.IsNullOrWhiteSpace(phone))
                phone = req.customer_phone ?? "";

            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { message = "Customer phone is missing, cannot verify OTP." });

            var verifyReq = new VerifyOtpSmsRequest(phone, dto.otp);

            var verifyRes = await _smsOtp.VerifyOtpAsync(verifyReq, ct);
            if (!verifyRes.success || !verifyRes.valid)
            {
                return BadRequest(new { message = verifyRes.message ?? "Invalid or expired OTP" });
            }

            await _dealService.RejectDealAsync(dto.order_request_id, dto.reason ?? "Customer rejected");
            return Ok(new { ok = true });
        }


        [HttpGet("payos/return")]
        public async Task<IActionResult> PayOsReturn([FromQuery] int request_id, [FromQuery] long order_code, [FromServices] IPaymentRepository paymentRepo, CancellationToken ct)
        {
            var info = await HttpContext.RequestServices.GetRequiredService<IPayOsService>()
                    .GetPaymentLinkInformationAsync(order_code, ct);

            var isPaid =
                string.Equals(info?.status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info?.status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (isPaid)
            {
                var amount = info?.amount ?? 0;
                await ProcessPaidAsync(
                    request_id,
                    order_code,
                    amount,
                    info?.payment_link_id,
                    info?.transaction_id,
                    info?.raw_json ?? "{}",
                    paymentRepo,
                    ct);
            }

            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}/look-up");
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
            var node = raw.TryGetProperty("data", out var data) ? data : raw;

            long orderCode =
                node.TryGetProperty("orderCode", out var oc) && oc.ValueKind == JsonValueKind.Number
                    ? oc.GetInt64()
                    : 0;

            var status =
                node.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";

            long amount =
                node.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number
                    ? am.GetInt64()
                    : 0;

            var paymentLinkId = node.TryGetProperty("paymentLinkId", out var pl) ? pl.GetString() : null;
            var transactionId = node.TryGetProperty("transactionId", out var tx) ? tx.GetString() : null;

            var isPaid =
                string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (!isPaid) return Ok(new { ok = true, ignored = true });
            if (orderCode <= 0) return Ok(new { ok = true, error = "orderCode missing/invalid" });

            var orderRequestId = (int)(orderCode % 100000);

            var (ok, message) = await ProcessPaidAsync(
                orderRequestId,
                orderCode,
                amount,
                paymentLinkId,
                transactionId,
                raw.ToString(),
                paymentRepo,
                ct);
            return Ok(new { ok = true, processed = ok, message, orderRequestId, orderCode });
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
        private async Task<(bool ok, string message)> ProcessPaidAsync(int orderRequestId, long orderCode, long amount, string? paymentLinkId, string? transactionId, string rawJson, IPaymentRepository paymentRepo, CancellationToken ct)
        {
            var existsOrderRequest = await _db.order_requests
                .AsNoTracking()
                .AnyAsync(x => x.order_request_id == orderRequestId, ct);

            if (!existsOrderRequest)
                return (false, $"order_request_id={orderRequestId} not found");
            var est = await _db.cost_estimates
        .AsNoTracking()
        .Where(x => x.order_request_id == orderRequestId)
        .OrderByDescending(x => x.created_at)
        .FirstOrDefaultAsync(ct);

            if (est == null)
                return (false, "Cost estimate not found for this request");

            var expiredAt = est.created_at.AddHours(24);
            if (DateTime.UtcNow > expiredAt.ToUniversalTime())
            {
                return (false, $"Quote expired at {expiredAt:o}, ignore payment.");
            }

            var existedPaid = await _paymentService.GetPaidByProviderOrderCodeAsync("PAYOS", orderCode, ct);
            if (existedPaid != null)
            {
                await _dealService.MarkAcceptedAsync(orderRequestId);

                var convert = await _service.ConvertToOrderAsync(orderRequestId);

                return (true, $"Already processed. Convert: {convert.Message}");
            }

            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

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
                updated_at = now
            }, ct);

            await paymentRepo.SaveChangesAsync(ct);

            await _dealService.MarkAcceptedAsync(orderRequestId);

            try
            {
                var convert = await _service.ConvertToOrderAsync(orderRequestId);
                if (!convert.Success)
                    return (false, "ConvertToOrder failed: " + convert.Message);

                try
                {
                    var item = await _db.order_items
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.item_id == convert.OrderItemId, ct);

                    var req = await _db.order_requests
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

                    int productTypeId = 0;

                    productTypeId = await _db.product_types.Where(x => x.code == req.product_type).Select(x => x.product_type_id).FirstAsync(ct);

                    if (productTypeId <= 0)
                        return (false, "Auto schedule failed: productTypeId missing/invalid");

                    var prodId = await _schedulingService.ScheduleOrderAsync(
                        orderId: convert.OrderId!.Value,
                        productTypeId: productTypeId,
                        productionProcessCsv: item?.production_process,
                        managerId: 3
                    );

                    Console.WriteLine($"✅ Auto scheduled production: prod_id={prodId} for order_id={convert.OrderId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("❌ Auto schedule failed: " + ex);
                    return (true, "Processed paid OK + converted, but schedule failed: " + ex.Message);
                }

                Console.WriteLine($"Convert result: success={convert.Success}, msg={convert.Message}, orderId={convert.OrderId}");

                return (true, "Processed paid OK + converted");

            }
            catch (DbUpdateException dbEx) when (dbEx.InnerException is PostgresException pg && pg.SqlState == "23505")
            {
            }

            try
            {
                await _dealService.NotifyConsultantPaidAsync(orderRequestId, (decimal)amount, now);
                await _dealService.NotifyCustomerPaidAsync(orderRequestId, (decimal)amount, now);
            }
            catch { }
            return (true, "Processed paid OK");
        }

        [HttpGet("payos-deposit/{request_id:int}")]
        public async Task<IActionResult> GetPayOsDeposit(int request_id, [FromServices] IPayOsService payOsService, CancellationToken ct)
        {
            try
            {
                var req = await _service.GetByIdAsync(request_id);
                if (req == null) return NotFound("Request not found");

                var est = await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == request_id)
                    .OrderByDescending(x => x.created_at)
                    .FirstOrDefaultAsync(ct);

                if (est == null) return BadRequest("Cost estimate not found");

                int amount = (int)est.deposit_amount / 100;
                if (amount <= 0) return BadRequest("Deposit amount must be > 0");

                long prefix = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm"));
                long orderCode = prefix * 100000 + request_id;

                var backendUrl = _config["Deal:BaseUrl"]!;
                var feBase = "https://sep490-fe.vercel.app";

                var returnUrl = $"{backendUrl}/api/Requests/payos/return?request_id={request_id}&order_code={orderCode}";

                var cancelUrl = $"{feBase}/look-up/{request_id}?status=cancel";
                var description = $"Dat coc don {request_id}";

                var newLink = await payOsService.CreatePaymentLinkAsync(
            orderCode: (int)orderCode,
            amount: amount,
            description: description,
            buyerName: req.customer_name ?? "Khach hang",
            buyerEmail: req.customer_email ?? "",
            buyerPhone: req.customer_phone ?? "",
            returnUrl: returnUrl,
            cancelUrl: cancelUrl,
            ct: ct
        );

                return Ok(new
                {
                    check_out_url = newLink.checkoutUrl,
                    qr_code = newLink.qr_code,           
                    account_number = newLink.account_number,
                    account_name = newLink.account_name,
                    bin = newLink.bin,
                    amount = newLink.amount,
                    description = newLink.description,
                    status = "PENDING",
                    order_code = orderCode
                });
            }
            catch (Exception ex)
            {
                // Log lỗi để debug
                Console.WriteLine(ex.ToString());
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
