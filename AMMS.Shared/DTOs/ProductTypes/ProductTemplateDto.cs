using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTypes
{
    public class ProductTemplateDto
    {
        public int design_profile_id { get; set; }
        public string template_code { get; set; } = "";
        public string? template_name { get; set; }

        public int? product_length_mm { get; set; }
        public int? product_width_mm { get; set; }
        public int? product_height_mm { get; set; }
        public int? glue_tab_mm { get; set; }
        public int? bleed_mm { get; set; }

        public int? print_width_mm { get; set; }
        public int? print_height_mm { get; set; }

        public string? coating_type { get; set; }
        public string? wave_type { get; set; }
        public int? number_of_plates { get; set; }

        public bool is_active { get; set; }
    }
}
