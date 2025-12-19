using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Email
{
    public class RejectDealRequest
    {
        public int RequestId { get; set; }
        public string Token { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
