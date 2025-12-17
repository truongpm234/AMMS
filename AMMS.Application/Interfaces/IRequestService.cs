using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IRequestService
    {
        Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req);
        Task<UpdateOrderRequestResponse> UpdateAsync(int id, UpdateOrderRequest req);
        Task DeleteAsync(int id);
    }
}
