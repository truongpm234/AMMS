using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.Constants;
using AMMS.Shared.DTOs.Payments;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using AMMS.Shared.DTOs.PayOS;

namespace AMMS.Application.Services
{
    public class PaymentsService : IPaymentsService
    {
        private readonly AppDbContext _db;
        private readonly IRequestService _requestService;
        private readonly IDealService _dealService;
        private readonly IPaymentRepository _paymentRepo;
        private readonly ILogger<PaymentsService> _logger;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;
        private readonly IBaseConfigRepository _baseconfigRepo;
        private readonly ICloudinaryFileStorageService _cloudinaryStorage;
        private readonly IPayOsService _payOs;
        public PaymentsService(
            AppDbContext db,
            IRequestService requestService,
            IDealService dealService,
            IPaymentRepository paymentRepo,
            ILogger<PaymentsService> logger, IConfiguration config, 
            IWebHostEnvironment env, IBaseConfigRepository baseconfigRepo, 
            ICloudinaryFileStorageService cloudinaryStorage, IPayOsService payOs)
        {
            _db = db;
            _requestService = requestService;
            _dealService = dealService;
            _paymentRepo = paymentRepo;
            _logger = logger;
            _config = config;
            _env = env;
            _baseconfigRepo = baseconfigRepo;
            _cloudinaryStorage = cloudinaryStorage;
            _payOs = payOs;
        }

        public Task<payment?> GetPaidByProviderOrderCodeAsync(string provider, long orderCode, CancellationToken ct = default)
        {
            return _paymentRepo.GetPaidByProviderOrderCodeAsync(provider, orderCode, ct);
        }

        public Task<payment?> GetLatestByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return _paymentRepo.GetLatestByRequestIdAsync(orderRequestId, ct);
        }

        public async Task<payment?> GetLatestPendingByRequestIdAsync(int requestId, CancellationToken ct)
        {
            return await _paymentRepo.GetLatestPendingByRequestIdAsync(requestId, ct);
        }

        public async Task UpsertPendingAsync(payment p, CancellationToken ct)
        {
            await _paymentRepo.UpsertPendingAsync(p, ct);
        }

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            return _paymentRepo.SaveChangesAsync(ct);
        }

        public async Task<payment?> GetLatestPendingByRequestIdAndEstimateIdAsync(int requestId, int estimateId, CancellationToken ct = default)
        {
            return await _paymentRepo.GetLatestPendingByRequestIdAndEstimateIdAsync(requestId, estimateId, ct);
        }

        public async Task<payment?> GetByOrderCodeAsync(long orderCode, CancellationToken ct = default)
        {
            return await _paymentRepo.GetByOrderCodeAsync(orderCode, ct);
        }

        public async Task<payment?> GetLatestByRequestIdAndEstimateIdAsync(int requestId, int estimateId, CancellationToken ct = default)
        {
            return await _paymentRepo.GetLatestByRequestIdAndEstimateIdAsync(requestId, estimateId, ct);
        }

        public async Task<payment?> GetLatestPendingByRequestIdAndTypeAsync(int requestId, string paymentType, CancellationToken ct = default)
        {
            return await _paymentRepo.GetLatestPendingByRequestIdAndTypeAsync(requestId, paymentType, ct);
        }

        public async Task<payment?> GetLatestByRequestIdAndTypeAsync(int requestId, string paymentType, CancellationToken ct = default)
        {
            return await _paymentRepo.GetLatestByRequestIdAndTypeAsync(requestId, paymentType, ct);
        }

        public async Task<(bool ok, string message)> ProcessPaidAsync(
            int orderRequestId,
            long orderCode,
            long amount,
            string? paymentLinkId,
            string? transactionId,
            string rawJson,
            int? estimateIdFromQuery,
            int? quoteIdFromQuery,
            CancellationToken ct = default)
        {
            var existingPayment = await _paymentRepo.GetByOrderCodeAsync(orderCode, ct);
            var paymentType = existingPayment?.payment_type ?? "Deposit";

            if (string.Equals(paymentType, "Remaining", StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessRemainingPaidAsync(
                    orderCode,
                    amount,
                    paymentLinkId,
                    transactionId,
                    rawJson,
                    ct);
            }

            return await ProcessDepositPaidAsync(
                orderRequestId,
                orderCode,
                amount,
                paymentLinkId,
                transactionId,
                rawJson,
                estimateIdFromQuery,
                quoteIdFromQuery,
                ct);
        }

        private static bool IsPayableStatus(string? status)
        {
            return string.Equals(status, "Verified", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Waiting", StringComparison.OrdinalIgnoreCase);
        }

        private static (bool ok, string message) ValidateQuotePaymentWindow(order_request req, DateTime now)
        {
            if (!IsPayableStatus(req.process_status))
                return (false, "Only request with process_status is verified or waiting can start payment");

            if (!req.quote_expires_at.HasValue)
                return (false, "Quote expiry time has not been initialized");

            if (now > req.quote_expires_at.Value)
                return (false, $"Quote expired at {req.quote_expires_at:yyyy-MM-dd HH:mm:ss}");

            return (true, "");
        }

        private async Task<(bool ok, string message)> ProcessDepositPaidAsync(
    int orderRequestId,
    long orderCode,
    long amount,
    string? paymentLinkId,
    string? transactionId,
    string rawJson,
    int? estimateIdFromQuery,
    int? quoteIdFromQuery,
    CancellationToken ct)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var req = await _db.order_requests
                    .FirstOrDefaultAsync(x => x.order_request_id == orderRequestId, ct);

                if (req == null)
                    return (false, $"order_request_id={orderRequestId} not found");

                var now = AppTime.NowVnUnspecified();

                if (string.Equals(req.process_status, "Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Request has been rejected. Cannot finalize payment.");
                }

                var alreadyLightAccepted =
                    string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Paid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Completed", StringComparison.OrdinalIgnoreCase);

                if (!alreadyLightAccepted)
                {
                    var validation = ValidateQuotePaymentWindow(req, now);
                    if (!validation.ok)
                    {
                        await tx.RollbackAsync(ct);
                        return (false, validation.message);
                    }
                }

                var paymentBefore = await _db.payments
                    .FirstOrDefaultAsync(p => p.provider == "PAYOS" && p.order_code == orderCode, ct);

                var shouldSendPaidEmail = paymentBefore == null ||
                                          !string.Equals(paymentBefore.status, "PAID", StringComparison.OrdinalIgnoreCase);

                var currentPayment = await _db.payments
                    .AsNoTracking()
                    .Where(p => p.provider == "PAYOS" && p.order_code == orderCode)
                    .OrderByDescending(p => p.payment_id)
                    .FirstOrDefaultAsync(ct);

                var resolvedEstimateId =
                    (currentPayment?.estimate_id).GetValueOrDefault() > 0
                        ? currentPayment!.estimate_id!.Value
                        : (estimateIdFromQuery.GetValueOrDefault() > 0 ? estimateIdFromQuery!.Value : 0);

                if (resolvedEstimateId <= 0)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Cannot resolve estimate_id for this payment/order_code");
                }

                var resolvedQuoteId =
                    (currentPayment?.quote_id).GetValueOrDefault() > 0
                        ? currentPayment!.quote_id!.Value
                        : (quoteIdFromQuery.GetValueOrDefault() > 0 ? quoteIdFromQuery!.Value : 0);

                if (resolvedQuoteId <= 0)
                {
                    resolvedQuoteId = await _db.quotes
                        .AsNoTracking()
                        .Where(x => x.order_request_id == orderRequestId && x.estimate_id == resolvedEstimateId)
                        .OrderByDescending(x => x.quote_id)
                        .Select(x => (int?)x.quote_id)
                        .FirstOrDefaultAsync(ct) ?? 0;
                }

                var est = await _db.cost_estimates
                    .FirstOrDefaultAsync(x => x.estimate_id == resolvedEstimateId
                                           && x.order_request_id == orderRequestId, ct);

                if (est == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Cost estimate not found for paid payment");
                }

                var wasAccepted =
                    string.Equals(req.process_status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Paid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.process_status, "Completed", StringComparison.OrdinalIgnoreCase);

                if (!wasAccepted)
                    req.process_status = "Accepted";

                if (req.accepted_estimate_id == null)
                    req.accepted_estimate_id = resolvedEstimateId;
                else if (req.accepted_estimate_id != resolvedEstimateId)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Request already accepted with a different estimate.");
                }

                if (resolvedQuoteId > 0)
                    req.quote_id = resolvedQuoteId;

                await _db.SaveChangesAsync(ct);

                decimal actualDepositAmount = (currentPayment?.amount).GetValueOrDefault() > 0 ? currentPayment!.amount : PaymentAmountHelper.GetDepositAmount(est);

                await UpsertPaidPaymentRowAsync(
                    orderRequestId: orderRequestId,
                    orderCode: orderCode,
                    actualAmount: actualDepositAmount,
                    paymentLinkId: paymentLinkId,
                    transactionId: transactionId,
                    rawJson: rawJson,
                    estimateIdFromQuery: resolvedEstimateId,
                    quoteIdFromQuery: resolvedQuoteId > 0 ? resolvedQuoteId : null,
                    paymentType: "Deposit",
                    ct: ct);

                var paidPayment = await _db.payments
                    .Where(p => p.provider == "PAYOS" && p.order_code == orderCode)
                    .OrderByDescending(p => p.payment_id)
                    .FirstOrDefaultAsync(ct);

                if (paidPayment == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Payment row not found");
                }

                if (!string.Equals(paidPayment.status, "PAID", StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(ct);
                    return (false, $"Payment status was not updated to PAID. Current status = {paidPayment.status}");
                }

                await _db.cost_estimates
                    .Where(x => x.order_request_id == orderRequestId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.is_active, x => x.estimate_id == resolvedEstimateId), ct);

                if (resolvedQuoteId > 0)
                {
                    await _db.quotes
                        .Where(x => x.order_request_id == orderRequestId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(x => x.status,
                                x => x.quote_id == resolvedQuoteId ? "Accepted" : "Rejected"), ct);
                }

                if (!req.order_id.HasValue)
                {
                    var convert = await _requestService.ConvertToOrderInCurrentTransactionAsync(orderRequestId);
                    if (!convert.Success || !convert.OrderId.HasValue)
                    {
                        await tx.RollbackAsync(ct);
                        return (false, convert.Message ?? "Convert to order failed");
                    }

                    req.order_id = convert.OrderId.Value;
                }

                if (req.order_id.HasValue)
                {
                    var ord = await _db.orders.FirstOrDefaultAsync(x => x.order_id == req.order_id.Value, ct);
                    if (ord == null)
                    {
                        await tx.RollbackAsync(ct);
                        return (false, "Order not found");
                    }

                    ord.payment_status = "Deposited";

                    if (!ord.layout_confirmed &&
                        string.IsNullOrWhiteSpace(ord.status))
                    {
                        ord.status = "LayoutPending";
                    }
                }

                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                await TryGenerateAndPersistReceiptAsync(
                    orderCode,
                    PaymentTypes.Deposit,
                    ct,
                    payosRawJson: rawJson,
                    forceGenerateWhenPayOsPaid: PayOsRawIndicatesPaid(rawJson));

                if (shouldSendPaidEmail)
                {
                    try
                    {
                        await _dealService.NotifyConsultantPaidAsync(orderRequestId, paidPayment.amount, now);
                        await _dealService.NotifyCustomerPaidAsync(orderRequestId, paidPayment.amount, now);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Send paid email failed. RequestId={RequestId}, OrderCode={OrderCode}",
                            orderRequestId, orderCode);
                    }
                }

                return (true, "Deposit payment recorded successfully. Request is Accepted.");
            });
        }

        private async Task<cost_estimate?> ResolveEstimateForPaymentAsync(
    order_request req,
    int? estimateId,
    CancellationToken ct = default)
        {
            if (estimateId.HasValue && estimateId.Value > 0)
            {
                var byPaymentEstimate = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.estimate_id == estimateId.Value, ct);

                if (byPaymentEstimate != null)
                    return byPaymentEstimate;
            }

            if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
            {
                var accepted = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.estimate_id == req.accepted_estimate_id.Value, ct);

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

        private async Task<(bool ok, string message)> ProcessRemainingPaidAsync(
    long orderCode,
    long amount,
    string? paymentLinkId,
    string? transactionId,
    string rawJson,
    CancellationToken ct)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var existing = await _db.payments
                    .FirstOrDefaultAsync(p => p.provider == "PAYOS" && p.order_code == orderCode, ct);

                if (existing == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Remaining payment row not found");
                }

                var now = AppTime.NowVnUnspecified();

                var req = await _requestService.GetRequestForUpdateAsync(existing.order_request_id, ct);
                if (req == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Order request not found for remaining payment");
                }

                if (!req.order_id.HasValue)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Order has not been created for remaining payment");
                }

                var est = await ResolveEstimateForPaymentAsync(req, existing.estimate_id, ct);
                if (est == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Accepted estimate not found for remaining payment");
                }

                var ord = await _db.orders
                    .FirstOrDefaultAsync(x => x.order_id == req.order_id.Value, ct);

                if (ord == null)
                {
                    await tx.RollbackAsync(ct);
                    return (false, "Order not found for remaining payment");
                }

                var prod = await _db.productions
                    .Where(x => x.order_id == ord.order_id)
                    .OrderByDescending(x => x.prod_id)
                    .FirstOrDefaultAsync(ct);

                var actualRemainingAmount = existing.amount > 0
                    ? existing.amount
                    : PaymentAmountHelper.GetRemainingAmount(est);

                req.process_status = "Paid";

                ord.status = "Paid";
                ord.payment_status = "Paid";

                if (prod != null)
                    prod.status = "Paid";

                existing.status = "PAID";
                existing.payment_type = "Remaining";
                existing.paid_at ??= now;
                existing.updated_at = now;

                existing.amount = actualRemainingAmount;

                if (!string.IsNullOrWhiteSpace(paymentLinkId))
                    existing.payos_payment_link_id = paymentLinkId;

                if (!string.IsNullOrWhiteSpace(transactionId))
                    existing.payos_transaction_id = transactionId;

                if (!string.IsNullOrWhiteSpace(rawJson))
                    existing.payos_raw = rawJson;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                await TryGenerateAndPersistReceiptAsync(
                    orderCode,
                    PaymentTypes.Remaining,
                    ct,
                    payosRawJson: rawJson,
                    forceGenerateWhenPayOsPaid: PayOsRawIndicatesPaid(rawJson));

                try
                {
                    await _dealService.NotifyRemainingPaidAsync(
                        ord.order_id,
                        actualRemainingAmount,
                        now,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Notify remaining paid failed. OrderId={OrderId}, OrderCode={OrderCode}",
                        ord.order_id,
                        orderCode);
                }

                return (true, $"Remaining payment recorded successfully: order_id={ord.order_id}");
            });
        }

        private async Task UpsertPaidPaymentRowAsync(
    int orderRequestId,
    long orderCode,
    decimal actualAmount,
    string? paymentLinkId,
    string? transactionId,
    string rawJson,
    int? estimateIdFromQuery,
    int? quoteIdFromQuery,
    string paymentType,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var existing = await _db.payments
                .FirstOrDefaultAsync(p => p.provider == "PAYOS" && p.order_code == orderCode, ct);

            if (existing != null)
            {
                existing.status = "PAID";
                existing.payment_type = paymentType;
                existing.paid_at ??= now;

                if (actualAmount > 0)
                    existing.amount = actualAmount;

                if (!string.IsNullOrWhiteSpace(paymentLinkId))
                    existing.payos_payment_link_id = paymentLinkId;

                if (!string.IsNullOrWhiteSpace(transactionId))
                    existing.payos_transaction_id = transactionId;

                if (!string.IsNullOrWhiteSpace(rawJson))
                    existing.payos_raw = rawJson;

                if ((existing.estimate_id == null || existing.estimate_id <= 0) &&
                    estimateIdFromQuery.HasValue && estimateIdFromQuery.Value > 0)
                {
                    existing.estimate_id = estimateIdFromQuery.Value;
                }

                if ((existing.quote_id == null || existing.quote_id <= 0) &&
                    quoteIdFromQuery.HasValue && quoteIdFromQuery.Value > 0)
                {
                    existing.quote_id = quoteIdFromQuery.Value;
                }

                existing.updated_at = now;

                await _db.SaveChangesAsync(ct);
                return;
            }

            var newPayment = new payment
            {
                order_request_id = orderRequestId,
                provider = "PAYOS",
                payment_type = paymentType,
                order_code = orderCode,
                amount = actualAmount,
                currency = "VND",
                status = "PAID",
                estimate_id = (estimateIdFromQuery.HasValue && estimateIdFromQuery.Value > 0)
                    ? estimateIdFromQuery.Value
                    : (int?)null,
                quote_id = (quoteIdFromQuery.HasValue && quoteIdFromQuery.Value > 0)
                    ? quoteIdFromQuery.Value
                    : (int?)null,
                paid_at = now,
                payos_payment_link_id = paymentLinkId,
                payos_transaction_id = transactionId,
                payos_raw = rawJson,
                created_at = now,
                updated_at = now
            };

            await _db.payments.AddAsync(newPayment, ct);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<PaymentReceiptResponseDto?> GetReceiptByOrderCodeAsync(long orderCode, CancellationToken ct = default)
        {
            if (orderCode <= 0)
                return null;

            var payment = await _db.payments
                .AsNoTracking()
                .Where(x => x.provider == "PAYOS" && x.order_code == orderCode)
                .OrderByDescending(x => x.payment_id)
                .FirstOrDefaultAsync(ct);

            if (payment == null)
                return null;
            var receiptStatus = await ResolveReceiptPaymentStatusAsync(
    payment,
    payosRawJson: null,
    forceGenerateWhenPayOsPaid: false,
    ct: ct);

            if (!receiptStatus.is_paid)
                return null;

            payment.status = receiptStatus.status;

            if (!payment.paid_at.HasValue)
                payment.paid_at = receiptStatus.paid_at;

            if (receiptStatus.amount.HasValue && receiptStatus.amount.Value > 0)
                payment.amount = receiptStatus.amount.Value;

            var req = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == payment.order_request_id, ct);

            if (req == null)
                return null;

            order? ord = null;
            if (req.order_id.HasValue && req.order_id.Value > 0)
            {
                ord = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == req.order_id.Value, ct);
            }

            int? resolvedEstimateId = payment.estimate_id ?? req.accepted_estimate_id;
            if ((!resolvedEstimateId.HasValue || resolvedEstimateId.Value <= 0) && req.order_request_id > 0)
            {
                resolvedEstimateId = await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == req.order_request_id)
                    .OrderByDescending(x => x.is_active)
                    .ThenByDescending(x => x.estimate_id)
                    .Select(x => (int?)x.estimate_id)
                    .FirstOrDefaultAsync(ct);
            }

            cost_estimate? est = null;
            if (resolvedEstimateId.HasValue && resolvedEstimateId.Value > 0)
            {
                est = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.estimate_id == resolvedEstimateId.Value, ct);
            }

            int? resolvedQuoteId = payment.quote_id ?? req.quote_id;

            var paidPayments = await _db.payments
                .AsNoTracking()
                .Where(x =>
                    x.order_request_id == payment.order_request_id &&
                    x.provider == "PAYOS" &&
                    (x.status == "PAID" || x.status == "SUCCESS"))
                .ToListAsync(ct);

            static DateTime GetSortTime(payment x)
                => (DateTime)x.paid_at;

            var orderedPaidPayments = paidPayments
                .OrderBy(GetSortTime)
                .ThenBy(x => x.payment_id)
                .ToList();

            decimal paidBeforeThisReceipt = 0m;
            foreach (var item in orderedPaidPayments)
            {
                if (item.payment_id == payment.payment_id)
                    break;

                paidBeforeThisReceipt += item.amount;
            }

            var amountReceived = payment.amount;
            var cumulativePaid = paidBeforeThisReceipt + amountReceived;

            var totalOrderValue = est?.final_total_cost
                                  ?? ord?.total_amount
                                  ?? amountReceived;

            var depositRequired = est?.deposit_amount ?? 0m;

            var remainingAfterThisReceipt = totalOrderValue - cumulativePaid;
            if (remainingAfterThisReceipt < 0)
                remainingAfterThisReceipt = 0m;

            var paymentTypeDisplay = string.Equals(payment.payment_type, PaymentTypes.Remaining, StringComparison.OrdinalIgnoreCase)
                ? "Thanh toán phần còn lại"
                : "Thanh toán tiền đặt cọc";

            var orderDisplayCode = !string.IsNullOrWhiteSpace(ord?.code)
                ? ord!.code
                : $"AM{req.order_request_id:D6}";

            var receiptDate = payment.paid_at ?? receiptStatus.paid_at;
            var receiptNo = $"PT-{receiptDate:yyyyMMdd}-{payment.payment_id:D6}";

            var receiptContent = string.Equals(payment.payment_type, PaymentTypes.Remaining, StringComparison.OrdinalIgnoreCase)
                ? $"Thu tiền thanh toán phần còn lại của đơn hàng {orderDisplayCode}"
                : $"Thu tiền đặt cọc của đơn hàng {orderDisplayCode}";

            var collectedBy = !string.IsNullOrWhiteSpace(req.assign_name)
                ? req.assign_name
                : "Hệ thống AMMS";

            var dto = new PaymentReceiptResponseDto
            {
                company_info = BuildReceiptCompanyInfo(),

                receipt_no = receiptNo,
                receipt_date = receiptDate,
                payment_id = payment.payment_id,
                provider = payment.provider,
                payment_type = payment.payment_type ?? "",
                payment_type_display = paymentTypeDisplay,
                payment_status = payment.status,
                currency = payment.currency,

                payos_order_code = payment.order_code,
                payos_payment_link_id = payment.payos_payment_link_id,
                payos_transaction_id = payment.payos_transaction_id,

                order_request_id = req.order_request_id,
                order_id = req.order_id,
                business_order_code = ord?.code,
                quote_id = resolvedQuoteId,
                estimate_id = resolvedEstimateId,

                customer_name = req.customer_name,
                customer_phone = req.customer_phone,
                customer_email = req.customer_email,
                customer_address = req.detail_address,

                product_name = req.product_name,
                quantity = req.quantity,

                receipt_content = receiptContent,
                collected_by = collectedBy,
                note = string.Equals(payment.payment_type, PaymentTypes.Remaining, StringComparison.OrdinalIgnoreCase)
                    ? "Phiếu thu thanh toán phần còn lại"
                    : "Phiếu thu thanh toán tiền đặt cọc",

                total_order_value = totalOrderValue,
                deposit_required = depositRequired,
                amount_received = amountReceived,
                amount_received_in_words = VietnameseMoneyTextHelper.ToVietnameseText(amountReceived),
                paid_before_this_receipt = paidBeforeThisReceipt,
                cumulative_paid = cumulativePaid,
                remaining_after_this_receipt = remainingAfterThisReceipt
            };

            return dto;
        }

        private PaymentReceiptCompanyDto BuildReceiptCompanyInfo()
        {
            return new PaymentReceiptCompanyDto
            {
                company_name = _config["Receipt:CompanyName"] ?? "CÔNG TY TNHH THƯƠNG MẠI DỊCH VỤ IN BAO BÌ ĐẠI PHÚC HẢI",
                address = _config["Receipt:Address"] ?? "Số 75 Nguyễn Công Trứ, Phường Lê Chân, Thành phố Hải Phòng, Việt Nam",
                phone = _config["Receipt:Phone"] ?? "02253250855",
                email = _config["Receipt:Email"] ?? "amms.printing@gmail.com",
                tax_code = _config["Receipt:TaxCode"] ?? "0201173299",
                bank_account = _config["Receipt:BankAccount"] ?? "123456789",
                bank_name = _config["Receipt:BankName"] ?? "BIDV - CN TP.HCM"
            };
        }

        public async Task<(byte[] FileBytes, string FileName, string ContentType)?> GenerateReceiptPdfByOrderCodeAsync(
    long orderCode,
    CancellationToken ct = default)
        {
            if (orderCode <= 0)
                return null;

            var payment = await _db.payments
                .AsNoTracking()
                .Where(x => x.provider == "PAYOS" && x.order_code == orderCode)
                .OrderByDescending(x => x.payment_id)
                .FirstOrDefaultAsync(ct);

            if (payment == null)
                return null;

            //if (!string.Equals(payment.status, "PAID", StringComparison.OrdinalIgnoreCase) &&
            //    !string.Equals(payment.status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            //{
            //    throw new InvalidOperationException("Payment has not been completed yet.");
            //}
            var receiptStatus = await ResolveReceiptPaymentStatusAsync(
    payment,
    payosRawJson: null,
    forceGenerateWhenPayOsPaid: false,
    ct: ct);

            if (!receiptStatus.is_paid)
                return null;

            payment.status = receiptStatus.status;

            if (!payment.paid_at.HasValue)
                payment.paid_at = receiptStatus.paid_at;

            if (receiptStatus.amount.HasValue && receiptStatus.amount.Value > 0)
                payment.amount = receiptStatus.amount.Value;

            if (string.IsNullOrWhiteSpace(payment.payos_raw) &&
                !string.IsNullOrWhiteSpace(receiptStatus.payos_raw))
            {
                payment.payos_raw = receiptStatus.payos_raw;
            }

            if (string.IsNullOrWhiteSpace(payment.payos_payment_link_id) &&
                !string.IsNullOrWhiteSpace(receiptStatus.payment_link_id))
            {
                payment.payos_payment_link_id = receiptStatus.payment_link_id;
            }

            if (string.IsNullOrWhiteSpace(payment.payos_transaction_id) &&
                !string.IsNullOrWhiteSpace(receiptStatus.transaction_id))
            {
                payment.payos_transaction_id = receiptStatus.transaction_id;
            }

            var request = await _db.order_requests
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_request_id == payment.order_request_id, ct);

            if (request == null)
                throw new InvalidOperationException("Order request not found.");

            order? order = null;
            if (request.order_id.HasValue && request.order_id.Value > 0)
            {
                order = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == request.order_id.Value, ct);
            }

            int? resolvedEstimateId = payment.estimate_id ?? request.accepted_estimate_id;
            cost_estimate? estimate = null;

            if (resolvedEstimateId.HasValue && resolvedEstimateId.Value > 0)
            {
                estimate = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.estimate_id == resolvedEstimateId.Value, ct);
            }

            if (estimate == null)
            {
                estimate = await _db.cost_estimates
                    .AsNoTracking()
                    .Where(x => x.order_request_id == request.order_request_id)
                    .OrderByDescending(x => x.is_active)
                    .ThenByDescending(x => x.estimate_id)
                    .FirstOrDefaultAsync(ct);
            }

            var allPaidPayments = await _db.payments
    .AsNoTracking()
    .Where(x =>
        x.order_request_id == request.order_request_id &&
        x.provider == "PAYOS" &&
        (
            x.status == "PAID" ||
            x.status == "SUCCESS"
        ))
    .ToListAsync(ct);

            static DateTime GetSortTime(payment x)
            {
                DateTime? value = x.paid_at;

                if (!value.HasValue)
                    value = x.updated_at;

                if (!value.HasValue)
                    value = x.created_at;

                return value ?? DateTime.MinValue;
            }

            var orderedPaidPayments = allPaidPayments
                .OrderBy(GetSortTime)
                .ThenBy(x => x.payment_id)
                .ToList();

            decimal paidBeforeThisReceipt = 0m;
            foreach (var item in orderedPaidPayments)
            {
                if (item.payment_id == payment.payment_id)
                    break;

                paidBeforeThisReceipt += item.amount;
            }

            var totalOrderValue = estimate?.final_total_cost
                                  ?? order?.total_amount
                                  ?? payment.amount;

            var cumulativePaid = paidBeforeThisReceipt + payment.amount;
            var remainingAfterThisReceipt = totalOrderValue - cumulativePaid;
            if (remainingAfterThisReceipt < 0m)
                remainingAfterThisReceipt = 0m;

            var consultantName = string.IsNullOrWhiteSpace(request.assign_name)
                ? "Tư vấn viên lập phiếu"
                : request.assign_name.Trim();

            var receiptDate = payment.paid_at ?? receiptStatus.paid_at;
            var receiptNo = $"PT-{receiptDate:yyyyMMdd}-{payment.payment_id:D6}";

            var company = new ReceiptCompanyInfo
            {
                CompanyName = _config["Receipt:CompanyName"] ?? "CÔNG TY TNHH THƯƠNG MẠI DỊCH VỤ IN BAO BÌ ĐẠI PHÚC HẢI",
                Address = _config["Receipt:Address"] ?? "",
                Phone = _config["Receipt:Phone"] ?? "",
                Email = _config["Receipt:Email"] ?? "",
                TaxCode = _config["Receipt:TaxCode"] ?? "",
                BankAccount = _config["Receipt:BankAccount"] ?? "",
                BankName = _config["Receipt:BankName"] ?? ""
            };

            var templatePath = ResolveReceiptTemplatePath();

            var placeholders = PaymentReceiptDocxHelper.BuildPlaceholders(
                request,
                payment,
                order,
                estimate,
                company,
                receiptDate,
                receiptNo,
                consultantName,
                paidBeforeThisReceipt,
                remainingAfterThisReceipt);

            var templateBytes = await File.ReadAllBytesAsync(templatePath, ct);

            // tạo docx
            var generatedDocxBytes = PaymentReceiptDocxHelper.GenerateDocx(templateBytes, placeholders);

            var fileName = $"phieu-thu-{payment.order_code}.docx";
            var contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

            return (generatedDocxBytes, fileName, contentType);
        }

        private string ResolveReceiptTemplatePath()
        {
            var candidates = new[]
            {
        Path.Combine(_env.ContentRootPath, "Templates", "PhieuThuTemplate.docx"),
        Path.Combine(AppContext.BaseDirectory, "Templates", "PhieuThuTemplate.docx")
    };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            throw new FileNotFoundException(
                "Receipt template not found. Checked paths: " + string.Join(" | ", candidates));
        }

        private static bool IsSuccessfulPaymentStatus(string? status)
        {
            var st = (status ?? "").Trim().ToUpperInvariant();

            return st is "PAID" or "SUCCESS";
        }

        private static bool IsPaidStatusFromText(string? status)
        {
            var st = (status ?? "").Trim().ToUpperInvariant();

            return st is "PAID" or "SUCCESS";
        }

        private static bool PayOsRawIndicatesPaid(string? rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var dataNode))
                {
                    var dataStatus = dataNode.TryGetProperty("status", out var st)
                        ? st.GetString()
                        : null;

                    var dataCode = dataNode.TryGetProperty("code", out var dc)
                        ? dc.GetString()
                        : null;

                    var dataDesc = dataNode.TryGetProperty("desc", out var dd)
                        ? dd.GetString()
                        : null;

                    if (IsPaidStatusFromText(dataStatus))
                        return true;

                    if (string.Equals(dataCode, "00", StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (!string.IsNullOrWhiteSpace(dataDesc) &&
                        dataDesc.Contains("thành công", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                var rootStatus = root.TryGetProperty("status", out var rs)
                    ? rs.GetString()
                    : null;

                if (IsPaidStatusFromText(rootStatus))
                    return true;

                var rootCode = root.TryGetProperty("code", out var rc)
                    ? rc.GetString()
                    : null;

                var rootSuccess = root.TryGetProperty("success", out var s)
                                  && s.ValueKind == JsonValueKind.True;

                if (rootSuccess && string.Equals(rootCode, "00", StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task TryGenerateAndPersistReceiptAsync(
    long orderCode,
    string paymentType,
    CancellationToken ct = default,
    string? payosRawJson = null,
    bool forceGenerateWhenPayOsPaid = false)
        {
            try
            {
                var payment = await _db.payments
                    .AsNoTracking()
                    .Where(x => x.provider == "PAYOS" && x.order_code == orderCode)
                    .OrderByDescending(x => x.payment_id)
                    .FirstOrDefaultAsync(ct);

                if (payment == null)
                    return;

                var receiptStatus = await ResolveReceiptPaymentStatusAsync(
                    payment,
                    payosRawJson,
                    forceGenerateWhenPayOsPaid,
                    ct);

                if (!receiptStatus.is_paid)
                    return;

                var request = await _db.order_requests
                    .FirstOrDefaultAsync(x => x.order_request_id == payment.order_request_id, ct);

                if (request == null)
                    return;

                var generated = await GenerateReceiptPdfByOrderCodeAsync(orderCode, ct);

                if (generated == null)
                    return;

                var extension = Path.GetExtension(generated.Value.FileName);
                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".docx";

                var normalizedPaymentType = string.Equals(
                    paymentType,
                    PaymentTypes.Remaining,
                    StringComparison.OrdinalIgnoreCase)
                    ? PaymentTypes.Remaining
                    : PaymentTypes.Deposit;

                var fileName = normalizedPaymentType == PaymentTypes.Remaining
                    ? $"phieu-thu-con-lai-AM{request.order_request_id:D6}{extension}"
                    : $"phieu-thu-dat-coc-AM{request.order_request_id:D6}{extension}";

                var publicId = normalizedPaymentType == PaymentTypes.Remaining
                    ? $"receipts/request_{request.order_request_id}/remaining_receipt"
                    : $"receipts/request_{request.order_request_id}/deposit_receipt";

                await using var ms = new MemoryStream(generated.Value.FileBytes);

                var url = await _cloudinaryStorage.UploadRawWithPublicIdAsync(
                    ms,
                    fileName,
                    generated.Value.ContentType,
                    publicId);

                if (normalizedPaymentType == PaymentTypes.Remaining)
                    request.remaining_receipt_path = url;
                else
                    request.deposit_receipt_path = url;

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Auto generate/save payment receipt failed. OrderCode={OrderCode}, PaymentType={PaymentType}",
                    orderCode,
                    paymentType);
            }
        }

        private static DateTime ResolveReceiptPaidAt(payment payment)
        {
            DateTime? resolved = payment.paid_at;

            if (!resolved.HasValue)
                resolved = payment.updated_at;

            if (!resolved.HasValue)
                resolved = payment.created_at;

            return resolved ?? AppTime.NowVnUnspecified();
        }

        private async Task<ReceiptPaymentStatusContext> ResolveReceiptPaymentStatusAsync(
            payment payment,
            string? payosRawJson = null,
            bool forceGenerateWhenPayOsPaid = false,
            CancellationToken ct = default)
        {
            var dbPaid = IsSuccessfulPaymentStatus(payment.status);

            var rawPaid =
                forceGenerateWhenPayOsPaid ||
                PayOsRawIndicatesPaid(payosRawJson) ||
                PayOsRawIndicatesPaid(payment.payos_raw);

            PayOsResultDto? liveInfo = null;
            var livePaid = false;

            if (!dbPaid && !rawPaid)
            {
                try
                {
                    liveInfo = await _payOs.GetPaymentLinkInformationAsync(payment.order_code, ct);
                    livePaid = IsSuccessfulPaymentStatus(liveInfo?.status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Cannot verify PayOS status before receipt generation. OrderCode={OrderCode}",
                        payment.order_code);
                }
            }

            var isPaid = dbPaid || rawPaid || livePaid;

            var resolvedStatus =
                dbPaid ? payment.status :
                rawPaid ? "PAID" :
                livePaid ? liveInfo?.status ?? "PAID" :
                payment.status ?? "PENDING";

            decimal? resolvedAmount = null;

            if (payment.amount > 0)
                resolvedAmount = payment.amount;
            else if (liveInfo?.amount.HasValue == true)
                resolvedAmount = liveInfo.amount.Value;

            return new ReceiptPaymentStatusContext
            {
                is_paid = isPaid,
                status = resolvedStatus,
                paid_at = ResolveReceiptPaidAt(payment),
                amount = resolvedAmount,
                payos_raw = liveInfo?.raw_json ?? payosRawJson ?? payment.payos_raw,
                payment_link_id = liveInfo?.payment_link_id ?? payment.payos_payment_link_id,
                transaction_id = liveInfo?.transaction_id ?? payment.payos_transaction_id
            };
        }

        private sealed class ReceiptPaymentStatusContext
        {
            public bool is_paid { get; init; }
            public string status { get; init; } = "PENDING";
            public DateTime paid_at { get; init; }
            public decimal? amount { get; init; }
            public string? payos_raw { get; init; }
            public string? payment_link_id { get; init; }
            public string? transaction_id { get; init; }
        }
    }
}
