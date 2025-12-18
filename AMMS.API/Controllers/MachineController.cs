using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Machines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MachineController : ControllerBase
    {
        private readonly IMachineService _service;

        public MachineController(IMachineService service)
        {
            _service = service;
        }

        [HttpGet("free-machines")]
        public async Task<IActionResult> GetFreeMachines()
        {
            var result = await _service.GetFreeMachinesAsync();
            return Ok(result);
        }

        [HttpGet("capacity")]
        public async Task<IActionResult> GetCapacity()
        {
            var result = await _service.GetCapacityAsync();
            return Ok(result);
        }
    }
}
