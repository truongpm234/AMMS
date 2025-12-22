using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Email
{
    public class VerifyOtpRequest
    {
        public string email { get; set; } = null!;
        public string otp { get; set; } = null!;
    }
}

