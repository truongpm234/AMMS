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

        public DealService(
            IRequestRepository requestRepo,
            ICostEstimateRepository estimateRepo,
            IOrderRepository orderRepo,
            IConfiguration config,
            IEmailService emailService,
            IQuoteRepository quoteRepo)
        {
            _requestRepo = requestRepo;
            _estimateRepo = estimateRepo;
            _orderRepo = orderRepo;
            _config = config;
            _emailService = emailService;
            _quoteRepo = quoteRepo;
        }

        // ================= SEND QUOTE =================

        public async Task SendDealAndEmailAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found");

            if (string.IsNullOrWhiteSpace(req.customer_email))
                throw new Exception("Customer email missing");

            var quote = new quote
            {
                // customer_id = req.customer_id,  
                // consultant_id = req.consultant_id,
                total_amount = est.final_total_cost,
                status = "Sent",
                created_at = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
            };

            await _quoteRepo.AddAsync(quote);
            await _quoteRepo.SaveChangesAsync();

            var baseUrl = _config["Deal:BaseUrl"]!;
            var token = Guid.NewGuid().ToString("N");

            var acceptUrl = $"{baseUrl}/api/requests/deal/accept?orderRequestId={orderRequestId}&token={token}";
            var rejectUrl = $"{baseUrl}/api/requests/deal/reject?orderRequestId={orderRequestId}&token={token}";

            var html = DealEmailTemplates.QuoteEmail(req, est, acceptUrl, rejectUrl);

            await _emailService.SendAsync(
                req.customer_email,
                "Báo giá đơn hàng in ấn",
                html
            );

            req.process_status = "Waiting";
            await _requestRepo.SaveChangesAsync();
        }

        // ================= ACCEPT =================

        public async Task AcceptDealAsync(int orderRequestId)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            var est = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found");

            req.process_status = "Accepted";

            order? order;

            if (req.order_id.HasValue)
            {
                order = await _orderRepo.GetByIdAsync(req.order_id.Value)
                    ?? throw new Exception("Order not found");
            }
            else
            {
                var code = await _orderRepo.GenerateNextOrderCodeAsync();

                order = new order
                {
                    code = code,
                    order_date = DateTime.Now,
                    delivery_date = req.delivery_date,
                    status = "New",
                    payment_status = "Unpaid",
                    total_amount = est.final_total_cost
                };

                await _orderRepo.AddOrderAsync(order);
                await _orderRepo.SaveChangesAsync();

                var item = new order_item
                {
                    order_id = order.order_id,
                    product_name = req.product_name,
                    quantity = (int)req.quantity,
                    design_url = req.design_file_path
                };

                await _orderRepo.AddOrderItemAsync(item);

                req.order_id = order.order_id;
            }

            // Email cho khách hàng
            if (!string.IsNullOrWhiteSpace(req.customer_email))
            {
                var trackingUrl = $"{_config["Deal:BaseUrl"]}/track?code={order.code}";

                await _emailService.SendAsync(
                    req.customer_email,
                    $"Đơn hàng {order.code} đã được phê duyệt",
                    DealEmailTemplates.AcceptCustomerEmail(req, order, est, trackingUrl)
                );
            }

            // Email cho consultant
            await _emailService.SendAsync(
                _config["Deal:ConsultantEmail"]!,
                "Khách hàng đã đồng ý báo giá",
                DealEmailTemplates.AcceptConsultantEmail(req, order)
            );

            await _orderRepo.SaveChangesAsync();
            await _requestRepo.SaveChangesAsync();
        }

        // ================= REJECT =================

        public async Task RejectDealAsync(int orderRequestId, string reason)
        {
            var req = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order request not found");

            req.process_status = "Rejected";

            await _emailService.SendAsync(
                _config["Deal:ConsultantEmail"]!,
                "Khách hàng từ chối báo giá",
                $"<p>Request #{orderRequestId} bị từ chối. Lý do: {reason}</p>"
            );

            await _requestRepo.SaveChangesAsync();
        }
    }
}
