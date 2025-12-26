using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.PayOS;
using AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RequestsController : ControllerBase
    {
        private readonly IRequestService _service;
        private readonly IDealService _dealService;
        private readonly IPaymentsService _paymentService;

        public RequestsController(IRequestService service, IDealService dealService, IPaymentsService paymentService)
        {
            _service = service;
            _dealService = dealService;
            _paymentService = paymentService;
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
        public async Task<IActionResult> Delete(int id)
        {
            await _service.DeleteAsync(id);
            return NoContent();
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

        [HttpPost("convert-to-order-by-{id:int}")]
        public async Task<IActionResult> ConvertToOrder(int id)
        {
            var result = await _service.ConvertToOrderAsync(id);
            if (!result.Success) return BadRequest(result);
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
                Console.WriteLine(ex.Message);

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
            var checkoutUrl = await _dealService.AcceptAndCreatePayOsLinkAsync(orderRequestId);
            return Redirect(checkoutUrl);
        }

        [HttpGet("reject-form")]
        public IActionResult RejectForm([FromQuery] int orderRequestId, [FromQuery] string token)
        {
            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}/reject-deal");
        }


        [HttpGet("payos/return")]
        public IActionResult PayOsReturn([FromQuery] int orderRequestId, [FromQuery] int orderCode)
        {
            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}");
            //return Redirect($"{fe}/payment-success?orderRequestId={orderRequestId}");
        }

        [HttpGet("payos/cancel")]
        public IActionResult PayOsCancel([FromQuery] int orderRequestId, [FromQuery] int orderCode)
        {
            var fe = "https://sep490-fe.vercel.app";
            return Redirect($"{fe}");
            //return Redirect($"{fe}/payment-cancel?orderRequestId={orderRequestId}");
        }

        [HttpPost("/api/payos/webhook")]
        public async Task<IActionResult> PayOsWebhook([FromBody] PayOsWebhookDto payload, [FromServices] IPaymentRepository paymentRepo, CancellationToken ct)
        {
            var isPaid =
                string.Equals(payload.status, "PAID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(payload.status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            if (!isPaid) return Ok(new { ok = true });

            var orderRequestId = payload.orderCode % 100000;

            var existed = await _paymentService.GetPaidByProviderOrderCodeAsync("PAYOS", payload.orderCode, ct);
            if (existed != null && existed.status == "PAID")
                return Ok(new { ok = true, message = "Already processed" });

            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            var amount = payload.amount.HasValue ? (decimal)payload.amount.Value : 0m;

            await paymentRepo.AddAsync(new payment
            {
                order_request_id = orderRequestId,
                provider = "PAYOS",
                order_code = payload.orderCode,
                amount = amount,
                currency = "VND",
                status = "PAID",
                paid_at = now,
                payos_payment_link_id = payload.paymentLinkId,
                payos_transaction_id = payload.transactionId,
                payos_raw = JsonSerializer.Serialize(payload),
                created_at = now,
                updated_at = now
            }, ct);

            await paymentRepo.SaveChangesAsync(ct);

            await _dealService.MarkScheduledAsync(orderRequestId);

            await _dealService.NotifyConsultantPaidAsync(orderRequestId, amount, now);

            await _dealService.NotifyCustomerPaidAsync(orderRequestId, amount, now);

            return Ok(new { ok = true });
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

    }
}
