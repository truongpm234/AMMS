using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class OrderMaterialService : IOrderMaterialService
    {
        private readonly IOrderMaterialRepository _repository;
        public OrderMaterialService(IOrderMaterialRepository orderMaterialRepository)
        {
            _repository = orderMaterialRepository;
        }

        public async Task<OrderMaterialsResponse?> GetMaterialsByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _repository.GetMaterialsByOrderIdAsync(orderId, ct);
        }
    }
}

