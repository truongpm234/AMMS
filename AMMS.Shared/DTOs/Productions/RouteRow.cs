using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class RouteRow
    {
        public int ProductTypeId { get; set; }
        public int SeqNum { get; set; }
        public string ProcessName { get; set; } = "";
    }
}
