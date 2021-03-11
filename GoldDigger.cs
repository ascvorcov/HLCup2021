using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace GoldDigger
{
    public class Api 
    {
        public readonly Stats[] Stats = { new Stats(), new Stats(), new Stats(), new Stats() };

        private readonly HttpClient _httpClient;

        private readonly Uri _healthCheck;
        private readonly Uri _licenses;
        private readonly Uri _explore;
        private readonly Uri _dig;
        private readonly Uri _cash;
    
        private readonly MediaTypeHeaderValue _contentType = new MediaTypeHeaderValue("application/json");
        private readonly MediaTypeWithQualityHeaderValue _header = new MediaTypeWithQualityHeaderValue("application/json");
        private readonly License noMoreLicenseError = new License {digAllowed = -1};

        public Api(string baseUrl, HttpClient httpClient)
        {
            _httpClient = httpClient;
            _healthCheck = new Uri(baseUrl.TrimEnd('/') + "/health-check");
            _licenses = new Uri(baseUrl.TrimEnd('/') + "/licenses");
            _explore = new Uri(baseUrl.TrimEnd('/') + "/explore");
            _dig = new Uri(baseUrl.TrimEnd('/') + "/dig");
            _cash = new Uri(baseUrl.TrimEnd('/') + "/cash");
        }
        
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <returns>Extra details about service status, if any.</returns>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetAsync(_healthCheck, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <param name="money">Amount of money to spend for a license. Empty array for get free license. Maximum 10 active licenses</param>
        /// <returns>Issued license.</returns>
        public Task<License> IssueLicenseAsync(int[] money, CancellationToken cancellationToken) => PostAsync<int[], License>(0, _licenses, money, noMoreLicenseError, cancellationToken);

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <param name="area">Area to be explored.</param>
        /// <returns>Report about found treasures.</returns>
        public Task<Report> ExploreAreaAsync(Area area, CancellationToken cancellationToken) => PostAsync<Area, Report>(1, _explore, area, null, cancellationToken);

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <param name="dig">License, place and depth to dig.</param>
        /// <returns>List of treasures found.</returns>
        public Task<string[]> DigAsync(Dig dig, CancellationToken cancellationToken) => PostAsync(2, _dig, dig, Array.Empty<string>(), cancellationToken);

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <param name="treasure">Treasure for exchange.</param>
        /// <returns>Payment for treasure.</returns>
        public Task<int[]> CashAsync(string treasure, CancellationToken cancellationToken) => PostAsync<string, int[]>(3, _cash, treasure, null, cancellationToken);

        private async Task<TOut> PostAsync<TIn, TOut>(int stats, Uri target, TIn obj, TOut custom, CancellationToken token)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, target);

            //var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(obj));
            var content = new ByteArrayContent(Utf8Json.JsonSerializer.Serialize(obj));

            content.Headers.ContentType = _contentType;
            message.Content = content;
            message.Headers.Accept.Add(_header);

            using var response =
                await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode)
            {
                Stats[stats].Success();
                return Utf8Json.JsonSerializer.Deserialize<TOut>(await response.Content.ReadAsStreamAsync(token));
                //return Newtonsoft.Json.JsonConvert.DeserializeObject<TOut>(await response.Content.ReadAsStringAsync(token));
            }

            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Conflict)
            {
                return custom;
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                App.Log("Error:" + await response.Content.ReadAsStringAsync(token));
            }

            Stats[stats].Fail();
            return default;
        }
    }

    public sealed class License 
    {
        public int id { get; set; }
    
        public int digAllowed { get; set; }
    
        public int digUsed { get; set; }
    }
    
    public sealed class Area 
    {
        public int posX { get; set; }
    
        public int posY { get; set; }
    
        public int? sizeX { get; set; }
    
        public int? sizeY { get; set; }
    }
    
    public sealed class Report 
    {
        public Area area { get; set; }
    
        public int amount { get; set; }
    }
    
    public sealed class Dig 
    {
        public int licenseID { get; set; }
    
        public int posX { get; set; }
    
        public int posY { get; set; }
    
        public int depth { get; set; }
    }
}
