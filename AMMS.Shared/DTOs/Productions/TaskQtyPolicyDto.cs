using System;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskQtyPolicyDto
    {
        public int task_id { get; set; }

        public string process_code { get; set; } = "";
        public string process_name { get; set; } = "";

        public string qty_unit { get; set; } = "sp";

        public int min_allowed { get; set; } = 1;
        public int max_allowed { get; set; } = 1;
        public int suggested_qty { get; set; } = 1;

        public int happy_case_qty { get; set; } = 1;

        public int order_qty { get; set; }
        public int sheets_required { get; set; }
        public int sheets_waste { get; set; }
        public int sheets_total { get; set; }
        public int n_up { get; set; }
        public int number_of_plates { get; set; }

        public int stage_index { get; set; }
        public int stage_count { get; set; }

        /*
         * Output policy:
         * Dùng để FE biết công đoạn này nên báo cáo ra bao nhiêu.
         * Với SUB/BOTH downstream: production_output_qty tính theo sp + hao hụt còn lại.
         */
        public int production_output_qty { get; set; }
        public string production_output_unit { get; set; } = "sp";

        /*
         * Manual input policy:
         * Dùng cho QR prepare/createQr để FE biết task có được nhập tay NVL/BTP/output không.
         */
        public string? input_mode { get; set; }

        public bool allow_manual_input { get; set; }
        public bool can_use_manual_input { get; set; }
        public bool manual_input_optional { get; set; }

        /*
         * Group/Split metadata:
         */
        public bool is_group_production { get; set; }
        public bool is_split_production { get; set; }

        public int? group_prod_id { get; set; }
        public int? split_prod_id { get; set; }

        public int group_total_qty { get; set; }

        /*
         * Hint để FE hiển thị hoặc debug.
         */
        public string? manual_input_hint { get; set; }
    }
}