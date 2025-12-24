using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Suppliers
{
    public class SupplierWithMaterialsDto
    {
        public int supplier_id { get; set; }
        public string name { get; set; } = null!;
        public string? contact_person { get; set; }
        public string? phone { get; set; }
        public string? email { get; set; }
        public string? main_material_type { get; set; }
    }
}
