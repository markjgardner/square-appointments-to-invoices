using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Square;
using Square.Models;
using Square.Exceptions;

namespace invoicer
{
    public class oAuth
    {
        private ISquareClient _square;
        private SecretClient _secrets;


        public oAuth(ISquareClient squareClient, SecretClient secrets)
        {
            _square = squareClient;
            _secrets = secrets;
        }
        
        [FunctionName("SquareAuth")]
        public async Task<IActionResult> SquareAuth(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req,
            ILogger log)
        {   
            // create and store CSRF token            
            var state = Guid.NewGuid().ToString();
            await _secrets.SetSecretAsync("csrf-token", state);

            var ClientId = System.Environment.GetEnvironmentVariable("SQUARE_APPID");
            var ClientSecret = System.Environment.GetEnvironmentVariable("SQUARE_APPSECRET");
            var SquareScopes = System.Environment.GetEnvironmentVariable("SQUARE_SCOPES");
            var authUri = 
#if DEBUG
                "https://connect.squareupsandbox.com/oauth2/authorize"
#else
                "https://connect.squareup.com/oauth2/authorize"
#endif
                + "?client_id=" + ClientId
                + "&scope=" + SquareScopes
                + "&session=false"
                + "&state=" + state;
            return new RedirectResult(authUri);
        }

        [FunctionName("SquareCallback")]
        public async Task<IActionResult> SquareCallback(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req,
            ILogger log)
        {
            var csrf = await _secrets.GetSecretAsync("csrf-token");
            var code = req.Query["code"][0];;
            var state = req.Query["state"][0];
            if (String.Compare(state, csrf.Value.Value) != 0)
            {
                return new BadRequestResult();
            }

            var ClientId = System.Environment.GetEnvironmentVariable("SQUARE_APPID");
            var ClientSecret = System.Environment.GetEnvironmentVariable("SQUARE_APPSECRET");
            var SquareScopes = System.Environment.GetEnvironmentVariable("SQUARE_SCOPES").Split(" ");
            var request = new ObtainTokenRequest.Builder(ClientId, "authorization_code")
                .ClientSecret(ClientSecret)
                .Code(code)
                .Scopes(SquareScopes)
                .Build(); 

            try {
                var token = await _square.OAuthApi.ObtainTokenAsync(request);
                await _secrets.SetSecretAsync("square-token", token.AccessToken);
                await _secrets.SetSecretAsync("square-refresh-token", token.RefreshToken);
                return new OkResult();
            }
            catch (ApiException ex) {
                log.LogInformation(String.Join("\n", ex.Errors));
                return new BadRequestResult();
            }
        }
        
        //Refresh the token weekly
        [FunctionName("refreshSquareToken")]
        public async Task Run([TimerTrigger("0 30 22 1 * *")]TimerInfo myTimer, ILogger log)
        {
            var ClientId = System.Environment.GetEnvironmentVariable("SQUARE_APPID");
            var ClientSecret = System.Environment.GetEnvironmentVariable("SQUARE_APPSECRET");
            var SquareScopes = System.Environment.GetEnvironmentVariable("SQUARE_SCOPES").Split(" ");
            var refresh = await _secrets.GetSecretAsync("square-refresh-token");
            var request = new ObtainTokenRequest.Builder(ClientId, "refresh_token")
                .ClientSecret(ClientSecret)
                .Scopes(SquareScopes)
                .RefreshToken(refresh.Value.Value)
                .Build();

            try {
                var token = await _square.OAuthApi.ObtainTokenAsync(request);
                await _secrets.SetSecretAsync("square-token", token.AccessToken);
                await _secrets.SetSecretAsync("square-refresh-token", token.RefreshToken);
            }
            catch (ApiException ex) {
                log.LogInformation(String.Join("\n", ex.Errors));
            }
        }
    }
}
