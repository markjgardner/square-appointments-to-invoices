using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Square;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;
using System;

[assembly: FunctionsStartup(typeof(invoicer.Startup))]

namespace invoicer
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton<SecretClient>((sp)=>{ 
                var vaultUri = new Uri(System.Environment.GetEnvironmentVariable("KEYVAULT_URI"));
                return new SecretClient(vaultUri, new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeSharedTokenCacheCredential = true })); 
            });

            builder.Services.AddSingleton<ISquareClient>((s) => {
                var secrets = s.GetService<SecretClient>();
                var token = secrets.GetSecret("square-token");    

                var client = new SquareClient.Builder();
                if (token.Value != null)
                    client = client.AccessToken(token.Value.Value);
#if DEBUG
                client = client.Environment(Square.Environment.Sandbox);
#else
                client = client.Environment(Square.Environment.Production);
#endif
                return client.Build();
            });
        }
    }
}