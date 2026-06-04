using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class GenerateImportReceiveFileResultDto
    {
        public bool success { get; set; }

        public int order_id { get; set; }
        public string order_code { get; set; } = string.Empty;

        public List<int> prod_ids { get; set; } = new();

        public string file_name { get; set; } = string.Empty;
        public string content_type { get; set; } = "application/pdf";

        public byte[] file_bytes { get; set; } = Array.Empty<byte>();

        public string message { get; set; } = string.Empty;
    }
}
