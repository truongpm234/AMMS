using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.SubProduct;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class SubProductRepository : ISubProductRepository
    {
        private readonly AppDbContext _db;

        public SubProductRepository(AppDbContext db)
        {
            _db = db;
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        public Task<sub_product?> GetByIdAsync(int id, CancellationToken ct = default)
            => _db.sub_products
                .AsNoTracking()
                .Include(x => x.product_type)
                .FirstOrDefaultAsync(x => x.id == id, ct);

        public async Task<PagedResultLite<SubProductDto>> GetPagedAsync(
    int page,
    int pageSize,
    bool? isActive = null,
    bool? isImported = null,
    CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            var query = _db.sub_products
                .AsNoTracking()
                .Include(x => x.product_type)
                .AsQueryable();

            if (isActive.HasValue)
                query = query.Where(x => x.is_active == isActive.Value);

            if (isImported.HasValue)
                query = query.Where(x => x.is_imported == isImported.Value);

            var rows = await query
                .OrderBy(x => x.is_imported)
                .ThenBy(x => x.is_active)
                .ThenByDescending(x => x.updated_at)
                .ThenByDescending(x => x.id)
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(x => new SubProductDto
                {
                    id = x.id,
                    product_type_id = x.product_type_id,
                    product_type_name = x.product_type != null ? x.product_type.name : null,
                    width = x.width,
                    length = x.length,
                    product_process = x.product_process,
                    quantity = x.quantity,
                    is_active = x.is_active,
                    is_imported = x.is_imported,
                    import_file = x.import_file,
                    source_task_id = x.source_task_id,
                    source_task_log_id = x.source_task_log_id,
                    source_prod_id = x.source_prod_id,
                    source_order_id = x.source_order_id,
                    description = x.description,
                    updated_at = x.updated_at
                })
                .ToListAsync(ct);

            var hasNext = rows.Count > pageSize;
            if (hasNext) rows.RemoveAt(rows.Count - 1);

            return new PagedResultLite<SubProductDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = rows
            };
        }

        public Task<sub_product?> GetByIdTrackingAsync(int id, CancellationToken ct = default)
    => _db.sub_products
        .Include(x => x.product_type)
        .FirstOrDefaultAsync(x => x.id == id, ct);

        public Task<bool> ProductTypeExistsAsync(int productTypeId, CancellationToken ct = default)
            => _db.product_types
                .AsNoTracking()
                .AnyAsync(x => x.product_type_id == productTypeId, ct);

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);

        public async Task AddAsync(sub_product entity, CancellationToken ct = default)
    => await _db.sub_products.AddAsync(entity, ct);
    }
}
