namespace AMMS.Shared.DTOs.User
{
    public class UserLoginResponseDto
    {
        public int user_id { get; set; }

        public int? role_id { get; set; }

        public string full_name { get; set; }
    }
}
