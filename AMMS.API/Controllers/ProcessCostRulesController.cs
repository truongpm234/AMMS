using AMMS.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProcessCostRulesController : ControllerBase
    {
        private readonly IProcessCostRuleService _processCostRuleService;
        public ProcessCostRulesController(IProcessCostRuleService processCostRuleService)
        {
            _processCostRuleService = processCostRuleService;
        }

        [HttpGet("get-all-process-cost-rules")]
        public async Task<IActionResult> GetAllProcessCostRules()
        {
            var rules = await _processCostRuleService.GetAllAsync();
            return Ok(rules);
        }
    }
}
