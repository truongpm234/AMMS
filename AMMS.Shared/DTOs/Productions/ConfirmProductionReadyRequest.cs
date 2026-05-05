using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ConfirmProductionReadyRequest
    {
        public bool is_production_ready { get; set; }
    }

    public class ProductionReadyCheckResponse
    {
        public int order_id { get; set; }
        public bool is_production_ready { get; set; }
        public bool has_enough_material { get; set; }
        public bool has_free_machine { get; set; }
        public int? production_id { get; set; }
        public int? product_type_id { get; set; }
        public int? request_print_width_mm { get; set; }
        public int? request_print_length_mm { get; set; }
        public int? order_quantity { get; set; }
        public bool is_full_process { get; set; } = true;
        public bool has_matched_sub_product { get; set; }
        public string? sub_product_message { get; set; }
        public int? selected_sub_product_id { get; set; }
        public int sub_product_used_qty { get; set; }
        public MatchedSubProductDto? matched_sub_product { get; set; }
        public List<ProductionReadyMaterialDto> materials { get; set; } = new();
        public List<ProductionReadyMachineDto> machines { get; set; } = new();
    }

    public class ProductionReadyMaterialDto
    {
        public int? material_id { get; set; }
        public string? material_code { get; set; }
        public string? material_name { get; set; }
        public string? unit { get; set; }

        public decimal required_qty { get; set; }
        public decimal available_qty { get; set; }
        public decimal missing_qty { get; set; }

        public bool is_enough { get; set; }

        // Enough | Missing | Unmapped
        public string status { get; set; } = "Missing";
    }

    public class ProductionReadyMachineDto
    {
        public int? process_id { get; set; }
        public int? seq_num { get; set; }
        public string? process_code { get; set; }
        public string? process_name { get; set; }

        public string? machine_code { get; set; }

        public bool machine_found { get; set; }
        public bool is_available { get; set; }

        public int total_quantity { get; set; }
        public int busy_quantity { get; set; }
        public int free_quantity { get; set; }

        // Available | Busy | Unmapped
        public string status { get; set; } = "Busy";
    }
}
