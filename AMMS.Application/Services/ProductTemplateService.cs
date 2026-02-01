using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
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

        public async Task<List<ProductTemplateDto>> GetByProductTypeIdAsync(int productTypeId, CancellationToken ct = default)
        {
            var templates = await _repo.GetByProductTypeIdAsync(productTypeId, ct);
            var papers = await _repo.GetPaperMaterialsStockDescAsync(ct);
            var suggested = papers.FirstOrDefault();

            return templates.Select(x => new ProductTemplateDto
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

                paper_code = suggested?.code ?? x.paper_code,
                paper_name = suggested?.name ?? x.paper_name,

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

        public async Task<ProductTemplatesWithPaperStockResponse> GetByProductTypeIdWithPaperStockAsync(CancellationToken ct = default)
        {
            var papers = await _repo.GetPaperMaterialsStockDescAsync(ct);
            var suggested = papers.FirstOrDefault();

            var dtoPapers = papers.Select(p => new PaperStockDto
            {
                material_id = p.material_id,
                code = p.code,
                name = p.name,
                unit = p.unit,
                stock_qty = p.stock_qty ?? 0m,
                cost_price = p.cost_price
            }).ToList();

            return new ProductTemplatesWithPaperStockResponse
            {
                paper_stock = dtoPapers
            };
        }

        public async Task<List<product_template>> GetAllAsync(CancellationToken ct = default)
        {
            return await _repo.GetAllAsync(ct);
        }
    }
}
