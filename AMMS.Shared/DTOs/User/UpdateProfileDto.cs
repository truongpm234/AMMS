using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.User
{
    public class UpdateProfileDto
    {
        public string? full_name { get; set; }
        public string? phone_number { get; set; }
        public string? address { get; set; }
        public string? email { get; set; }
    }
}
