using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ProductionGroupInfoDto
    {
        public int group_id { get; set; }

        public int group_prod_id { get; set; }

        public string? group_code { get; set; }

        public string? group_status { get; set; }

        public string? group_process_codes { get; set; }

        public int group_total_qty { get; set; }

        public int? product_type_id { get; set; }

        public DateTime? group_created_at { get; set; }

        public DateTime? group_planned_start_date { get; set; }

        public DateTime? group_actual_start_date { get; set; }

        public DateTime? group_end_date { get; set; }

        public bool is_active_group { get; set; }

        public ProdOrderInfoDto? current_prod_order { get; set; }

        public List<ProdOrderInfoDto> prod_orders { get; set; } = new();
    }
}
