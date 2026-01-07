namespace AMMS.Shared.DTOs.User
{
    public class UserRegisterRequestDto
    {
        required
        public string user_name
        { get; set; }

        required
        public string email
        { get; set; }

        required
        public string phone_number
        { get; set; }
        required
        public string password
        { get; set; }
        required
        public string full_name
        { get; set; }

    }
}
