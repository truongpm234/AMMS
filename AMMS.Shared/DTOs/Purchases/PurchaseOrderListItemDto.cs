using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public sealed record PurchaseOrderListItemDto(
        int purchase_id,
        string? code,
        string supplier_name,
        DateTime? created_at,
        string cretae_by_name,
        decimal total_quantity,
        DateTime? eta_date,
        string status,
        string? received_by_name,
        string? unit_summary
    );
}