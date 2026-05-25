using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class CreateSubProductDto
    {
        public int product_type_id { get; set; }
        public int? width { get; set; }
        public int? length { get; set; }
        public string? product_process { get; set; }
        public int? quantity { get; set; }
        public bool? is_active { get; set; }
        public string? description { get; set; }
        public string? paper_material_code { get; set; }
        public string? wave_material_code { get; set; }
        public string? coating_material_code { get; set; }
        public string? lamination_material_code { get; set; }
        public decimal? unit_cost_to_stage { get; set; }
    }
}
