using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
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

        public async Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var result = await _orderRepo.GetAllMissingMaterialsAsync(page, pageSize, ct);

            static decimal RoundUpToTens(decimal value)
            {
                if (value <= 0m) return 0m;
                return Math.Ceiling(value / 10m) * 10m; 
            }

            if (result.Data == null || result.Data.Count == 0)
                return result;

            foreach (var x in result.Data)
            {
                var missingBase = x.quantity;
                if (missingBase < 0m) missingBase = 0m;

                // +10%
                var withBuffer = missingBase * 1.10m;

                var rounded = RoundUpToTens(withBuffer);

                decimal unitPrice = 0m;
                if (missingBase > 0m && x.total_price > 0m)
                {
                    unitPrice = x.total_price / missingBase;
                }

                x.quantity = rounded;
                x.total_price = Math.Round(rounded * unitPrice, 2);
            }

            return result;
        }

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
