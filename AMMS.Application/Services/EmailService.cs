using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class EmailService : IEmailService
    {
        private readonly SendGridSettings _settings;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;

        public EmailService(IOptions<SendGridSettings> options, IConfiguration config, IMemoryCache cache)
        {
            _settings = options.Value;
            _config = config;
            _cache = cache;
        }

        public async Task SendAsync(string toEmail, string subject, string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new Exception("SendGrid:ApiKey missing");

            var client = new SendGridClient(_settings.ApiKey);

            var from = new EmailAddress(_settings.FromEmail, _settings.FromName);
            var to = new EmailAddress(toEmail);

            var msg = MailHelper.CreateSingleEmail(
                from,
                to,
                subject,
                plainTextContent: null,
                htmlContent: htmlContent
            );

            var response = await client.SendEmailAsync(msg);

            if ((int)response.StatusCode >= 400)
            {
                var body = await response.Body.ReadAsStringAsync();
                throw new Exception($"SendGrid failed: {response.StatusCode} - {body}");
            }
        }
        private string CacheKey(string email) => $"OTP::{NormalizeEmail(email)}";

        private static string NormalizeEmail(string email)
            => email.Trim().ToLowerInvariant();

        private int ExpiryMinutes =>
            int.TryParse(_config["Otp:ExpiryMinutes"], out var m) ? m : 5;

        private int MaxAttempts =>
            int.TryParse(_config["Otp:MaxAttempts"], out var a) ? a : 5;

        private static string GenerateOtp6()
        {
            // crypto-safe 000000-999999
            var b = RandomNumberGenerator.GetBytes(4);
            var v = BitConverter.ToUInt32(b, 0) % 1_000_000;
            return v.ToString("D6");
        }

        private static string Sha256(string input)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash);
        }

        private sealed class OtpCacheModel
        {
            public string OtpHash { get; set; } = null!;
            public DateTime ExpiresAtUtc { get; set; }
            public int Attempts { get; set; }
        }

        public async Task SendOtpAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("email is required");

            email = NormalizeEmail(email);

            var otp = GenerateOtp6();
            var expiresAt = DateTime.UtcNow.AddMinutes(ExpiryMinutes);

            var model = new OtpCacheModel
            {
                OtpHash = Sha256($"{email}:{otp}"),
                ExpiresAtUtc = expiresAt,
                Attempts = 0
            };

            _cache.Set(
                CacheKey(email),
                model,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ExpiryMinutes)
                });

            var html = $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:520px;margin:auto;color:#333'>
  <h2 style='margin:0'>Mã OTP xác thực</h2>
  <p style='margin:10px 0 0;color:#666'>OTP có hiệu lực trong <b>{ExpiryMinutes} phút</b>.</p>

  <div style='margin:18px 0;padding:14px;border:1px solid #eee;border-radius:8px;text-align:center'>
    <div style='font-size:28px;letter-spacing:6px;font-weight:700'>{otp}</div>
  </div>

  <p style='font-size:12px;color:#888'>Nếu bạn không yêu cầu mã này, hãy bỏ qua email.</p>
</div>";

            await SendAsync(email, "OTP xác thực AMMS", html);
        }

        public Task<bool> VerifyOtpAsync(string email, string otp)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
                return Task.FromResult(false);

            email = NormalizeEmail(email);
            otp = otp.Trim();

            if (!_cache.TryGetValue(CacheKey(email), out OtpCacheModel? model) || model == null)
                return Task.FromResult(false);

            if (DateTime.UtcNow > model.ExpiresAtUtc)
            {
                _cache.Remove(CacheKey(email));
                return Task.FromResult(false);
            }

            model.Attempts++;
            if (model.Attempts > MaxAttempts)
            {
                _cache.Remove(CacheKey(email));
                return Task.FromResult(false);
            }

            var hash = Sha256($"{email}:{otp}");
            var ok = string.Equals(hash, model.OtpHash, StringComparison.OrdinalIgnoreCase);

            if (!ok)
            {
                _cache.Set(CacheKey(email), model, model.ExpiresAtUtc);
                return Task.FromResult(false);
            }

            _cache.Remove(CacheKey(email));
            return Task.FromResult(true);
        }
    }
}
