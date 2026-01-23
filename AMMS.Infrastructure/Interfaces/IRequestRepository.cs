using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
namespace AMMS.Infrastructure.Interfaces
{
    public interface IRequestRepository
    {
        Task AddAsync(order_request entity);
        Task UpdateAsync(order_request entity);
        Task<order_request?> GetByIdAsync(int id);
        Task CancelAsync(int id, CancellationToken ct = default);
        Task<int> SaveChangesAsync();
        Task<int> CountAsync();
        Task<List<RequestPagedDto>> GetPagedAsync(int skip, int take);
        Task<bool> AnyOrderLinkedAsync(int requestId);
        Task<bool> HasEnoughStockForRequestAsync(int requestId, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetSortedByQuantityPagedAsync(
    bool ascending, int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetSortedByDatePagedAsync(
    bool ascending, int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetSortedByDeliveryDatePagedAsync(
    bool nearestFirst, int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestEmailStatsDto>> GetEmailsByAcceptedCountPagedAsync(
    int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestStockCoverageDto>> GetSortedByStockCoveragePagedAsync(
    int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetByOrderRequestDatePagedAsync(
    DateOnly date, int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> SearchPagedAsync(
    string keyword, int page, int pageSize, CancellationToken ct = default);
        Task<string?> GetEmailByPhoneAsync(string phone, CancellationToken ct = default);
        Task<PagedResultLite<OrderListDto>> GetOrdersByPhonePagedAsync(
            string phone, int page, int pageSize, CancellationToken ct = default);
        Task<string?> GetDesignFilePathAsync(int orderRequestId, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetRequestsByPhonePagedAsync(
    string phone, int page, int pageSize, CancellationToken ct = default);

    }
}