using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskQrResponse
    {
        public int task_id { get; set; }
        public string token { get; set; } = "";
        public long expires_at_unix { get; set; }
        public int qty_good_used { get; set; }
        public bool is_auto_filled { get; set; }
    }
}
