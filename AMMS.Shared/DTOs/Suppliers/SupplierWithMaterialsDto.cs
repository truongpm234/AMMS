using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Suppliers
{
    public class SupplierWithMaterialsDto
    {
        public int SupplierId { get; set; }
        public string Name { get; set; } = null!;
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? MainMaterialType { get; set; }

        public List<SupplierMaterialBasicDto> Materials { get; set; } = new();
    }
}
