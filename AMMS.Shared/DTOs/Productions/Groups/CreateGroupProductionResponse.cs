using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class CreateGroupProductionResponse
    {
        public int group_prod_id { get; set; }

        public string? group_code { get; set; }

        public int product_type_id { get; set; }

        public int total_qty { get; set; }

        public List<int> order_ids { get; set; } = new();

        public List<string> process_codes { get; set; } = new();

        public List<int> group_task_ids { get; set; } = new();

        public List<int> group_prod_ids { get; set; } = new();

        public List<int> split_prod_ids { get; set; } = new();

        public List<int> all_created_prod_ids { get; set; } = new();

        public List<GroupProductionPlanWarningDto> warnings { get; set; } = new();

        public string message { get; set; } = "";

        public string? code { get; set; }

    }
}
