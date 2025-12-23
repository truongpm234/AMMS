using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IOrderService
    {
        Task<order> GetOrderByCodeAsync(string code);
        Task<order> GetByIdAsync(int id);
        Task<PagedResultLite<OrderListDto>> GetPagedAsync(int page, int pageSize);
    }
}