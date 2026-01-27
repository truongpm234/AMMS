using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Exceptions.AMMS.Application.Exceptions;
using AMMS.Shared.DTOs.PayOS;
using Microsoft.Extensions.Configuration;

namespace AMMS.Application.Services
{
    public class DealService : IDealService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IOrderRepository _orderRepo;
        private readonly IConfiguration _config;
        private readonly IEmailService _emailService;
        private readonly IQuoteRepository _quoteRepo;
        private readonly IPayOsService _payOs;
        private readonly IPaymentsService _payment;
        public DealService(
            IRequestRepository requestRepo,
            ICostEstimateRepository estimateRepo,
            IOrderRepository orderRepo,
            IConfiguration config,
            IEmailService emailService,
            IQuoteRepository quoteRepo,
            IPayOsService payOs, IPaymentsService payment)
        {
            _requestRepo = requestRepo;
            _estimateRepo = estimateRepo;
            _orderRepo = orderRepo;
            _config = config;
            _emailService = emailService;
            _quoteRepo = quoteRepo;
            _payOs = payOs;
            _payment = payment;
        }

        public async Task SendDealAndEmailAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found");

            var deposit = est.deposit_amount;

            if (string.IsNullOrWhiteSpace(req.customer_email))
                throw new Exception("Customer email missing");

            var quote = new quote
            {
                order_request_id = orderRequestId,
                total_amount = est.final_total_cost,
                status = "Sent",
                created_at = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
            };

            await _quoteRepo.AddAsync(quote);
            await _quoteRepo.SaveChangesAsync();

            req.quote_id = quote.quote_id;
            req.process_status = "Waiting";

            await _requestRepo.SaveChangesAsync();

            var baseUrlFe = _config["Deal:BaseUrlFe"]!;
            var orderDetailUrl = $"{baseUrlFe}/checkout/{orderRequestId}";

            var html = DealEmailTemplates.QuoteEmail(req, est, orderDetailUrl);
            Console.WriteLine($"order_request_date = {req.order_request_date?.ToString("O") ?? "NULL"}");
            await _emailService.SendAsync(req.customer_email, "Báo giá đơn hàng in ấn", html);
        }

        public async Task<string> AcceptAndCreatePayOsLinkAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId) ?? throw new Exception("Order request not found");
            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId) ?? throw new Exception("Estimate not found");

            await SendConsultantStatusEmailAsync(req, est, statusText: "KHÁCH ĐỒNG Ý BÁO GIÁ");

            var deposit = est.deposit_amount;
            var amount = (int)Math.Round(deposit, 0);
            var description = $"AM{orderRequestId:D6}";
            
            var orderCode = await GetOrCreatePayOsOrderCodeAsync(orderRequestId);
            var baseUrl = _config["Deal:BaseUrl"]!;

            var returnUrl = $"{baseUrl}/api/requests/payos/return?request_id={orderRequestId}&order_code={orderCode}";
            var cancelUrl = $"{baseUrl}/api/requests/payos/cancel?orderRequestId={orderRequestId}&orderCode={orderCode}";

            var existing = await _payOs.GetPaymentLinkInformationAsync(orderCode);
            if (existing != null)
            {
                var st = (existing.status ?? "").ToUpperInvariant();
                if (st == "PENDING" || st == "PAID" || st == "SUCCESS" || st == "CANCELLED")
                    return existing.check_out_url ?? "";
            }

            var result = await _payOs.CreatePaymentLinkAsync(
                orderCode: orderCode,
                amount: amount,
                description: description,
                buyerName: req.customer_name ?? "N/A",
                buyerEmail: req.customer_email ?? "",
                buyerPhone: req.customer_phone ?? "",
                returnUrl: returnUrl,
                cancelUrl: cancelUrl
            );

            return result.check_out_url ?? "";
        }

        public async Task<PayOsDepositInfoDto> PrepareDepositPaymentAsync(int orderRequestId, CancellationToken ct = default)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId) ?? throw new InvalidOperationException("Order request not found");
            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId) ?? throw new InvalidOperationException("Cost estimate not found");

            var expiredAt = est.created_at.AddHours(24);
            if (DateTime.UtcNow > expiredAt.ToUniversalTime())
            {
                throw new InvalidOperationException("Báo giá đã hết hạn, không thể tạo link thanh toán.");
            }

            var deposit = est.deposit_amount;
            if (deposit <= 0) throw new InvalidOperationException("Deposit amount is zero.");

            var orderCode = (long)orderRequestId; // Logic tạo mã đơn
            var feBase = _config["Deal:BaseUrlFe"] ?? "https://sep490-fe.vercel.app";
            var returnUrl = $"{feBase}/request-detail/{orderRequestId}";
            var cancelUrl = returnUrl;
            var description = $"Đặt cọc đơn hàng AM{orderRequestId:D6}";

            PayOsResultDto result;

            var existingLink = await _payOs.GetPaymentLinkInformationAsync(orderCode, ct);
            if (existingLink != null && (existingLink.status == "PENDING" || existingLink.status == "PAID"))
            {
                // Dùng lại thông tin cũ
                result = existingLink;
            }
            else
            {
                result = await _payOs.CreatePaymentLinkAsync(
                    orderCode: (int)orderCode,
                    amount: (int)deposit / 100, // Chia 100 dễ test
                    description: description,
                    buyerName: req.customer_name ?? "",
                    buyerEmail: req.customer_email ?? "",
                    buyerPhone: req.customer_phone ?? "",
                    returnUrl: returnUrl,
                    cancelUrl: cancelUrl,
                    ct: ct);
            }

            return new PayOsDepositInfoDto
            {
                order_code = orderCode,
                checkout_url = result.check_out_url,
                deposit_amount = deposit,
                expire_at = expiredAt,
                qr_code = result.qr_code,
                account_number = result.account_number,
                account_name = result.account_name,
                bin = result.bin
            };
        }

        public async Task RejectDealAsync(int orderRequestId, string reason)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            req.process_status = "Rejected";

            if (req.quote_id != null)
            {
                var q = await _quoteRepo.GetByIdAsync(req.quote_id.Value);
                if (q != null) q.status = "Rejected";
                await _quoteRepo.SaveChangesAsync();
            }

            await _requestRepo.SaveChangesAsync();

            cost_estimate? est = null;
            try { est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId); } catch { }

            var safeReason = System.Net.WebUtility.HtmlEncode(reason ?? "");
            await SendConsultantStatusEmailAsync(req, est, $"KHACH TU CHOI (LY DO: {safeReason})");
        }

        public async Task MarkAcceptedAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            req.process_status = "Accepted";

            if (req.quote_id != null)
            {
                var q = await _quoteRepo.GetByIdAsync(req.quote_id.Value);
                if (q != null) q.status = "Accepted";
                await _quoteRepo.SaveChangesAsync();
            }

            await _requestRepo.SaveChangesAsync();
        }

        public async Task SendConsultantStatusEmailAsync(
    order_request req,
    cost_estimate? est,
    string statusText,
    decimal? paidAmount = null,
    DateTime? paidAt = null)
        {
            var consultantEmail = _config["Deal:ConsultantEmail"];
            if (string.IsNullOrWhiteSpace(consultantEmail))
                return;

            var address = $"{req.detail_address}";
            var delivery = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A";

            var finalTotal = est?.final_total_cost ?? 0m;
            var deposit = est?.deposit_amount ?? 0m;

            var paidLine = paidAmount.HasValue
                ? $"<p><b>Số tiền đã thanh toán:</b> {paidAmount.Value:n0} VND</p>"
                : "";

            var paidAtLine = paidAt.HasValue
                ? $"<p><b>Thời gian thanh toán:</b> {paidAt.Value:dd/MM/yyyy HH:mm:ss}</p>"
                : "";

            var html = $@"
<div style='font-family:Arial;max-width:720px;margin:24px auto'>
  <h2>Thông báo trạng thái đơn</h2>
  <p style='font-size:16px'><b>Trạng thái:</b> <span style='color:#0f172a'>{statusText}</span></p>
  <hr/>

  <h3>Thông tin khách hàng</h3>
  <ul>
    <li><b>Tên:</b> {req.customer_name}</li>
    <li><b>SĐT:</b> {req.customer_phone}</li>
    <li><b>Email:</b> {req.customer_email}</li>
    <li><b>Địa chỉ:</b> {address}</li>
  </ul>

  <h3>Thông tin đơn hàng</h3>
  <ul>
    <li><b>Request ID:</b> {req.order_request_id}</li>
    <li><b>Sản phẩm:</b> {req.product_name}</li>
    <li><b>Số lượng:</b> {req.quantity}</li>
    <li><b>Ngày giao dự kiến:</b> {delivery}</li>
    <li><b>Final Total:</b> {finalTotal:n0} VND</li>
    <li><b>Phí cọc:</b> {deposit:n0} VND</li>
  </ul>

  {paidLine}
  {paidAtLine}

  <p style='color:#64748b;font-size:12px'>AMMS System</p>
</div>";

            await _emailService.SendAsync(
                consultantEmail,
                $"[AMMS] Trạng thái đơn #{req.order_request_id}: {statusText}",
                html
            );
        }
        public async Task NotifyConsultantPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            cost_estimate? est = null;
            try
            {
                est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId);
            }
            catch { }

            await SendConsultantStatusEmailAsync(
                req,
                est,
                statusText: "KHÁCH ĐỒNG Ý & ĐÃ THANH TOÁN CỌC",
                paidAmount: paidAmount,
                paidAt: paidAt
            );
        }
        public async Task NotifyCustomerPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            if (string.IsNullOrWhiteSpace(req.customer_email))
                return;

            cost_estimate? est = null;
            try
            {
                est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId);
            }
            catch { }

            var finalTotal = est?.final_total_cost ?? 0m;
            var deposit = est?.deposit_amount ?? 0m;

            var html = $@"
<div style='font-family:Arial;max-width:720px;margin:24px auto'>
  <h2>Thanh toán thành công</h2>
  <p>Chúng tôi đã nhận được tiền cọc cho đơn hàng của bạn.</p>

  <h3>Thông tin đơn hàng</h3>
  <ul>
    <li><b>Request ID:</b> {req.order_request_id}</li>
    <li><b>Sản phẩm:</b> {req.product_name}</li>
    <li><b>Số lượng:</b> {req.quantity}</li>
    <li><b>Tổng giá trị:</b> {finalTotal:n0} VND</li>
    <li><b>Tiền cọc:</b> {deposit:n0} VND</li>
  </ul>

  <h3>Thông tin thanh toán</h3>
  <ul>
    <li><b>Số tiền đã thanh toán:</b> {paidAmount:n0} VND</li>
    <li><b>Thời gian:</b> {paidAt:dd/MM/yyyy HH:mm:ss}</li>
    <li><b>Trạng thái:</b> PAID</li>
  </ul>

  <p style='color:#64748b;font-size:12px'>AMMS System</p>
</div>";

            await _emailService.SendAsync(
                req.customer_email,
                $"[AMMS] Thanh toán thành công - Đơn #{req.order_request_id}",
                html
            );
        }

        public async Task<PayOsResultDto> CreateOrReuseDepositLinkAsync(int requestId, CancellationToken ct = default)
        {
            var req = await _requestRepo.GetByIdAsync(requestId)
                      ?? throw new InvalidOperationException("Request not found");

            var est = await _estimateRepo.GetByOrderRequestIdAsync(requestId)
                      ?? throw new InvalidOperationException("Cost estimate not found");

            // ✅ 1) DB snapshot trước
            var pending = await _payment.GetLatestPendingByRequestIdAsync(requestId, ct);
            if (pending != null && !string.IsNullOrWhiteSpace(pending.payos_raw))
                return PayOsRawMapper.FromPayment(pending);

            var backendUrl = _config["Deal:BaseUrl"]!;
            var feBase = _config["Deal:BaseUrlFe"] ?? "https://sep490-fe.vercel.app";

            // ⚠️ test /100 thì giữ, prod bỏ /100
            var amount = (int)Math.Round(est.deposit_amount, 0) / 100;
            var description = $"AM{requestId:D6}";

            // ✅ 2) Retry orderCode nếu duplicate
            const int maxAttempt = 9;
            Exception? last = null;

            for (int attempt = 1; attempt <= maxAttempt; attempt++)
            {
                int orderCode = checked(requestId * 10 + attempt);

                var returnUrl = $"{backendUrl}/api/requests/payos/return?request_id={requestId}&order_code={orderCode}";
                var cancelUrl = $"{feBase}/reject-deal/{requestId}?status=cancel";

                try
                {
                    var payos = await _payOs.CreatePaymentLinkAsync(
                        orderCode: orderCode,
                        amount: amount,
                        description: description,
                        buyerName: req.customer_name ?? "Khach hang",
                        buyerEmail: req.customer_email ?? "",
                        buyerPhone: req.customer_phone ?? "",
                        returnUrl: returnUrl,
                        cancelUrl: cancelUrl,
                        ct: ct);

                    // ✅ 3) lưu snapshot PENDING
                    var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
                    await _payment.UpsertPendingAsync(new payment
                    {
                        order_request_id = requestId,
                        provider = "PAYOS",
                        order_code = orderCode,
                        amount = payos.amount ?? amount,
                        currency = "VND",
                        status = "PENDING",
                        payos_payment_link_id = payos.payment_link_id,
                        payos_raw = payos.raw_json,
                        created_at = now,
                        updated_at = now
                    }, ct);

                    await _payment.SaveChangesAsync(ct);
                    return payos;
                }
                catch (PayOsException ex) when (IsDuplicateOrderCode(ex.Message))
                {
                    last = ex; // thử attempt tiếp
                    continue;
                }
            }

            throw new InvalidOperationException($"Cannot create PayOS link after retries. Last error: {last?.Message}");
        }

        private static bool IsDuplicateOrderCode(string msg)
        {
            msg = (msg ?? "").ToLowerInvariant();
            return msg.Contains("ordercode") && (msg.Contains("exists") || msg.Contains("tồn tại") || msg.Contains("231"));
        }

        private async Task<int> GetOrCreatePayOsOrderCodeAsync(
    int orderRequestId,
    CancellationToken ct = default,
    int maxAttempt = 9)
        {
            for (int attempt = 1; attempt <= maxAttempt; attempt++)
            {
                int orderCode = checked(orderRequestId * 10 + attempt);

                var info = await _payOs.GetPaymentLinkInformationAsync(orderCode, ct);

                if (info == null) return orderCode;

                var st = (info.status ?? "").ToUpperInvariant();
                if (st == "PENDING" || st == "PAID" || st == "SUCCESS")
                    return orderCode;

                if (st == "CANCELLED" || st == "EXPIRED")
                    continue;
            }

            throw new InvalidOperationException("Cannot allocate orderCode: attempts exhausted.");
        }

    }
}
