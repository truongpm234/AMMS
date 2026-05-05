using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class SetProductionMethodRequest
    {
        public int order_id { get; set; }
        public bool is_full_process { get; set; }
        public int? sub_id { get; set; }
    }
}
