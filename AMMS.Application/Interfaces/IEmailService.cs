using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IEmailService
    {
        Task SendAsync(string toEmail, string subject, string htmlContent);
        Task SendOtpAsync(string email);
        Task<bool> VerifyOtpAsync(string email, string otp);
    }
}

