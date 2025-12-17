using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AMMS.Infrastructure.Repositories
{
    public class RequestRepository : IRequestRepository
    {
        private readonly AppDbContext _db;

        public RequestRepository(AppDbContext db)
        {
            _db = db;
        }

        public DbContext DbContext => _db;

        public async Task<order_request?> GetByIdAsync(int id)
        {
            return await _db.order_requests
                .FirstOrDefaultAsync(x => x.order_request_id == id);
        }

        public Task UpdateAsync(order_request entity)
        {
            _db.order_requests.Update(entity);
            return Task.CompletedTask;
        }

        public async Task<int> SaveChangesAsync()
        {
            return await _db.SaveChangesAsync();
        }

        public async Task AddAsync(order_request entity)
        {
            await _db.order_requests.AddAsync(entity);
        }

        public async Task DeleteAsync(int id)
        {
            var entity = await _db.order_requests.FindAsync(id);
            if (entity != null)
                _db.order_requests.Remove(entity);
        }
        public Task<int> CountAsync()
        {
            return _db.order_requests.AsNoTracking().CountAsync();
        }

        public Task<List<order_request>> GetPagedAsync(int skip, int takePlusOne)
        {
            return _db.order_requests
                .AsNoTracking()
                .OrderByDescending(x => x.order_request_date)
                .Skip(skip)
                .Take(takePlusOne)
                .ToListAsync();
        }
    }
    }