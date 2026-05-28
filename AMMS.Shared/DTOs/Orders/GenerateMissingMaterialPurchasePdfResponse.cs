using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class GenerateMissingMaterialPurchasePdfResponse
    {
        public bool success { get; set; }
        public string file_name { get; set; } = "";
        public string file_url { get; set; } = "";
        public int total_rows { get; set; }
        public int updated_file_purpose_rows { get; set; }
        public decimal total_amount { get; set; }
        public string message { get; set; } = "";
    }
}

