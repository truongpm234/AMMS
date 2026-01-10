using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;

public class GoogleAuthService
{
    private readonly string _clientId;

    public GoogleAuthService(IConfiguration config)
    {
        _clientId = config["GoogleAuth:ClientId"];
    }

    public async Task<GoogleJsonWebSignature.Payload> VerifyToken(string idToken)
    {
        return await GoogleJsonWebSignature.ValidateAsync(
            idToken,
            new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _clientId }
            });
    }
}