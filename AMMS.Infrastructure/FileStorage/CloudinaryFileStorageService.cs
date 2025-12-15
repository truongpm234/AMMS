using Microsoft.Extensions.Options;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using AMMS.Infrastructure.Configurations;
using AMMS.Infrastructure.Interfaces;

namespace AMMS.Infrastructure.FileStorage
{
    public class CloudinaryFileStorageService : ICloudinaryFileStorageService
    {
        private readonly Cloudinary _cloudinary;

        public CloudinaryFileStorageService(IOptions<CloudinaryOptions> options)
        {
            var account = new Account(options.Value.CloudName, options.Value.ApiKey, options.Value.ApiSecret);
            _cloudinary = new Cloudinary(account);
        }

        public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType, string folder)
        {
            // img
            if (contentType.StartsWith("image/"))
            {
                var imageParams = new ImageUploadParams
                {
                    File = new FileDescription(fileName, fileStream),
                    Folder = folder,
                    UseFilename = true,
                    UniqueFilename = true
                };

                var result = await _cloudinary.UploadAsync(imageParams);
                return result.SecureUrl.ToString();
            }

            // pdf, word, zip,...
            var rawParams = new RawUploadParams
            {
                File = new FileDescription(fileName, fileStream),
                Folder = folder,
                UseFilename = true,
                UniqueFilename = true
            };

            var rawResult = await _cloudinary.UploadAsync(rawParams);
            return rawResult.SecureUrl.ToString();
        }
    }
}
