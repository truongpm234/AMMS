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
    [Table("estimate_config", Schema = "AMMS_DB")]
    public class estimate_config
    {
        [Key, Column("config_group", Order = 0)]
        [MaxLength(100)]
        public string config_group { get; set; } = null!;

        [Key, Column("config_key", Order = 1)]
        [MaxLength(120)]
        public string config_key { get; set; } = null!;

        [Column("value_num", TypeName = "numeric(18,6)")]
        public decimal? value_num { get; set; }

        [Column("value_text")]
        public string? value_text { get; set; }

        [Column("value_bool")]
        public bool? value_bool { get; set; }

        [Column("value_json", TypeName = "jsonb")]
        public string? value_json { get; set; }

        [Column("updated_at")]
        public DateTime updated_at { get; set; }
    }
}
