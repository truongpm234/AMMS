using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTemplates
{
    public class ProductTemplateDto
    {
        public int design_profile_id { get; set; }

        public int product_type_id { get; set; }

        public string template_code { get; set; } = null!;

        public string? template_name { get; set; }

        public string? description { get; set; }

        public int? product_length_mm { get; set; }

        public int? product_width_mm { get; set; }

        public int? product_height_mm { get; set; }

        public int? glue_tab_mm { get; set; }

        public int? bleed_mm { get; set; }

        public bool? is_one_side_box { get; set; }

        public int? number_of_plates { get; set; }

        public string? coating_type { get; set; }

        public string? paper_code { get; set; }

        public string? paper_name { get; set; }

        public string? wave_type { get; set; }

        public int? print_width_mm { get; set; }

        public int? print_height_mm { get; set; }

        public string? production_processes { get; set; }

        public int? default_quantity { get; set; }

        public bool is_active { get; set; }

        public DateTime created_at { get; set; }

        public DateTime? updated_at { get; set; }
    }
}

