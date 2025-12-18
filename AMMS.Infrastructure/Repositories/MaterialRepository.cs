using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class MaterialRepository : IMaterialRepository
    {
        private readonly AppDbContext _db;
        public MaterialRepository(AppDbContext db) => _db = db;

        public Task<material?> GetByCodeAsync(string code)
        {
            var c = (code ?? "").Trim().ToLower();
            return _db.materials.AsNoTracking()
                .FirstOrDefaultAsync(m => m.code.ToLower() == c);
        }

        public async Task<List<material>> GetAll()
        {
            return await _db.materials.AsNoTracking().ToListAsync();
        }

        public async Task<material> GetByIdAsync(int id)
        {
            return await _db.materials
                .FirstOrDefaultAsync(m => m.material_id == id);
        }

        public async Task UpdateAsync(material entity)
        {
            _db.materials.Update(entity);
            await Task.CompletedTask;
        }

        public async Task SaveChangeAsync()
        {
            await _db.SaveChangesAsync();
        }

    }
}
