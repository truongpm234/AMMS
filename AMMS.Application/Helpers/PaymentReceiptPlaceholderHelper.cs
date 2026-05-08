using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.Constants;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public sealed class ReceiptCompanyInfo
    {
        public string CompanyName { get; set; } = "";
        public string Address { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Email { get; set; } = "";
        public string TaxCode { get; set; } = "";
        public string BankAccount { get; set; } = "";
        public string BankName { get; set; } = "";
    }

    public static class PaymentReceiptPlaceholderHelper
    {
        public static Dictionary<string, string> BuildPlaceholders(
            order_request request,
            payment payment,
            order? order,
            cost_estimate? estimate,
            ReceiptCompanyInfo company,
            DateTime receiptDate,
            string receiptNo,
            string consultantName,
            decimal paidBeforeThisReceipt,
            decimal remainingAfterThisReceipt)
        {
            var requestCode = $"AM{request.order_request_id:D6}";

            var orderCode = !string.IsNullOrWhiteSpace(order?.code)
                ? order!.code
                : requestCode;

            var paymentTypeDisplay = string.Equals(
                payment.payment_type,
                PaymentTypes.Remaining,
                StringComparison.OrdinalIgnoreCase)
                ? "Thanh toán phần còn lại"
                : "Thanh toán tiền đặt cọc";

            var totalOrderValue =
                estimate?.final_total_cost
                ?? order?.total_amount
                ?? payment.amount;

            var reason = BuildReceiptReason(request, payment, orderCode);

            var paymentAmountText = ContractDocxHelper.NumberToVietnameseText(
                (long)Math.Round(payment.amount, 0, MidpointRounding.AwayFromZero));

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["{{COMPANY_NAME}}"] = company.CompanyName,
                ["{{COMPANY_ADDRESS}}"] = company.Address,
                ["{{COMPANY_PHONE}}"] = company.Phone,
                ["{{COMPANY_EMAIL}}"] = company.Email,
                ["{{COMPANY_TAX_CODE}}"] = company.TaxCode,
                ["{{COMPANY_BANK_ACCOUNT}}"] = company.BankAccount,
                ["{{COMPANY_BANK_NAME}}"] = company.BankName,

                ["{{RECEIPT_NO}}"] = receiptNo,
                ["{{RECEIPT_DAY}}"] = receiptDate.Day.ToString(),
                ["{{RECEIPT_MONTH}}"] = receiptDate.Month.ToString(),
                ["{{RECEIPT_YEAR}}"] = receiptDate.Year.ToString(),

                ["{{PAYER_NAME}}"] = request.customer_name ?? "",
                ["{{PAYER_ADDRESS}}"] = request.detail_address ?? "",

                ["{{RECEIPT_REASON}}"] = reason,

                ["{{REQUEST_CODE}}"] = requestCode,
                ["{{ORDER_CODE}}"] = orderCode,
                ["{{QUOTE_ID}}"] = payment.quote_id?.ToString() ?? request.quote_id?.ToString() ?? "",
                ["{{ESTIMATE_ID}}"] = payment.estimate_id?.ToString() ?? request.accepted_estimate_id?.ToString() ?? "",

                ["{{PRODUCT_NAME}}"] = request.product_name ?? "",
                ["{{QUANTITY}}"] = FormatNumber(request.quantity ?? 0),
                ["{{PAYMENT_TYPE_DISPLAY}}"] = paymentTypeDisplay,
                ["{{PAYMENT_METHOD}}"] = "Chuyển khoản ngân hàng",

                ["{{PAYMENT_AMOUNT}}"] = FormatNumber(payment.amount),
                ["{{PAYMENT_AMOUNT_TEXT}}"] = paymentAmountText,

                ["{{TOTAL_ORDER_VALUE}}"] = FormatNumber(totalOrderValue),
                ["{{PAID_BEFORE}}"] = FormatNumber(paidBeforeThisReceipt),
                ["{{REMAINING_AFTER}}"] = FormatNumber(remainingAfterThisReceipt),

                ["{{TRANSACTION_ID}}"] = payment.payos_transaction_id ?? "",
                ["{{PAYOS_ORDER_CODE}}"] = payment.order_code.ToString(),

                ["{{PAYER_SIGN_NAME}}"] = request.customer_name ?? "",
                ["{{CONSULTANT_SIGN_NAME}}"] = consultantName
            };
        }

        private static string BuildReceiptReason(order_request request, payment payment, string orderCode)
        {
            var productName = string.IsNullOrWhiteSpace(request.product_name)
                ? "đơn hàng in ấn"
                : request.product_name.Trim();

            if (string.Equals(payment.payment_type, PaymentTypes.Remaining, StringComparison.OrdinalIgnoreCase))
            {
                return $"Thu tiền thanh toán phần còn lại của đơn hàng {orderCode} - {productName}.";
            }

            return $"Thu tiền đặt cọc cho đơn hàng {orderCode} - {productName}.";
        }

        public static string FormatNumber(decimal value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);
        }

        public static string FormatNumber(int value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);
        }
    }
}

