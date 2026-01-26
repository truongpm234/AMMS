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

            if (!res.IsSuccessStatusCode)
            {
                // Nếu lỗi là do OrderCode đã tồn tại (code "231" hoặc message tương tự), 
                // ta có thể ném lỗi đặc biệt để tầng trên xử lý, hoặc cứ throw exception chung.
                // Ở đây throw exception để log ra lỗi chi tiết.
                throw new PayOsException($"PayOS Error: {rawResponse}");
            }

            using var doc = JsonDocument.Parse(rawResponse);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                throw new PayOsException("PayOS response missing data");

            var result = new PayOsResultDto
            {
                checkoutUrl = GetString(data, "checkoutUrl"),
                qr_code = GetString(data, "qrCode"),
                account_number = GetString(data, "accountNumber"),
                account_name = GetString(data, "accountName"),
                bin = GetString(data, "bin"),
                amount = amount,
                status = "PENDING",
                description = description,
                payment_link_id = GetString(data, "paymentLinkId"),
                transaction_id = null
            };

            if (string.IsNullOrEmpty(result.checkoutUrl))
                throw new PayOsException("PayOS response missing checkoutUrl");

            return result;
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

            return new PayOsResultDto
            {
                status = GetString(data, "status"),
                amount = data.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number ? am.GetInt32() : 0,
                checkoutUrl = GetString(data, "checkoutUrl"),
                qr_code = GetString(data, "qrCode"),
                account_number = GetString(data, "accountNumber"),
                account_name = GetString(data, "accountName"),
                bin = GetString(data, "bin"),
                description = GetString(data, "description"),
                payment_link_id = GetString(data, "paymentLinkId"),
                transaction_id = GetString(data, "transactionId") ?? GetString(data, "reference")
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
