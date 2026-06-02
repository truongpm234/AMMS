using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class SuggestionOrderPreviewDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int single_prod_id { get; set; }

        public int? product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public string? product_name { get; set; }

        public int quantity { get; set; }

        public string? production_process { get; set; }

        public string? production_method { get; set; }

        public DateTime? delivery_date { get; set; }
    }

    public class SuggestionBatchPreviewDto
    {
        public string batch_type { get; set; } = "";
        // SINGLE / GROUP / SPLIT

        public string prod_kind { get; set; } = "";
        // SINGLE / GROUP / SPLIT

        public string department_code { get; set; } = "";

        public string department_name { get; set; } = "";

        public List<int> order_ids { get; set; } = new();

        public List<string?> order_codes { get; set; } = new();

        public List<string> process_codes { get; set; } = new();

        public DateTime planned_start_date { get; set; }

        public DateTime planned_end_date { get; set; }

        public int duration_days { get; set; }

        public List<SuggestionTaskPreviewDto> tasks { get; set; } = new();

        public string? note { get; set; }
    }

    public class SuggestionTaskPreviewDto
    {
        public string process_code { get; set; } = "";

        public string process_name { get; set; } = "";

        public string department_code { get; set; } = "";

        public string department_name { get; set; } = "";

        public string? machine { get; set; }

        public int? seq_num { get; set; }

        public DateTime planned_start_time { get; set; }

        public DateTime planned_end_time { get; set; }
    }
}
