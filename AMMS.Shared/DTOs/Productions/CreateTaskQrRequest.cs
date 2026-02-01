using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CreateTaskQrRequest
    {
        public int task_id { get; set; }
        [DefaultValue(10)]
        public int ttl_minutes { get; set; }
        [DefaultValue(null)]
        public int? qty_good { get; set; }
    }

}
