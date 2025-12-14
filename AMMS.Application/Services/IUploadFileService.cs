namespace AMMS.Application.Services
{
    public interface IUploadFileService
    {
        Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, string module);
    }
}