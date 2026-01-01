using AMMS.Shared.DTOs.ProductTemplates;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IProductTemplateService
    {
        Task<List<ProductTemplateDto>> GetByProductTypeIdAsync(int productTypeId, CancellationToken ct = default);
    }
}
