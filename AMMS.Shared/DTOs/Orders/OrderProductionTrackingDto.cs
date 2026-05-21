using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderProductionTrackingDto
    {
        public int? request_id { get; set; }

        public int order_id { get; set; }
        public string? order_status { get; set; }

        public List<ProductionTrackingDto> productions { get; set; } = new();
    }
}
