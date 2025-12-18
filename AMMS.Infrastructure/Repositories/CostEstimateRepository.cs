using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;

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
            return await Task.FromResult(_db.cost_estimates.FirstOrDefault(ce => ce.order_request_id == orderRequestId));
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

        public async Task UpdateSystemTotalCodeAsync(decimal costTotal, int id)
        {
            var costEstimate = await GetByIdAsync(id);
            if (costEstimate != null)
            {
                costEstimate.system_total_cost = costTotal;
                _db.cost_estimates.Update(costEstimate);
            }
        }
    }
}
