using AMMS.Application.Interfaces;
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

        public async Task<string> CreatePaymentLinkAsync(
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
            var dataToSign =
                $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";

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
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadFromJsonAsync<PayOsCreateResponse>(cancellationToken: ct);
            if (json?.data?.checkoutUrl == null)
                throw new Exception("PayOS response missing checkoutUrl");

            return json.data.checkoutUrl;
        }

        public async Task<PayOsPaymentInfo?> GetPaymentLinkInformationAsync(long orderCode, CancellationToken ct = default)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{_opt.BaseUrl}/v2/payment-requests/{orderCode}");
            msg.Headers.Add("x-client-id", _opt.ClientId);
            msg.Headers.Add("x-api-key", _opt.ApiKey);

            var res = await _http.SendAsync(msg, ct);
            if (!res.IsSuccessStatusCode) return null;

            var raw = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return new PayOsPaymentInfo { rawJson = raw };

            string? status = data.TryGetProperty("status", out var st) ? st.GetString() : null;
            long? amount = data.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number ? am.GetInt64() : null;
            string? paymentLinkId = data.TryGetProperty("paymentLinkId", out var pl) ? pl.GetString() : null;
            string? transactionId = data.TryGetProperty("transactionId", out var tx) ? tx.GetString() : null;

            return new PayOsPaymentInfo
            {
                status = status,
                amount = amount,
                paymentLinkId = paymentLinkId,
                transactionId = transactionId,
                rawJson = raw
            };
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
