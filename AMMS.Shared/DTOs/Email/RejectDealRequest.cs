using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Email
{
    public class RejectDealRequest
    {
        public int order_request_id { get; set; }
        public string token { get; set; } = null!;
        public string reason { get; set; } = null!;
        public string? phone { get; set; }
        public string? otp { get; set; }
    }

}
