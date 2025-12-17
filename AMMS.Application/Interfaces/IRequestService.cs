using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Order_requests;

namespace AMMS.Application.Interfaces
{
    public interface IRequestService
    {
        Task<CreateRequestResponse> CreateAsync(CreateResquest req);
        Task<UpdateRequestResponse> UpdateAsync(int id, UpdateOrderRequest req);
        Task DeleteAsync(int id);
        Task<order_request?> GetByIdAsync(int id);
        Task<PagedResultLite<order_request>> GetPagedAsync(int page, int pageSize);
    }
}
