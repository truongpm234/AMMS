using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AMMS.Application.Extensions
{
    public class SmtpEmailSender : IEmailService
    {
        private readonly EmailSettings _settings;

        public SmtpEmailSender(IOptions<EmailSettings> options)
        {
            _settings = options.Value;
        }

        public async Task SendAsync(string toEmail, string subject, string htmlContent)
        {
            Console.WriteLine("===== SMTP SEND EMAIL =====");
            Console.WriteLine($"Host: '{_settings.Host}'");
            Console.WriteLine($"Port: {_settings.Port}");
            Console.WriteLine($"From: {_settings.FromName} <{_settings.FromEmail}>");
            Console.WriteLine($"To: {toEmail}");
            Console.WriteLine($"Subject: {subject}");

            if (string.IsNullOrWhiteSpace(_settings.Host))
                throw new Exception("Smtp:Host missing");
            if (_settings.Port <= 0)
                throw new Exception("Smtp:Port missing/invalid");
            if (string.IsNullOrWhiteSpace(_settings.Username))
                throw new Exception("Smtp:Username missing");
            if (string.IsNullOrWhiteSpace(_settings.Password))
                throw new Exception("Smtp:Password missing");
            if (string.IsNullOrWhiteSpace(_settings.FromEmail))
                throw new Exception("Smtp:FromEmail missing");
            if (string.IsNullOrWhiteSpace(_settings.FromName))
                _settings.FromName = "AMMS";

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;

            message.Body = new BodyBuilder
            {
                HtmlBody = htmlContent,
                TextBody = "This email contains HTML content."
            }.ToMessageBody();

            using var smtp = new SmtpClient();

            // ✅ Tránh treo lâu trên Render (mặc định có thể rất lâu)
            smtp.Timeout = 20000; // 20s

            // ✅ Gmail app password không cần XOAUTH2
            smtp.AuthenticationMechanisms.Remove("XOAUTH2");

            // ✅ Quan trọng: Port 465 dùng SSL-on-connect, 587 dùng STARTTLS
            var secureOption = _settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            try
            {
                Console.WriteLine($"[SMTP] Connecting using {secureOption}...");
                await smtp.ConnectAsync(_settings.Host, _settings.Port, secureOption);

                Console.WriteLine("[SMTP] Connected. Authenticating...");
                await smtp.AuthenticateAsync(_settings.Username, _settings.Password);

                Console.WriteLine("[SMTP] Authenticated. Sending...");
                await smtp.SendAsync(message);

                Console.WriteLine("[SMTP] Sent. Disconnecting...");
                await smtp.DisconnectAsync(true);

                Console.WriteLine("SMTP EMAIL SENT SUCCESS");
            }
            catch (Exception ex)
            {
                Console.WriteLine("SMTP FAILED:");
                Console.WriteLine(ex.GetType().FullName);
                Console.WriteLine(ex.Message);
                throw;
            }
        }
    }
}
