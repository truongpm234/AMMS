using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Exceptions.AMMS.Application.Exceptions;
using AMMS.Shared.DTOs.PayOS;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AMMS.Application.Services
{
    public sealed class PayOsService : IPayOsService
    {
        private readonly HttpClient _http;
        private readonly PayOsOptions _opt;

        public PayOsService(HttpClient http, IOptions<PayOsOptions> opt)
        {
            _http = http;
            _opt = opt.Value;
        }

        public async Task<PayOsResultDto> CreatePaymentLinkAsync(
    int orderCode,
    int amount,
    string description,
    string buyerName,
    string buyerEmail,
    string buyerPhone,
    string returnUrl,
    string cancelUrl,
    CancellationToken ct = default)
        {
            var dataToSign = $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";
            var signature = HmacSha256Hex(_opt.ChecksumKey, dataToSign);

            var req = new
            {
                orderCode,
                amount,
                description,
                buyerName,
                buyerEmail,
                buyerPhone,
                cancelUrl,
                returnUrl,
                signature
            };

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v2/payment-requests");
            msg.Headers.Add("x-client-id", _opt.ClientId);
            msg.Headers.Add("x-api-key", _opt.ApiKey);
            msg.Content = JsonContent.Create(req);

            var res = await _http.SendAsync(msg, ct);
            var rawResponse = await res.Content.ReadAsStringAsync(ct);

            // ✅ PayOS có thể trả 200 nhưng success=false; hoặc trả lỗi nhưng vẫn có data
            using var doc = JsonDocument.Parse(rawResponse);

            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "") : "";
            var hasData = root.TryGetProperty("data", out var data);

            // ✅ Case "code=00" có data => coi như OK
            if (code == "00" && hasData)
                return MapPayOsDataToDto(data, orderCode, description, amount, rawResponse);

            // Nếu HTTP fail mà vẫn có data thì vẫn map (tuỳ bạn muốn strict hay not)
            if (hasData && !string.Equals(code, "", StringComparison.Ordinal))
            {
                // Một số case PayOS vẫn kèm data, ưu tiên trả dto để FE có link
                return MapPayOsDataToDto(data, orderCode, description, amount, rawResponse);
            }

            // ❌ Không có data => throw
            throw new PayOsException($"PayOS Error: {rawResponse}");
        }

        private static PayOsResultDto MapPayOsDataToDto(JsonElement data, int orderCode, string description, int amount, string raw)
        {
            string? GetString(string name) =>
                data.TryGetProperty(name, out var p) ? p.GetString() : null;

            int? GetInt(string name)
            {
                if (!data.TryGetProperty(name, out var p)) return null;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var s)) return s;
                return null;
            }

            long? GetLong(string name)
            {
                if (!data.TryGetProperty(name, out var p)) return null;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
                return null;
            }

            var dto = new PayOsResultDto
            {
                order_code = GetLong("orderCode") ?? orderCode,
                check_out_url = GetString("checkoutUrl"),
                qr_code = GetString("qrCode"),
                account_number = GetString("accountNumber"),
                account_name = GetString("accountName"),
                bin = GetString("bin"),
                amount = GetInt("amount") ?? amount,
                status = GetString("status") ?? "PENDING",
                description = GetString("description") ?? description,
                payment_link_id = GetString("paymentLinkId"),
                transaction_id = GetString("transactionId") ?? GetString("reference"),
                raw_json = raw
            };

            if (string.IsNullOrWhiteSpace(dto.check_out_url))
                throw new PayOsException("PayOS response missing checkoutUrl");

            return dto;
        }

        public async Task<PayOsResultDto?> GetPaymentLinkInformationAsync(long orderCode, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{_opt.BaseUrl}/v2/payment-requests/{orderCode}");
            msg.Headers.Add("x-client-id", _opt.ClientId);
            msg.Headers.Add("x-api-key", _opt.ApiKey);

            var res = await _http.SendAsync(msg, ct);
            if (!res.IsSuccessStatusCode) return null;

            var raw = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;

            int? amount = null;
            if (data.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number)
                amount = am.TryGetInt32(out var v) ? v : null;

            return new PayOsResultDto
            {
                order_code = orderCode,     // ✅
                raw_json = raw,             // ✅

                status = GetString(data, "status"),
                amount = amount,

                check_out_url = GetString(data, "checkoutUrl"),
                qr_code = GetString(data, "qrCode"),
                account_number = GetString(data, "accountNumber"),
                account_name = GetString(data, "accountName"),
                bin = GetString(data, "bin"),
                description = GetString(data, "description"),
                payment_link_id = GetString(data, "paymentLinkId"),
                transaction_id = GetString(data, "transactionId") ?? GetString(data, "reference"),
            };
        }


        private string? GetString(JsonElement element, string propName)
        {
            return element.TryGetProperty(propName, out var prop) ? prop.GetString() : null;
        }

        private static string HmacSha256Hex(string key, string data)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
