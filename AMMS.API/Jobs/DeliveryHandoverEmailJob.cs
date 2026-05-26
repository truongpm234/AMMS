using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace AMMS.API.Jobs
{
    public sealed class DeliveryHandoverEmailJob
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly IEmailService _emailService;
        private readonly ILogger<DeliveryHandoverEmailJob> _logger;

        public DeliveryHandoverEmailJob(
            AppDbContext db,
            IConfiguration config,
            IEmailService emailService,
            ILogger<DeliveryHandoverEmailJob> logger)
        {
            _db = db;
            _config = config;
            _emailService = emailService;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 180, 600 })]
        [DisableConcurrentExecution(timeoutInSeconds: 5 * 60)]
        public async Task RunAsync(int orderId, CancellationToken ct = default)
        {
            _logger.LogInformation(
                "[DeliveryHandoverEmailJob] Start. orderId={OrderId}",
                orderId);

            var ord = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
            {
                _logger.LogWarning(
                    "[DeliveryHandoverEmailJob] Order not found. orderId={OrderId}",
                    orderId);
                return;
            }

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            if (req == null)
            {
                _logger.LogWarning(
                    "[DeliveryHandoverEmailJob] Order request not found. orderId={OrderId}",
                    orderId);
                return;
            }

            if (string.IsNullOrWhiteSpace(req.customer_email))
            {
                _logger.LogWarning(
                    "[DeliveryHandoverEmailJob] Customer email missing. requestId={RequestId}",
                    req.order_request_id);
                return;
            }

            var prod = await ResolveProductionForDeliveryEmailAsync(orderId, ct);

            cost_estimate? est = null;

            if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
            {
                est = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == req.accepted_estimate_id.Value &&
                        x.order_request_id == req.order_request_id,
                        ct);
            }

            est ??= await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == req.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            var isDelivery =
                string.Equals(ord.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(req.process_status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prod?.status, "Delivery", StringComparison.OrdinalIgnoreCase);

            if (!isDelivery)
            {
                _logger.LogInformation(
                    "[DeliveryHandoverEmailJob] Skip because status is not Delivery. orderId={OrderId}, orderStatus={OrderStatus}, requestStatus={RequestStatus}, prodStatus={ProdStatus}",
                    orderId,
                    ord.status,
                    req.process_status,
                    prod?.status);
                return;
            }

            var feBase = (_config["Deal:BaseUrlFe"] ?? "").TrimEnd('/');

            if (string.IsNullOrWhiteSpace(feBase))
                throw new InvalidOperationException("Missing Deal:BaseUrlFe");

            var confirmReceiveUrl = $"{feBase}/customer-receive/{req.order_request_id}";

            var html = DeliveryEmailTemplates.BuildDeliveryHandoverEmail(
                req,
                ord,
                prod,
                est,
                confirmReceiveUrl);

            var subject = $"[MES] Đơn hàng {ord.code} đã được bàn giao cho đơn vị vận chuyển";

            await _emailService.SendAsync(req.customer_email, subject, html);

            _logger.LogInformation(
                "[DeliveryHandoverEmailJob] Sent delivery handover email successfully. orderId={OrderId}, requestId={RequestId}, to={To}",
                orderId,
                req.order_request_id,
                req.customer_email);
        }

        private async Task<production?> ResolveProductionForDeliveryEmailAsync(
    int orderId,
    CancellationToken ct)
        {
            var linkedProdRows = await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.order_id == orderId &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .Select(x => new
                {
                    x.prod_id,
                    x.single_prod_id
                })
                .ToListAsync(ct);

            var linkedProdIds = linkedProdRows
                .Select(x => x.prod_id)
                .Concat(
                    linkedProdRows
                        .Where(x => x.single_prod_id.HasValue)
                        .Select(x => x.single_prod_id!.Value)
                )
                .Distinct()
                .ToList();

            var candidates = await _db.productions
                .AsNoTracking()
                .Where(x =>
                    (
                        x.order_id.HasValue &&
                        x.order_id.Value == orderId
                    )
                    ||
                    linkedProdIds.Contains(x.prod_id))
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return null;

            /*
             * Quan trọng:
             * Không dùng string.Equals(..., StringComparison.OrdinalIgnoreCase)
             * trong IQueryable.
             * Đã ToListAsync rồi nên đoạn dưới chạy trong memory, an toàn.
             */
            return candidates
                .OrderByDescending(x => IsDeliveryStatus(x.status))
                .ThenByDescending(x => x.prod_id)
                .FirstOrDefault();
        }

        private static bool IsDeliveryStatus(string? status)
        {
            return string.Equals(
                status,
                "Delivery",
                StringComparison.OrdinalIgnoreCase);
        }

        private async Task<cost_estimate?> ResolveEstimateAsync(
            order_request req,
            CancellationToken ct)
        {
            if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
            {
                var accepted = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == req.accepted_estimate_id.Value &&
                        x.order_request_id == req.order_request_id,
                        ct);

                if (accepted != null)
                    return accepted;
            }

            return await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == req.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);
        }
    }
}