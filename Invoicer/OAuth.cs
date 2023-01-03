using System;
using System.Threading.Tasks;
using System.Text;
using Azure.Security.KeyVault.Secrets;
using Invoicer.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Square;
using Square.Models;
using Square.Exceptions;
using Microsoft.Extensions.Options;

namespace Invoicer
{
    public class OAuth
    {
        private readonly ILogger _logger;
        private ISquareClient _square;
        private SecretClient _secrets;
        private readonly SquareAppConfig _appConfig;


        public OAuth(ISquareClient squareClient, SecretClient secrets, IOptions<SquareAppConfig> config, ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<OAuth>();
            _square = squareClient;
            _secrets = secrets;
            _appConfig = config.Value;
        }
        
        [Function("SquareAuth")]
        public async Task<HttpResponseData> SquareAuth(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {   
            // create and store CSRF token            
            var state = Guid.NewGuid().ToString();
            await _secrets.SetSecretAsync("csrf-token", state);
            
            var authUri = new StringBuilder(_appConfig.SquareEndpoint);
            authUri.Append("/oauth2/authorize");
            authUri.Append("?client_id=" + _appConfig.SquareAppId);
            authUri.Append("&scope=" + _appConfig.SquareScopes);
            authUri.Append("&session=false");
            authUri.Append("&state=" + state);

            var resp = req.CreateResponse(System.Net.HttpStatusCode.Redirect);
            resp.Headers.Add("Location", authUri.ToString());
            return resp;
        }

        [Function("SquareCallback")]
        public async Task<HttpResponseData> SquareCallback(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var csrf = await _secrets.GetSecretAsync("csrf-token");
            var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
            if (String.Compare(query["state"], csrf.Value.Value) != 0)
            {
                resp.StatusCode = System.Net.HttpStatusCode.BadRequest;
                return resp;
            }

            var SquareScopes = _appConfig.SquareScopes.Split(" ");
            var request = new ObtainTokenRequest.Builder(_appConfig.SquareAppId, "authorization_code")
                .ClientSecret(_appConfig.SquareAppSecret)
                .Code(query["code"])
                .Scopes(SquareScopes)
                .Build(); 

            try {
                var token = await _square.OAuthApi.ObtainTokenAsync(request);
                await _secrets.SetSecretAsync("square-token", token.AccessToken);
                await _secrets.SetSecretAsync("square-refresh-token", token.RefreshToken);
                return resp;
            }
            catch (ApiException ex) {
                _logger.LogInformation(String.Join("\n", ex.Errors));
                resp.StatusCode = System.Net.HttpStatusCode.BadRequest;
                return resp;
            }
        }
        
        //Refresh the token weekly
        [Function("refreshSquareToken")]
        public async Task Run([TimerTrigger("0 30 22 1 * *")]TimerInfo myTimer)
        {
            
            var SquareScopes = _appConfig.SquareScopes.Split(" ");
            var refresh = await _secrets.GetSecretAsync("square-refresh-token");
            var request = new ObtainTokenRequest.Builder(_appConfig.SquareAppId, "refresh_token")
                .ClientSecret(_appConfig.SquareAppSecret)
                .Scopes(SquareScopes)
                .RefreshToken(refresh.Value.Value)
                .Build();

            try {
                var token = await _square.OAuthApi.ObtainTokenAsync(request);
                await _secrets.SetSecretAsync("square-token", token.AccessToken);
                await _secrets.SetSecretAsync("square-refresh-token", token.RefreshToken);
            }
            catch (ApiException ex) {
                _logger.LogInformation(String.Join("\n", ex.Errors));
            }
        }
    }
}