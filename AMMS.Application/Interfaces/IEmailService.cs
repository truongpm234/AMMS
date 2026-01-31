namespace AMMS.Application.Interfaces
{
    public interface IEmailService
    {
        Task SendAsync(string toEmail, string subject, string htmlContent);
        Task SendOtpAsync(string email);
        Task<bool> VerifyOtpAsync(string email, string otp);

        Task SendMailResetPassword(string email);
    }
}

