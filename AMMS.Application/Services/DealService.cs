using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
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
        public DealService(
            IRequestRepository requestRepo,
            ICostEstimateRepository estimateRepo,
            IOrderRepository orderRepo,
            IConfiguration config,
            IEmailService emailService,
            IQuoteRepository quoteRepo,
            IPayOsService payOs)
        {
            _requestRepo = requestRepo;
            _estimateRepo = estimateRepo;
            _orderRepo = orderRepo;
            _config = config;
            _emailService = emailService;
            _quoteRepo = quoteRepo;
            _payOs = payOs;
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

            var baseUrl = _config["Deal:BaseUrl"]!;
            var token = Guid.NewGuid().ToString("N");

            var acceptUrl = $"{baseUrl}/api/requests/accept-pay?orderRequestId={orderRequestId}&token={token}";
            var rejectUrl = $"{baseUrl}/api/requests/reject-form?orderRequestId={orderRequestId}&token={token}";

            var orderDetailUrl = $"{baseUrl}/order-detail/{orderRequestId}";
            var hasDesignFile = !string.IsNullOrWhiteSpace(req.design_file_path);
            var customerWillSendDesign = (req.is_send_design ?? false) || !hasDesignFile;

            string html;

            if (customerWillSendDesign)
            {
                html = DealEmailTemplates.QuoteEmailNeedDesign(
                    req,
                    est,
                    deposit,
                    acceptUrl,
                    rejectUrl,
                    orderDetailUrl);
            }
            else
            {
                html = DealEmailTemplates.QuoteEmail(
                    req,
                    est,
                    deposit,
                    acceptUrl,
                    rejectUrl);
            }

            await _emailService.SendAsync(req.customer_email, "Báo giá đơn hàng in ấn", html);
        }

        public async Task<string> AcceptAndCreatePayOsLinkAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found");

            await SendConsultantStatusEmailAsync(req, est, statusText: "KHÁCH ĐỒNG Ý BÁO GIÁ");

            var deposit = est.deposit_amount;
            var amount = (int)Math.Round(deposit, 0);

            var description = $"AM{orderRequestId:D6}";

            // orderCode unique (int)
            var orderCode = (req.quote_id ?? orderRequestId) * 100000 + orderRequestId;

            var baseUrl = _config["Deal:BaseUrl"]!;

            // ✅ return/cancel về RequestsController (GET)
            var returnUrl = $"{baseUrl}/api/requests/payos/return?orderRequestId={orderRequestId}&orderCode={orderCode}";
            var cancelUrl = $"{baseUrl}/api/requests/payos/cancel?orderRequestId={orderRequestId}&orderCode={orderCode}";

            var checkoutUrl = await _payOs.CreatePaymentLinkAsync(
                orderCode: orderCode,
                amount: amount,
                description: description,
                buyerName: req.customer_name ?? "N/A",
                buyerEmail: req.customer_email ?? "",
                buyerPhone: req.customer_phone ?? "",
                returnUrl: returnUrl,
                cancelUrl: cancelUrl
            );

            return checkoutUrl;
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

    }
}
