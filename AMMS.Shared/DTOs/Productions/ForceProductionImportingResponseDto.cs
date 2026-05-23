using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ForceProductionImportingResponseDto
    {
        public bool success { get; set; }

        public int prod_id { get; set; }

        public string? production_code { get; set; }

        public string production_status { get; set; } = "Importing";

        public List<int> order_ids { get; set; } = new();

        public int updated_order_count { get; set; }

        public int updated_request_count { get; set; }

        public DateTime? importing_at { get; set; }

        public string message { get; set; } = "";
    }
}