using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;

namespace AMMS.Application.Services
{
    public class DealService : IDealService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IConfiguration _config;
        private readonly IEmailService _emailService;

        public DealService(
            IRequestRepository requestRepo,
            ICostEstimateRepository estimateRepo,
            IConfiguration config,
            IEmailService emailService)
        {
            _requestRepo = requestRepo;
            _estimateRepo = estimateRepo;
            _config = config;
            _emailService = emailService;
        }

        public async Task SendDealAndEmailAsync(int orderRequestId)
        {
            Console.WriteLine($"[DEAL] Start SendDealAndEmailAsync - OrderRequestId: {orderRequestId}");

            var order = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order not found");

            var estimate = await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId)
                ?? throw new Exception("Estimate not found");

            Console.WriteLine($"[DEAL] CustomerEmail: {order.customer_email}");

            var baseUrl = _config["Deal:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new Exception("Deal:BaseUrl missing");

            var token = Guid.NewGuid().ToString();

            var acceptUrl = $"{baseUrl}/api/requests/deal/accept?orderRequestId={orderRequestId}&token={token}";
            var rejectUrl = $"{baseUrl}/api/requests/deal/reject?orderRequestId={orderRequestId}&token={token}&reason=Khong%20dong%20y";

            Console.WriteLine($"[DEAL] AcceptUrl: {acceptUrl}");
            Console.WriteLine($"[DEAL] RejectUrl: {rejectUrl}");

            var html = $@"
<h3>BÁO GIÁ ĐƠN HÀNG</h3>
<p>Sản phẩm: {order.product_name}</p>
<p>Số lượng: {order.quantity}</p>
<p><b>Giá đề xuất: {estimate.final_total_cost:N0} VND</b></p>

<a href='{acceptUrl}' style='padding:10px 15px;background:green;color:white;text-decoration:none'>
   Đồng ý
</a>

<a href='{rejectUrl}' style='padding:10px 15px;background:red;color:white;text-decoration:none;margin-left:10px'>
   Từ chối
</a>
";

            // ✅ Chỉ gọi email service (không đọc Mailtrap/Smtp trong DealService nữa)
            await _emailService.SendAsync(order.customer_email!, "Báo giá đơn hàng in ấn", html);

            // ✅ Update status sau khi gửi thành công
            order.process_status = "Waiting";
            await _requestRepo.SaveChangesAsync();

            Console.WriteLine("[DEAL] SendDealAndEmailAsync finished");
        }

        public async Task AcceptDealAsync(int orderRequestId)
        {
            Console.WriteLine($"[DEAL] AcceptDealAsync - OrderRequestId: {orderRequestId}");

            var order = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order not found");

            order.process_status = "Accepted";

            var consultantEmail = _config["Deal:ConsultantEmail"]
                ?? throw new Exception("Deal:ConsultantEmail missing");

            await _emailService.SendAsync(
                consultantEmail,
                "Khách hàng đã đồng ý báo giá",
                $"<p>Order #{orderRequestId} đã được chấp nhận</p>"
            );

            await _requestRepo.SaveChangesAsync();
        }

        public async Task RejectDealAsync(int orderRequestId, string reason)
        {
            Console.WriteLine($"[DEAL] RejectDealAsync - OrderRequestId: {orderRequestId} - Reason: {reason}");

            var order = await _requestRepo.GetByIdAsync(orderRequestId)
                ?? throw new Exception("Order not found");

            order.process_status = "Rejected";

            var consultantEmail = _config["Deal:ConsultantEmail"]
                ?? throw new Exception("Deal:ConsultantEmail missing");

            await _emailService.SendAsync(
                consultantEmail,
                "Khách hàng từ chối báo giá",
                $"<p>Order #{orderRequestId} bị từ chối. Lý do: {reason}</p>"
            );

            await _requestRepo.SaveChangesAsync();
        }
    }
}
