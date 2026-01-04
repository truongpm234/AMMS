using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.ProductTypes;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class ProductTypeRepository : IProductTypeRepository
    {
        private readonly AppDbContext _db;

        public ProductTypeRepository(AppDbContext db)
        {
            _db = db;
        }

        public Task<List<product_type>> GetAllAsync()
            => _db.product_types.AsNoTracking().ToListAsync();

        public Task<product_type?> GetByCodeAsync(string code)
            => _db.product_types.AsNoTracking().FirstOrDefaultAsync(x => x.code == code);

        public async Task<ProductTypeDetailDto?> GetProductTypeDetailAsync(int productTypeId, CancellationToken ct = default)
        {
            // 1) product type
            var pt = await _db.product_types
                .AsNoTracking()
                .Where(x => x.product_type_id == productTypeId)
                .Select(x => new { x.product_type_id, x.code, x.name, x.description })
                .FirstOrDefaultAsync(ct);

            if (pt == null) return null;

            var templates = await _db.product_templates
                .AsNoTracking()
                .Where(t => t.product_type_id == productTypeId && t.is_active == true)
                .OrderBy(t => t.template_code)
                .Select(t => new ProductTemplateDto
                {
                    design_profile_id = t.design_profile_id,
                    template_code = t.template_code,
                    template_name = t.template_name,

                    product_length_mm = t.product_length_mm,
                    product_width_mm = t.product_width_mm,
                    product_height_mm = t.product_height_mm,
                    glue_tab_mm = t.glue_tab_mm,
                    bleed_mm = t.bleed_mm,

                    print_width_mm = t.print_width_mm,
                    print_height_mm = t.print_height_mm,

                    coating_type = t.coating_type,
                    wave_type = t.wave_type,
                    number_of_plates = t.number_of_plates,

                    is_active = t.is_active
                })
                .ToListAsync(ct);

            // 3) processes (join cost rule + machines)
            // ✅ dùng DbSet: process_cost_rules
            var processes = await (
                from p in _db.product_type_processes.AsNoTracking()
                where p.product_type_id == productTypeId && (p.is_active ?? true) == true

                join cr in _db.process_cost_rules.AsNoTracking()
                    on p.process_code equals cr.process_code into crj
                from cr in crj.DefaultIfEmpty()

                join mc in _db.machines.AsNoTracking()
                    on p.machine equals mc.machine_code into mcj
                from mc in mcj.DefaultIfEmpty()

                orderby p.seq_num
                select new ProductTypeProcessDto
                {
                    process_id = p.process_id,
                    seq_num = p.seq_num,
                    process_name = p.process_name,
                    process_code = p.process_code,
                    machine_code = p.machine,

                    unit = cr != null ? cr.unit : null,
                    unit_price = cr != null ? cr.unit_price : (decimal?)null,

                    machine_quantity = mc != null ? mc.quantity : null,
                    capacity_per_hour = mc != null ? mc.capacity_per_hour : null,
                    efficiency_percent = mc != null ? mc.efficiency_percent : (decimal?)null
                }
            ).ToListAsync(ct);

            return new ProductTypeDetailDto
            {
                product_type_id = pt.product_type_id,
                code = pt.code,
                name = pt.name,
                description = pt.description,
                templates = templates,
                processes = processes
            };
        }
    }
}
