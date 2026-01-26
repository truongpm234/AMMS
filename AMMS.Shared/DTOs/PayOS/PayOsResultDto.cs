using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.PayOS
{
    public class PayOsResultDto
    {
        public string? checkoutUrl { get; set; }
        public string? qr_code { get; set; }
        public string? account_number { get; set; }
        public string? account_name { get; set; }
        public int? amount { get; set; }
        public string? status { get; set; }
        public string? description { get; set; }
        public string? bin { get; set; }
        public string? payment_link_id { get; set; }
        public string? transaction_id { get; set; }
        public string? raw_json { get; set; }
    }
}
