namespace AMMS.Application.Helpers
{
    public class ResetPasswordMail
    {
        public static string Subject => "[MES] Yêu cầu đặt lại mật khẩu";

        public static string GetHtmlBody(
            string fullName,
            string resetLink,
            int expiredMinutes = 30
        )
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <title>Đặt lại mật khẩu</title>
</head>
<body style='margin:0; padding:0; background-color:#f4f6f8; font-family:Arial, Helvetica, sans-serif;'>
    <table width='100%' cellpadding='0' cellspacing='0'>
        <tr>
            <td align='center'>
                <table width='600' cellpadding='0' cellspacing='0' style='background:#ffffff; margin:40px 0; border-radius:8px; overflow:hidden;'>
                    
                    <!-- Header -->
                    <tr>
                        <td style='background:#2563eb; padding:20px; color:#ffffff; text-align:center;'>
                            <h2 style='margin:0;'>Hệ thống AMMS</h2>
                        </td>
                    </tr>

                    <!-- Nội dung -->
                    <tr>
                        <td style='padding:30px; color:#333333;'>
                            <p>Xin chào <strong>{fullName}</strong>,</p>

                            <p>
                                Chúng tôi đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản AMMS của bạn.
                            </p>

                            <p style='text-align:center; margin:30px 0;'>
                                <a href='{resetLink}'
                                   style='background:#2563eb;
                                          color:#ffffff;
                                          text-decoration:none;
                                          padding:14px 24px;
                                          border-radius:6px;
                                          display:inline-block;
                                          font-weight:bold;'>
                                    Đặt lại mật khẩu
                                </a>
                            </p>

                            <p>
                                Liên kết này sẽ hết hạn sau <strong>{expiredMinutes} phút</strong>.
                                Nếu bạn không yêu cầu đặt lại mật khẩu, vui lòng bỏ qua email này.
                            </p>

                            <p style='margin-top:30px;'>
                                Trân trọng,<br/>
                                <strong>Đội ngũ hỗ trợ AMMS</strong>
                            </p>
                        </td>
                    </tr>

                    <!-- Footer -->
                    <tr>
                        <td style='background:#f1f5f9; padding:15px; text-align:center; font-size:12px; color:#64748b;'>
                            © {DateTime.Now.Year} AMMS. Mọi quyền được bảo lưu.
                        </td>
                    </tr>

                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
        }
    }
}
