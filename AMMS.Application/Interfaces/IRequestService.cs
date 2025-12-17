using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IRequestService
    {
        Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req);
        Task<UpdateOrderRequestResponse> UpdateAsync(int id, UpdateOrderRequest req);
        Task DeleteAsync(int id);
        Task<order_request?> GetByIdAsync(int id);
    }
}
