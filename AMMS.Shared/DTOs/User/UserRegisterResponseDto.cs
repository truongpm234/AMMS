namespace AMMS.Shared.DTOs.User
{
    public class UserRegisterResponseDto
    {
        public string status { get; set; }

        public int user_id { get; set; }

        public int? role_id { get; set; }

        public string full_name { get; set; }
    }
}
