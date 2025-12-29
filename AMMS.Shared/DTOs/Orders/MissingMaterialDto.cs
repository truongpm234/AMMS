using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class MissingMaterialDto
    {
        public int material_id { get; set; } 
        public string? material_name { get; set; }
        public decimal needed { get; set; }
        public decimal available { get; set; }
        public decimal quantity { get; set; }     
        public string? unit { get; set; }         
        public DateTime? request_date { get; set; }
        public decimal total_price { get; set; }
    }
}
