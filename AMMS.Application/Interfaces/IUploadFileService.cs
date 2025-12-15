namespace AMMS.Application.Interfaces
{
    public interface IUploadFileService
    {
        Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, string module);
    }
}