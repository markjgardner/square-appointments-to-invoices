using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Invoicer.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Square;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(s =>
    {
        s.AddSingleton<SecretClient>((sp)=>{ 
            var vaultUri = new Uri(System.Environment.GetEnvironmentVariable("KEYVAULT_URI"));
            return new SecretClient(vaultUri, new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeSharedTokenCacheCredential = true })); 
        });
        s.AddSingleton<ISquareClient>((s) => {
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
         s.AddOptions<SquareAppConfig>().Configure<Microsoft.Extensions.Configuration.IConfiguration>((settings, configuration) =>
        {
            configuration.GetSection(SquareAppConfig.SquareApp).Bind(settings);
        });
    })
    .Build();

host.Run();