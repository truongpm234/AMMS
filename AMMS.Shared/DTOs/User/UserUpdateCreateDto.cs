using System.ComponentModel.DataAnnotations;

namespace AMMS.Shared.DTOs.User
{
    public class UserUpdateCreateDto
    {
        [Required]
        public string user_name { get; set; }
        [Required]
        public string user_email { get; set; }
        [Required]
        public string user_password { get; set; }
        [Required]
        public string? user_phone { get; set; }
        public string? full_name { get; set; }
        [Required]
        public int role_id { get; set; }
        public bool is_active { get; set; }
    }
}
