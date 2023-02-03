namespace Invoicer.Models 
{
    public class SquareAppConfig 
    {
        public const string SquareApp = "SQUARE_APP";
        public string SquareAppId { get; set; }
        public string SquareAppSecret { get; set; }
        public string SquareScopes { get; set; }
        public string SquareEndpoint { get; set; }

    }
}