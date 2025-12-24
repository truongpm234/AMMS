using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public sealed record UpdateStatusBuyMaterialDto(
        int PurchaseId,
        string? Code,
        string SupplierName,
        DateTime? CreatedAt,
        string CreatedByName,
        decimal TotalQuantity,
        bool CanFullfill 
    );
}
