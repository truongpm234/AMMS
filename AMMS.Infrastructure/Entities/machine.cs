using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Entities
{
    [Table("machines", Schema = "AMMS_DB")]
    public partial class machine
    {
        public int machine_id { get; set; }
        public string process_name { get; set; } = null!;
        public string machine_code { get; set; } = null!;
        public bool is_active { get; set; } = true;
    }
}
