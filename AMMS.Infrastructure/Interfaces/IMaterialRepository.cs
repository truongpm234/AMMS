using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IMaterialRepository
    {
        Task<material?> GetByCodeAsync(string code);
        Task<List<material>> GetAll();
        Task<material> GetByIdAsync(int id);
        Task UpdateAsync(material entity);
        Task SaveChangeAsync();
        Task<List<material>> GetMaterialByTypeSongAsync();
        Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
            int page, int pageSize, CancellationToken ct = default);
        Task<MaterialTypePaperDto> GetAllPaperTypeAsync();
    }
}
