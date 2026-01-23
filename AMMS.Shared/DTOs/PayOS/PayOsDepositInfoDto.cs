using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.PayOS
{
    public sealed class PayOsDepositInfoDto
    {
        public long order_code { get; set; }
        public string checkout_url { get; set; } = null!;
        public DateTime expire_at { get; set; }
    }
}
