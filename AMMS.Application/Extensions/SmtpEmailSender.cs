using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MailKit.Net.Smtp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            Console.WriteLine($"Host: {_settings.Host}");
            Console.WriteLine($"Port: {_settings.Port}");
            Console.WriteLine($"To: {toEmail}");

            if (string.IsNullOrWhiteSpace(_settings.Username) ||
                string.IsNullOrWhiteSpace(_settings.Password))
            {
                throw new Exception("SMTP config missing. Check appsettings or Render ENV.");
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;

            message.Body = new BodyBuilder
            {
                HtmlBody = htmlContent,
                TextBody = "HTML email"
            }.ToMessageBody();

            using var smtp = new SmtpClient();

            await smtp.ConnectAsync(
                _settings.Host,
                _settings.Port,
                SecureSocketOptions.StartTls
            );

            await smtp.AuthenticateAsync(
                _settings.Username,
                _settings.Password
            );

            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            Console.WriteLine("SMTP EMAIL SENT SUCCESS");
        }
    }
}
