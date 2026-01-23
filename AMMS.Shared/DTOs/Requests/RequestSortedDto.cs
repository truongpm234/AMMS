using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Requests
{
    public record RequestSortedDto(
    int request_id,
    string customer_name,
    string phone,
    string? email,
    DateTime? delivery_date,
    string product_name,
    int quantity,
    string? status,
    DateTime? request_date
);
}
