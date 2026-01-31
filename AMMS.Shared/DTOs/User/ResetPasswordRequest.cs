namespace AMMS.Shared.DTOs.User
{
    public class ResetPasswordRequest
    {

        public string token { get; set; }
        public string email { get; set; }
        public string new_password { get; set; }
    }
}
