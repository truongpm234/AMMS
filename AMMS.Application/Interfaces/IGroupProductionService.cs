using AMMS.Shared.DTOs.Productions.Groups;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IGroupProductionService
    {
        Task<List<GroupProductionCandidateDto>> GetCandidatesAsync(
            int? productTypeId,
            string? processCodes,
            CancellationToken ct = default);

        Task<CreateGroupProductionResponse> CreateAsync(
            CreateGroupProductionRequest req,
            int? managerUserId,
            CancellationToken ct = default);

        Task StartAsync(int groupProdId, CancellationToken ct = default);

        Task<GroupProductionDetailDto?> GetDetailAsync(
            int groupProdId,
            CancellationToken ct = default);
        Task<List<SuggestedGroupProductionDto>> SuggestAsync(
        int? productTypeId,
        CancellationToken ct = default);

        Task<GroupProductionTaskContextDto?> GetTaskContextAsync(
            int taskId,
            CancellationToken ct = default);
    }
}
