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
    }
}
