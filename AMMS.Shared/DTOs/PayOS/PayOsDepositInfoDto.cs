using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.PayOS
{
    public sealed class PayOsDepositInfoDto
    {
        public decimal deposit_amount;
        public long order_code { get; set; }
        public string checkout_url { get; set; } = null!;
        public DateTime expire_at { get; set; }
        public string? qr_code { get; set; }
        public string? account_number { get; set; }
        public string? account_name { get; set; }
        public string? bin { get; set; }
    }
}
