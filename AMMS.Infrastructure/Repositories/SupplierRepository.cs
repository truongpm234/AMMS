using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class SupplierRepository : ISupplierRepository
    {
        private readonly AppDbContext _db;

        public SupplierRepository(AppDbContext db)
        {
            _db = db;
        }

        public Task<int> CountAsync(CancellationToken ct = default)
            => _db.suppliers.AsNoTracking().CountAsync(ct);

        // ✅ Lấy danh sách supplier + materials có main_material_type trùng nhau
        public async Task<List<SupplierLiteDto>> GetPagedAsync(
    int skip, int take, CancellationToken ct = default)
        {
            return await _db.suppliers
                .AsNoTracking()
                .OrderBy(s => s.name)
                .Skip(skip)
                .Take(take)
                .Select(s => new SupplierLiteDto
                {
                    SupplierId = s.supplier_id,
                    Name = s.name,
                    ContactPerson = s.contact_person,
                    Phone = s.phone,
                    Email = s.email,
                    MainMaterialType = s.main_material_type,
                    Rating = s.rating
                })
                .ToListAsync(ct);
        }


        public async Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
    int supplierId, int page, int pageSize, CancellationToken ct = default)
        {
            page = page <= 0 ? 1 : page;
            pageSize = pageSize <= 0 ? 10 : pageSize;

            var supplier = await _db.suppliers.AsNoTracking()
                .Where(s => s.supplier_id == supplierId)
                .Select(s => new
                {
                    s.supplier_id,
                    s.name,
                    s.contact_person,
                    s.phone,
                    s.email,
                    s.main_material_type
                })
                .FirstOrDefaultAsync(ct);

            if (supplier == null) return null;

            // ✅ baseQuery: thêm cost_price
            var baseQuery =
                from sm in _db.supplier_materials.AsNoTracking()
                where sm.supplier_id == supplierId
                join m in _db.materials.AsNoTracking()
                    on sm.material_id equals m.material_id
                select new
                {
                    m.material_id,
                    m.code,
                    m.name,
                    m.unit,
                    UnitPrice = m.cost_price,  // ✅ đơn giá từ material
                    sm.is_active,
                    sm.note
                };

            var totalCount = await baseQuery.CountAsync(ct);

            var items = await baseQuery
                .OrderBy(x => x.name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new SupplierMaterialDto(
                    x.material_id,
                    x.code,
                    x.name,
                    x.unit,
                    x.UnitPrice,
                    x.is_active,
                    x.note
                ))
                .ToListAsync(ct);

            return new SupplierDetailDto
            {
                supplier_id = supplier.supplier_id,
                name = supplier.name,
                contact_person = supplier.contact_person,
                phone = supplier.phone,
                email = supplier.email,
                main_material_type = supplier.main_material_type,
                Materials = new PagedResultLite<SupplierMaterialDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = (page * pageSize) < totalCount,
                    Data = items
                }
            };
        }
        public async Task<List<SupplierByMaterialIdDto>> ListSupplierByMaterialId(int id)
        {
            var listSuppliers = await _db.suppliers
            .Join(_db.supplier_materials,
                 s => s.supplier_id,
                 sm => sm.supplier_id,
                 (s, sm) => new { s, sm })
            .Where(x => x.sm.material_id == id && x.sm.is_active)
            .Select(x => new SupplierByMaterialIdDto
            {
                supplier_id = x.s.supplier_id,
                name = x.s.name,
                email = x.s.email,
                phone = x.s.phone,
                price = x.sm.unit_price,
                contact_person = x.s.contact_person,
                rating = x.s.rating
            })
            .ToListAsync();
            return listSuppliers;
        }
    }
}