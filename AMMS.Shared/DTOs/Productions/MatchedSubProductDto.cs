using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class MatchedSubProductDto
    {
        public int id { get; set; }

        public int product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public bool is_active { get; set; }

        public string? description { get; set; }

        public DateTime? updated_at { get; set; }
        public bool is_imported { get; set; }

        public string? import_file { get; set; }

        public string? paper_material_code { get; set; }

        public string? wave_material_code { get; set; }

        public string? coating_material_code { get; set; }

        public string? lamination_material_code { get; set; }

        public string? material_signature { get; set; }

        public int? cost_estimate_id { get; set; }

        public decimal unit_cost_to_stage { get; set; }

        public decimal total_cost_to_stage { get; set; }

        public string? match_reason { get; set; }
    }
}
