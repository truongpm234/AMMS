using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskMaterialUsageInputDto
    {
        public int material_id { get; set; }
        public decimal quantity_used { get; set; }
        public bool is_stock { get; set; } = true;
        public decimal quantity_left { get; set; }
        public string material_code { get; set; }

        public string material_name { get; set; }

        public string unit { get; set; }
    }
}
