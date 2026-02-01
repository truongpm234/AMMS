using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IOrderMaterialService
    {
        Task<OrderMaterialsResponse?> GetMaterialsByOrderIdAsync(int orderId, CancellationToken ct = default);
    }

}