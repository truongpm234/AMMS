using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IRequestService
    {
        Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req);
        Task UpdateAsync(int id, CreateCustomerOrderResquest req);
        Task DeleteAsync(int id);
    }
}
