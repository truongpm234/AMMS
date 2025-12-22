using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Materials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class MaterialService : IMaterialService
    {
        private readonly IMaterialRepository _materialRepository;

        public MaterialService(IMaterialRepository materialRepository)
        {
            _materialRepository = materialRepository;
        }

        public async Task<List<material>> GetAllAsync()
        {
            return await _materialRepository.GetAll();
        }

        public async Task<material?> GetByIdAsync(int id)
        {
            return await _materialRepository.GetByIdAsync(id);
        }

        public async Task UpdateAsync(material material)
        {
            await _materialRepository.GetByIdAsync(material.material_id);
            await _materialRepository.UpdateAsync(material);
            await _materialRepository.SaveChangeAsync();
        }
        public Task<List<string>> GetAllPaperTypeAsync()
        {
            var result = Enum.GetNames(typeof(PaperCode)).ToList();
            return Task.FromResult(result);
        }
        public Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
            int page, int pageSize, CancellationToken ct = default) => 
            _materialRepository.GetShortageForAllOrdersPagedAsync(page, pageSize, ct);
    }
}
