using AMMS.Application.Interfaces;
using AMMS.Domain;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _repo;

        public OrderService(IOrderRepository repo)
        {
            _repo = repo;
        }

        public Task<CreateCustomerOrderResponse> CreateCustomerOrderAsync(CreateCustomerOrderRequest req)
        {
            return _repo.CreateCustomerOrderAsync(req);
        }
    }
}
