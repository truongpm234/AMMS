using AMMS.Domain.Entities;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IOrderService
    {
        Task<CreateCustomerOrderResponse> CreateAsync(CreateCustomerOrderResquest req);
        Task UpdateAsync(int id, CreateCustomerOrderResquest req);
        Task DeleteAsync(int id);
    }
}
