using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IOrderRepository
    {
        Task<List<OrderResponseDto>> GetPagedWithFulfillAsync(int skip, int take, CancellationToken ct = default);
        Task AddOrderAsync(order entity);
        Task AddOrderItemAsync(order_item entity);
        void Update(order entity);
        Task<order?> GetByIdAsync(int id);
        Task<order?> GetByCodeAsync(string code);
        Task<List<OrderListDto>> GetPagedAsync(int skip, int take);
        Task<int> CountAsync();
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
        Task<string> GenerateNextOrderCodeAsync();
        Task<OrderDetailDto?> GetDetailByIdAsync(int orderId, CancellationToken ct = default);
        Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(
            int page,
            int pageSize,
            CancellationToken ct = default);
        Task<string> DeleteDesignFilePath(int orderRequestId);
        Task<object> BuyMaterialAndRecalcOrdersAsync(
            int materialId,
            decimal quantity,
            int managerUserId,
            CancellationToken ct = default);
        Task<List<order>> GetAllOrderInprocessStatus();

    }
}