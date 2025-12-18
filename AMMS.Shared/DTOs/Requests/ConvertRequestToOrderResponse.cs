using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Requests
{
    public class ConvertRequestToOrderResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int RequestId { get; set; }
        public int? OrderId { get; set; }
        public int? OrderItemId { get; set; }
        public string? OrderCode { get; set; }
    }
}
