using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.ProductTemplates;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class ProductTemplateService : IProductTemplateService
    {
        private readonly IProductTemplateRepository _repo;

        public ProductTemplateService(IProductTemplateRepository repo)
        {
            _repo = repo;
        }

        public async Task<List<ProductTemplateDto>> GetByProductTypeIdAsync(
            int productTypeId,
            CancellationToken ct = default)
        {
            var entities = await _repo.GetByProductTypeIdAsync(productTypeId, ct);

            return entities.Select(x => new ProductTemplateDto
            {
                design_profile_id = x.design_profile_id,
                product_type_id = x.product_type_id,
                template_code = x.template_code,
                template_name = x.template_name,
                description = x.description,

                product_length_mm = x.product_length_mm,
                product_width_mm = x.product_width_mm,
                product_height_mm = x.product_height_mm,
                glue_tab_mm = x.glue_tab_mm,
                bleed_mm = x.bleed_mm,
                is_one_side_box = x.is_one_side_box,

                number_of_plates = x.number_of_plates,
                coating_type = x.coating_type,

                paper_code = x.paper_code,
                paper_name = x.paper_name,
                wave_type = x.wave_type,

                print_width_mm = x.print_width_mm,
                print_height_mm = x.print_height_mm,

                production_processes = x.production_processes,
                default_quantity = x.default_quantity,

                is_active = x.is_active,
                created_at = x.created_at,
                updated_at = x.updated_at
            }).ToList();
        }
    }
}
