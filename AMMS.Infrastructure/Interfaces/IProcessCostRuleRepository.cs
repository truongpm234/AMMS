using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Enums;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IProcessCostRuleRepository
    {
        Task<(decimal unitPrice, string unit, string note)> GetRateAsync(ProcessType p, CancellationToken ct = default);
        Task<List<process_cost_rule>> GetAllAsync();
    }
}