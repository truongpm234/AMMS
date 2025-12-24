using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Suppliers
{
    public record SupplierMaterialDto(
        int MaterialId,
        string Code,
        string Name,
        string Unit,
        bool IsActive,
        string? Note
    );
}

