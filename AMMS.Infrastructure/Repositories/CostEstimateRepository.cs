using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Requests;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class CostEstimateRepository : ICostEstimateRepository
    {
        private readonly AppDbContext _db;

        public CostEstimateRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(cost_estimate entity)
        {
            await _db.cost_estimates.AddAsync(entity);
        }

        public async Task SaveChangesAsync()
        {
            await _db.SaveChangesAsync();
        }

        public async Task<cost_estimate?> GetByOrderRequestIdAsync(int orderRequestId)
        {
            return await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == orderRequestId)
                .OrderByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync();
        }

        public async Task<cost_estimate?> GetByIdAsync(int id)
        {
            return await _db.cost_estimates.FindAsync(id);
        }

        public async Task UpdateAsync(cost_estimate entity)
        {
            _db.cost_estimates.Update(entity);
            await Task.CompletedTask;
        }

        public async Task<DepositByRequestResponse?> GetDepositByRequestIdAsync(int requestId, CancellationToken ct = default)
        {
            return await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == requestId)
                .OrderByDescending(x => x.created_at)
                .ThenByDescending(x => x.estimate_id)
                .Select(x => new DepositByRequestResponse
                {
                    order_request_id = x.order_request_id,
                    deposit_amount = x.deposit_amount
                })
                .FirstOrDefaultAsync(ct);
        }
    }
}
