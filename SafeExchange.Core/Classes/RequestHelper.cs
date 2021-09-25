/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Newtonsoft.Json;
    using System.IO;
    using System.Threading.Tasks;

    public static class RequestHelper
    {
        public static async Task<dynamic> GetRequestDataAsync(HttpRequest req)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            return data;
        }
    }
}
