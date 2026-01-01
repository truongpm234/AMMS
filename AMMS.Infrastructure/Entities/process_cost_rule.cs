using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    [Table("process_cost_rule", Schema = "AMMS_DB")]
    public class process_cost_rule
    {
        [Key]
        [Column("process_code")]
        [MaxLength(50)]
        public string process_code { get; set; } = null!;  

        [Column("process_name")]
        [MaxLength(255)]
        public string? process_name { get; set; }

        [Column("unit")]
        [MaxLength(20)]
        public string unit { get; set; } = null!;           

        [Column("unit_price", TypeName = "numeric(18,2)")]
        public decimal unit_price { get; set; }

        [Column("note")]
        public string? note { get; set; }
    }
}

