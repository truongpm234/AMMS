using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    namespace AMMS.Infrastructure.Entities
    {
        [Table("missing_materials", Schema = "AMMS_DB")]
        public class missing_material
        {
            [Key]
            [Column("miss_id")]
            public long miss_id { get; set; }

            [Required]
            [Column("material_id")]
            public int material_id { get; set; }

            [Required]
            [Column("material_name")]
            public string material_name { get; set; } = "";

            [Column("needed", TypeName = "numeric(18,4)")]
            public decimal needed { get; set; } = 0m;

            [Column("available", TypeName = "numeric(18,4)")]
            public decimal available { get; set; } = 0m;

            [Column("quantity", TypeName = "numeric(18,4)")]
            public decimal quantity { get; set; } = 0m;

            [Required]
            [Column("unit")]
            public string unit { get; set; } = "";

            [Column("request_date")]
            public DateTime? request_date { get; set; }

            [Column("total_price", TypeName = "numeric(18,2)")]
            public decimal total_price { get; set; } = 0m;

            [Column("is_buy")]
            public bool is_buy { get; set; } = false;

            [Column("created_at")]
            public DateTime created_at { get; set; }
        }
    }

}
