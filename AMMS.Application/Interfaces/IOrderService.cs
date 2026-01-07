using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Interfaces
{
    public interface IOrderService
    {
        Task<order> GetOrderByCodeAsync(string code);
        Task<order> GetByIdAsync(int id);

        Task<OrderDetailDto?> GetDetailAsync(int id, CancellationToken ct = default);

        Task<PagedResultLite<OrderResponseDto>> GetPagedAsync(int page, int pageSize);

        Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(
            int page,
            int pageSize,
            CancellationToken ct = default);

        Task<string> DeleteDesignFilePath(int orderRequestId);

        Task<List<order>> GetAllOrderWithStatusInProcess();
    }
}