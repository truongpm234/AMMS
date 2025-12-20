using AMMS.Infrastructure.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface ICostEstimateRepository
    {
        Task AddAsync(cost_estimate entity);
        Task SaveChangesAsync();
        Task<cost_estimate?> GetByOrderRequestIdAsync(int orderRequestId);
        Task<cost_estimate?> GetByIdAsync(int id);
        Task UpdateAsync(cost_estimate entity);
    }
}

