using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Requests
{
    public class PhoneHistoryWithOtpResult
    {
        public PagedResultLite<OrderListDto> Orders { get; set; } = new();
        public PagedResultLite<RequestSortedDto> Requests { get; set; } = new();
    }
}
