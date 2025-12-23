using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderLookupSendOtpRequest
    {
        public string Phone { get; set; } = null!;
    }
}
