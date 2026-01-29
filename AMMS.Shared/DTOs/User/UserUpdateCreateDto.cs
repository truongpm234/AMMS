namespace AMMS.Shared.DTOs.User
{
    public class UserUpdateCreateDto
    {
        public string? user_name { get; set; }
        public string? user_email { get; set; }
        public string? user_password { get; set; }
        public string? user_phone { get; set; }
        public string? full_name { get; set; }
        public int? role_id { get; set; }
        public bool? is_active { get; set; }
    }
}
