using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        public async Task<List<order>> GetAllAsync()
        {
            return await _orderRepo.GetAllAsync();
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
    }
}
