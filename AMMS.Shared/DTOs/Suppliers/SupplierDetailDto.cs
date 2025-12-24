using AMMS.Shared.DTOs.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Suppliers {
    public class SupplierDetailDto
    {
        public int supplier_id { get; init; }
        public string name { get; init; } = null!;
        public string? contact_person { get; init; }
        public string? phone { get; init; }
        public string? email { get; init; }
        public string? main_material_type { get; init; }
        public PagedResultLite<SupplierMaterialDto> Materials { get; init; }
    }
}
