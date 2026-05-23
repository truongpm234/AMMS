using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class GenerateImportReceiveBatchResponse
    {
        public bool success { get; set; }
        public int order_id { get; set; }
        public string? order_code { get; set; }

        public int total_productions { get; set; }
        public int generated_count { get; set; }

        public List<GenerateImportReceiveResponse> files { get; set; } = new();

        public string? message { get; set; }
    }
}
