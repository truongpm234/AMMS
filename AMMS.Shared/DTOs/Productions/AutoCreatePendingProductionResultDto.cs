using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class AutoCreatePendingProductionResultDto
    {
        public bool created { get; set; }

        public int order_id { get; set; }

        public int? prod_id { get; set; }

        public string? method { get; set; }

        public int method_count { get; set; }

        public List<string> available_methods { get; set; } = new();

        public string reason { get; set; } = "";
    }
}
