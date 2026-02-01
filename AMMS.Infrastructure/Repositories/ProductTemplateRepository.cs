using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class ProductTemplateRepository : IProductTemplateRepository
    {
        private readonly AppDbContext _db;

        public ProductTemplateRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<product_template>> GetByProductTypeIdAsync(
            int productTypeId,
            CancellationToken ct = default)
        {
            return await _db.product_templates
                .Where(x => x.product_type_id == productTypeId && x.is_active == true)
                .OrderBy(x => x.template_code)
                .ToListAsync(ct);
        }

        public async Task<List<product_template>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.product_templates
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.template_code)
                .ToListAsync(ct);
        }
        public Task<List<material>> GetPaperMaterialsStockDescAsync(CancellationToken ct = default)
        {
            return _db.materials
                .AsNoTracking()
                .Where(m => m.material_class == "MAIN" && m.type == "GIẤY")
                .OrderByDescending(m => m.stock_qty ?? 0m)
                .ThenBy(m => m.code)
                .ToListAsync(ct);
        }
    }
}
