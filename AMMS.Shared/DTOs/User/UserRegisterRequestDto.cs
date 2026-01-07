namespace AMMS.Shared.DTOs.User
{
    public class UserRegisterRequestDto
    {
        public string user_name { get; set; }

        required
        public string email
        { get; set; }

        public string password { get; set; }

        public string full_name { get; set; }

    }
}
