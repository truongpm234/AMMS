using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CreateTaskQrRequest
    {
        public int task_id { get; set; }
        public int ttl_minutes { get; set; } = 60;
    }

}
