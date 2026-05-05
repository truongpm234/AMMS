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
        Task<order_request?> GetByOderIdAsync(int id);
        Task CancelAsync(int id, CancellationToken ct = default);
        Task MarkProcessStatusFinishedByOrderAsync(int orderId, int? quoteId, CancellationToken ct = default);
        Task<int> SaveChangesAsync();
        Task<int> CountAsync();
        Task<List<RequestPagedDto>> GetPagedAsync(int skip, int takePlusOne, int? consultantUserId = null);
        Task<RequestPagedDto?> GetByOrderIdAsync(int orderId);
        Task<RequestWithCostDto?> GetByIdWithCostAsync(int id, int? consultantUserId = null);
        Task<bool> AnyOrderLinkedAsync(int requestId);
        Task<bool> HasEnoughStockForRequestAsync(int requestId, CancellationToken ct = default);
        Task<PagedResultLite<RequestEmailStatsDto>> GetEmailsByAcceptedCountPagedAsync(
            int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<RequestStockCoverageDto>> GetSortedByStockCoveragePagedAsync(
            int page, int pageSize, int? consultantUserId = null, CancellationToken ct = default);
        Task<string?> GetEmailByPhoneAsync(string phone, CancellationToken ct = default);
        Task<PagedResultLite<OrderListDto>> GetOrdersByPhonePagedAsync(
            string phone, int page, int pageSize, CancellationToken ct = default);
        Task<string?> GetDesignFilePathAsync(int orderRequestId, int? consultantUserId = null, CancellationToken ct = default);
        Task<PagedResultLite<RequestSortedDto>> GetRequestsByPhonePagedAsync(
            string phone, int page, int pageSize, int? consultantUserId = null, CancellationToken ct = default);
        Task<RequestDetailDto?> GetInformationRequestById(
            int requestId, int? consultantUserId = null, CancellationToken ct = default);
        Task<int> DeleteDesignFilePathByRequestIdAsync(int orderRequestId, CancellationToken ct = default);
        Task<RequestWithTwoEstimatesDto?> GetActiveEstimatesInProcessAsync(
            int requestId, int? consultantUserId = null, CancellationToken ct = default);
        Task<List<cost_estimate>> GetActiveEstimatesWithProcessesByRequestIdAsync(
            int requestId, int? consultantUserId = null, CancellationToken ct = default);
        Task<bool> TryMarkDealWaitingFromVerifiedAsync(int requestId, CancellationToken ct = default);
        Task<order_request?> GetRequestForUpdateAsync(int orderRequestId, CancellationToken ct);
        Task<int?> GetLeastLoadedConsultantUserIdAsync(CancellationToken ct = default);
        Task<bool> CanConsultantAccessRequestAsync(int requestId, int consultantUserId, CancellationToken ct = default);
        Task<bool> UpdateDeliveryNoteAsync(int orderRequestId, string note, CancellationToken ct = default);
        Task<order_request?> GetByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<DateTime?> CalculateAsync(int orderRequestId, CancellationToken ct = default);
        Task<DateTime?> RecalculateAndPersistAsync(int orderRequestId, CancellationToken ct = default);
        Task MarkProcessStatusImportingByOrderAsync(int orderId, int? quoteId, CancellationToken ct = default);
        Task<decimal> GetVatPercentAsync(CancellationToken ct = default);
    }
}