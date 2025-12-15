using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Domain
{
    public interface IOrderRepository
    {
        Task<CreateCustomerOrderResponse> CreateCustomerOrderAsync(CreateCustomerOrderRequest req);
    }
}
