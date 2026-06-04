using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.Constants;
using AMMS.Shared.DTOs.Socket;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AMMS.Application.Helpers
{
    public class RemainingPaymentReminderJob
    {
        private readonly AppDbContext _db;
        private readonly IDealService _dealService;
        private readonly IPaymentRepository _paymentRepo;
        private readonly NotificationService _notificationService;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly ILogger<RemainingPaymentReminderJob> _logger;

        public RemainingPaymentReminderJob(
            AppDbContext db,
            IDealService dealService,
            IPaymentRepository paymentRepo,
            NotificationService notificationService,
            IHubContext<RealtimeHub> hub,
            ILogger<RemainingPaymentReminderJob> logger)
        {
            _db = db;
            _dealService = dealService;
            _paymentRepo = paymentRepo;
            _notificationService = notificationService;
            _hub = hub;
            _logger = logger;
        }

        public async Task RemindRemainingPaymentIfStillPendingAsync(int orderId)
        {
            var ct = CancellationToken.None;

            try
            {
                var ord = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

                if (ord == null)
                    return;

                var req = await _db.order_requests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

                if (req == null)
                    return;

                var prod = await _db.productions
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderByDescending(x => x.prod_id)
                    .FirstOrDefaultAsync(ct);

                var latestRemaining = await _paymentRepo.GetLatestByRequestIdAndTypeAsync(
                    req.order_request_id,
                    PaymentTypes.Remaining,
                    ct);

                if (latestRemaining != null && IsPaidStatus(latestRemaining.status))
                    return;

                var stillPending =
                    string.Equals(ord.status, "PendingPaid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "PendingPaid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prod?.status, "PendingPaid", StringComparison.OrdinalIgnoreCase);

                if (!stillPending)
                    return;

                await _dealService.SendRemainingPaymentReminderEmailAsync(
                    orderId,
                    ct);

                var message =
                    $"Đơn hàng {ord.code} vẫn chưa thanh toán phần còn lại sau 07 ngày. " +
                    $"Consultant vui lòng gọi điện liên hệ khách hàng để hỗ trợ thanh toán.";

                await _hub.Clients.Group(RealtimeGroups.ByRole("consultant")).SendAsync(
                    "remaining-payment-overdue",
                    new
                    {
                        message,
                        order_id = orderId,
                        request_id = req.order_request_id,
                        customer_name = req.customer_name,
                        customer_phone = req.customer_phone
                    },
                    ct);

                await _notificationService.CreateNotfi(
                    2,
                    message,
                    req.assigned_consultant,
                    req.order_request_id,
                    "RemainingPaymentOverdue");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "RemindRemainingPaymentIfStillPendingAsync failed. OrderId={OrderId}",
                    orderId);
            }
        }

        private static bool IsPaidStatus(string? status)
        {
            var s = (status ?? "").Trim().ToUpperInvariant();

            return s == "PAID" ||
                   s == "SUCCESS" ||
                   s == "SUCCEEDED" ||
                   s == "COMPLETED";
        }
    }
}