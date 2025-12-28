using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.PayOS
{
    public sealed class PayOsPaymentInfo
    {
        public string? status { get; set; }         
        public long? amount { get; set; }
        public string? paymentLinkId { get; set; }
        public string? transactionId { get; set; }
        public string rawJson { get; set; } = "{}";
    }
}
