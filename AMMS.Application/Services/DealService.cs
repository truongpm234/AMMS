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

            req.quote_id = quote.quote_id;
            req.process_status = "Waiting";

            await _requestRepo.SaveChangesAsync();

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

    if (req.quote_id == null)
        throw new Exception("Quote not found for this request");

    // ✅ CHỈ đổi trạng thái
    req.process_status = "Accepted";

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
