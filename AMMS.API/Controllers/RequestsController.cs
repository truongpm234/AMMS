using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.Requests;
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

        public RequestsController(
            IRequestService service,
            IDealService dealService,
            IPaymentsService paymentService,
            AppDbContext db,
            IProductionSchedulingService schedulingService,
            ISmsOtpService smsOtp)
        {
            _service = service;
            _dealService = dealService;
            _paymentService = paymentService;
            _db = db;
            _schedulingService = schedulingService;
            _smsOtp = smsOtp;
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

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            await _service.CancelAsync(id, ct);
            return Ok(new { message = "Cancelled", order_request_id = id });
        }


        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var order = await _service.GetByIdAsync(id);
            if (order == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(order);
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
        public async Task<IActionResult> PayOsReturn([FromQuery] int orderRequestId, [FromQuery] long orderCode, [FromServices] IPaymentRepository paymentRepo, CancellationToken ct)
        {
            var info = await HttpContext.RequestServices.GetRequiredService<IPayOsService>()
                    .GetPaymentLinkInformationAsync(orderCode, ct);

            var isPaid =
                string.Equals(info?.status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info?.status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (isPaid)
            {
                var amount = info?.amount ?? 0;
                await ProcessPaidAsync(
                    orderRequestId,
                    orderCode,
                    amount,
                    info?.paymentLinkId,
                    info?.transactionId,
                    info?.rawJson ?? "{}",
                    paymentRepo,
                    ct);
            }

            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}/order-detail/{orderRequestId}");
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
            // FK check
            var existsOrderRequest = await _db.order_requests
                .AsNoTracking()
                .AnyAsync(x => x.order_request_id == orderRequestId, ct);

            if (!existsOrderRequest)
                return (false, $"order_request_id={orderRequestId} not found");

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

                // ✅ Auto schedule production
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
    }
}
