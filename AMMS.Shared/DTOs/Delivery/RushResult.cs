using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Delivery
{
    public class RushResult
    {
        public bool IsRush { get; set; }
        public decimal RushPercent { get; set; }
        public decimal RushAmount { get; set; }
        public int DaysEarly { get; set; }
    }
}
