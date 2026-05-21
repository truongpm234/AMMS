using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Entities
{
    [Table("sub_product", Schema = "AMMS_DB")]
    public class sub_product
    {
        public int id { get; set; }

        public int product_type_id { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public bool is_active { get; set; } = true;

        public bool is_imported { get; set; } = true;

        public string? import_file { get; set; }

        public int? source_task_id { get; set; }

        public int? source_task_log_id { get; set; }

        public int? source_prod_id { get; set; }

        public int? source_order_id { get; set; }

        public string? description { get; set; }

        public DateTime? updated_at { get; set; }

        public virtual product_type product_type { get; set; } = null!;
    }
}