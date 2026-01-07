using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepo;

        public OrderService(IOrderRepository orderRepo)
        {
            _orderRepo = orderRepo;
        }

        public async Task<order> GetOrderByCodeAsync(string code)
        {
            var order = await _orderRepo.GetByCodeAsync(code);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public async Task<PagedResultLite<OrderResponseDto>> GetPagedAsync(int page, int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _orderRepo.GetPagedWithFulfillAsync(skip, pageSize + 1);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<OrderResponseDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }
        public async Task<order> GetByIdAsync(int id)
        {
            var order = await _orderRepo.GetByIdAsync(id);
            if (order == null)
            {
                throw new Exception("Order not found");
            }
            return order;
        }
        public Task<OrderDetailDto?> GetDetailAsync(int id, CancellationToken ct = default)
            => _orderRepo.GetDetailByIdAsync(id, ct);

        public Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(int page, int pageSize, CancellationToken ct = default)
            => _orderRepo.GetAllMissingMaterialsAsync(page, pageSize, ct);

        public Task<string> DeleteDesignFilePath(int orderRequestId)
        {
            return _orderRepo.DeleteDesignFilePath(orderRequestId);
        }

        public Task<List<order>> GetAllOrderWithStatusInProcess()
        {
            return _orderRepo.GetAllOrderInprocessStatus();
        }
    }
}
