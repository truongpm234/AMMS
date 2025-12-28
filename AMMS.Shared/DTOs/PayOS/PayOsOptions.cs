using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.PayOS
{
    public sealed class PayOsOptions
    {
        public string ClientId { get; set; } = null!;
        public string ApiKey { get; set; } = null!;
        public string ChecksumKey { get; set; } = null!;
        public string BaseUrl { get; set; } = "https://api-merchant.payos.vn";
        public string WebhookUrl { get; set; } = "";
    }

}
