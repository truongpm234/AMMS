using AMMS.Application.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UploadsController : ControllerBase
    {
        private readonly IUploadFileService _uploadService;

        public UploadsController(IUploadFileService uploadService)
        {
            _uploadService = uploadService;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File is empty");

            using var stream = file.OpenReadStream();

            var url = await _uploadService.UploadAsync(
                stream,
                file.FileName,
                file.ContentType,
                "uploads");

            return Ok(new { url });
        }
    }
}
