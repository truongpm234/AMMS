using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Helpers
{
    using System.Text.Json;
    using AMMS.Infrastructure.Entities;
    using AMMS.Shared.DTOs.PayOS;

    public static class PayOsRawMapper
    {
        public static PayOsResultDto FromPayment(payment p)
        {
            var dto = new PayOsResultDto
            {
                order_code = p.order_code,
                amount = (int?)p.amount,
                status = p.status,
                payment_link_id = p.payos_payment_link_id,
                transaction_id = p.payos_transaction_id,
                raw_json = p.payos_raw
            };

            if (string.IsNullOrWhiteSpace(p.payos_raw)) return dto;

            try
            {
                using var doc = JsonDocument.Parse(p.payos_raw);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : root;

                dto.check_out_url = GetString(data, "checkoutUrl");
                dto.qr_code = GetString(data, "qrCode");
                dto.account_number = GetString(data, "accountNumber");
                dto.account_name = GetString(data, "accountName");
                dto.bin = GetString(data, "bin");
                dto.description = GetString(data, "description");

                dto.payment_link_id = GetString(data, "paymentLinkId") ?? dto.payment_link_id;
                dto.transaction_id = GetString(data, "transactionId") ?? GetString(data, "reference") ?? dto.transaction_id;

                if (data.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number)
                    dto.amount = am.GetInt32();

                if (data.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                    dto.status = st.GetString();

                if (data.TryGetProperty("orderCode", out var oc) && oc.ValueKind == JsonValueKind.Number)
                    dto.order_code = oc.GetInt64();
            }
            catch { }

            return dto;
        }

        private static string? GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

}
